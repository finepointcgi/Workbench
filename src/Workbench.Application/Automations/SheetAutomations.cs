using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Application.Notifications;
using Workbench.Application.Sheets;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Automations;

/// <summary>
/// For features that add or change many rows at once (the Excel plan import): runs the sheet's automations on every row the
/// change added or changed, as one batch. Notifications and emails are collected while it runs and sent once per automation and
/// person at the end (a summary naming the rows), so an import of 200 rows doesn't send 200 notifications.
/// <code>
/// var before = await sheetAutomations.CaptureAsync(sheetId, ct);
/// ... write the rows ...
/// await sheetAutomations.RunBatchAsync(before, "the Excel import", ct);
/// </code>
/// </summary>
public sealed class SheetAutomations(IWorkbenchDbContextFactory dbFactory, SheetAutomationRuntime runtime, Notifier notifier, IEmailSender email,
    AutomationItemServiceAccessor accessor, IItemService items, ICurrentUser currentUser)
{
    /// <summary>The rows as they are now, to compare with after the change.</summary>
    public async Task<SheetRowsSnapshot> CaptureAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        return new SheetRowsSnapshot(sheetId, rows.Select(r => r.ToDto()).ToDictionary(r => r.Id));
    }

    /// <summary>
    /// Runs automations for rows added (item created) or changed (field changes) since <paramref name="before"/>, then sends the
    /// batch's notifications. <paramref name="source"/> names the change in the summaries ("the Excel import").
    /// </summary>
    public async Task<SheetBatchResult> RunBatchAsync(SheetRowsSnapshot before, string source, CancellationToken cancellationToken = default)
    {
        // Act through the automation decorator (inside the permission checks), like a person's change does.
        var through = accessor.Instance ?? items;
        IReadOnlyList<ItemDto> after;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            after = (await db.Items.AsNoTracking().Where(i => i.SheetId == before.SheetId).OrderBy(i => i.Order).ToListAsync(cancellationToken)).Select(i => i.ToDto()).ToList();
        }

        var added = 0;
        var changed = 0;
        using var batch = SheetAutomationBatch.Begin(source);
        using (SheetAutomationRuntime.ForActor(currentUser.Email))
        {
            foreach (var row in after)
            {
                if (!before.Rows.TryGetValue(row.Id, out var old))
                {
                    added++;
                    await runtime.OnItemCreatedAsync(through, before.SheetId, row);
                }
                else if (!SameRow(old, row))
                {
                    changed++;
                    await runtime.OnItemUpdatedAsync(through, before.SheetId, old, row);
                }
            }
        }

        var sent = await batch.FlushAsync(notifier, email);
        return new SheetBatchResult(added, changed, sent);
    }

    private static bool SameRow(ItemDto a, ItemDto b) =>
        a.Title == b.Title && a.Start == b.Start && a.Finish == b.Finish
        && a.Values.Count == b.Values.Count && a.Values.All(v => b.Values.TryGetValue(v.Key, out var other) && other == v.Value);
}

public sealed record SheetRowsSnapshot(Guid SheetId, IReadOnlyDictionary<Guid, ItemDto> Rows);

/// <param name="Notifications">Notifications and emails sent at the end (one per automation and person).</param>
public sealed record SheetBatchResult(int RowsAdded, int RowsChanged, int Notifications);

/// <summary>Holds the automation item decorator of the current scope, so batch runs act through it rather than the permission-checked service.</summary>
public sealed class AutomationItemServiceAccessor
{
    public IItemService? Instance { get; set; }
}

/// <summary>
/// Where a new row came from. The forms work wraps the row it adds:
/// <code>using (SheetAutomationEvents.AddedByForm(form.Name)) { await items.CreateAsync(sheetId, request); }</code>
/// so automations with the "added by a form" trigger (<see cref="Contracts.Automations.RecordEventKind.AddedByForm"/>, event
/// <see cref="Contracts.Automations.AutomationSheets.ItemAddedByFormEvent"/>) run, besides "item created" ones.
/// </summary>
public static class SheetAutomationEvents
{
    private static readonly AsyncLocal<string?> Form = new();

    /// <summary>The form adding rows in this flow, or null.</summary>
    public static string? CurrentForm => Form.Value;

    public static IDisposable AddedByForm(string formName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formName);
        var previous = Form.Value;
        Form.Value = formName.Trim();
        return new Restore(() => Form.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>
/// Collects what sheet automations would notify or email while a batch runs (see <see cref="SheetAutomations"/>); flushed once at
/// the end: one notification per automation and person, naming the rows (or the single original one when there was just one).
/// </summary>
public sealed class SheetAutomationBatch : IDisposable
{
    private static readonly AsyncLocal<SheetAutomationBatch?> Active = new();
    private readonly SheetAutomationBatch? _previous;
    private readonly string _source;
    private readonly Dictionary<(Guid Automation, string Recipient, string Kind), Pending> _notes = [];
    private readonly Dictionary<(Guid Automation, string Recipient), PendingEmail> _emails = [];

    private SheetAutomationBatch(string source)
    {
        _source = source;
        _previous = Active.Value;
        Active.Value = this;
    }

    public static SheetAutomationBatch? Current => Active.Value;

    public static SheetAutomationBatch Begin(string source) => new(source);

    /// <summary>Queues a notification for each valid recipient; returns them.</summary>
    public IReadOnlyList<string> Notify(Guid automationId, string automationName, Guid sheetId, string sheetName, IEnumerable<string?> recipients, NotificationRequest request, string row)
    {
        var emails = recipients.Select(Notifier.Normalize).OfType<string>().Distinct().ToList();
        foreach (var to in emails)
        {
            var key = (automationId, to, request.Kind);
            if (!_notes.TryGetValue(key, out var pending))
            {
                _notes[key] = pending = new Pending(automationName, sheetId, sheetName, request);
            }

            pending.Rows.Add(row);
        }

        return emails;
    }

    public void Email(Guid automationId, string automationName, string sheetName, IReadOnlyList<string> recipients, string subject, string text, string row)
    {
        foreach (var to in recipients)
        {
            if (!_emails.TryGetValue((automationId, to), out var pending))
            {
                _emails[(automationId, to)] = pending = new PendingEmail(automationName, sheetName, subject, text);
            }

            pending.Rows.Add(row);
        }
    }

    /// <summary>Sends the collected notifications and emails. Returns how many were sent.</summary>
    public async Task<int> FlushAsync(Notifier notifier, IEmailSender email)
    {
        var sent = 0;
        foreach (var ((_, to, _), pending) in _notes)
        {
            var request = pending.Rows.Count == 1 ? pending.First : pending.First with
            {
                Title = $"{pending.Automation}: {pending.Rows.Count} rows from {_source}",
                Body = $"{pending.Automation} ran on {pending.Rows.Count} rows of {pending.SheetName} from {_source}: {List(pending.Rows)}.",
                ItemId = null,
            };
            sent += (await notifier.NotifyAsync([to], request)).Count;
        }

        foreach (var ((_, to), pending) in _emails)
        {
            var (subject, text) = pending.Rows.Count == 1 ? (pending.Subject, pending.Text)
                : ($"{pending.Automation}: {pending.Rows.Count} rows from {_source}", $"{pending.Automation} ran on {pending.Rows.Count} rows of {pending.SheetName} from {_source}: {List(pending.Rows)}.");
            await email.SendAsync(new EmailMessage(to, subject, text, System.Net.WebUtility.HtmlEncode(text)));
            sent++;
        }

        _notes.Clear();
        _emails.Clear();
        return sent;
    }

    private static string List(List<string> rows) =>
        string.Join(", ", rows.Take(10).Select(r => $"\"{r}\"")) + (rows.Count > 10 ? $" and {rows.Count - 10} more" : "");

    public void Dispose() => Active.Value = _previous;

    private sealed record Pending(string Automation, Guid SheetId, string SheetName, NotificationRequest First)
    {
        public List<string> Rows { get; } = [];
    }

    private sealed record PendingEmail(string Automation, string SheetName, string Subject, string Text)
    {
        public List<string> Rows { get; } = [];
    }
}
