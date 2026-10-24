using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;
using Workbench.Application.Integrations;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Forms;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Forms;
using Workbench.Domain.Integrations;
using DomainAudience = Workbench.Domain.Forms.FormAudience;
using FormAudience = Workbench.Contracts.Forms.FormAudience;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Forms;

/// <summary>
/// Forms that add a row to a sheet. Building one needs Editor on the sheet; making it public needs the workspace's Owner
/// role and <see cref="Permission.SheetFormsPublic"/>. A response adds the row through the normal item pipeline (activity,
/// live updates, the sheet's "row added" rules), raises <see cref="IntegrationEventType.SheetFormSubmitted"/> and notifies
/// the form's creator. People filling in a form never read the sheet: they get only the form's fields.
/// </summary>
public sealed class FormService(
    IWorkbenchDbContextFactory dbFactory,
    TimeProvider clock,
    AccessControl access,
    IItemService items,
    FormSubmissionGrant grant,
    INotificationPublisher notifications,
    ILogger<FormService>? logger = null) : IFormService, IPublicFormService
{
    public const int MaxFields = 60;
    public const int MaxAnswerLength = 4_000;

    /// <summary>Public responses: per client (address hash) per form in <see cref="Window"/>, and per form per hour.</summary>
    public const int PublicPerClient = 5;
    public const int PublicPerFormPerHour = 200;

    /// <summary>Signed-in responses per person per form in <see cref="Window"/>.</summary>
    public const int SignedInPerPerson = 30;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private const string DefaultThanks = "Thanks! Your response was recorded.";

    // ---- Building forms ----

    public async Task<IReadOnlyList<FormDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var forms = await db.Set<SheetForm>().AsNoTracking().Where(f => f.SheetId == sheetId).OrderBy(f => f.CreatedAtUtc).ToListAsync(cancellationToken);
        var result = new List<FormDto>();
        foreach (var form in forms)
        {
            result.Add(await ToDtoAsync(db, form, cancellationToken));
        }

        return result;
    }

    public async Task<FormDto> CreateAsync(Guid sheetId, SaveFormRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Editor, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        if (request.Audience == FormAudience.Public)
        {
            await RequireCanPublishAsync(sheetId, cancellationToken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var form = new SheetForm
        {
            SheetId = sheetId,
            Title = "",
            PublicToken = NewToken(),
            CreatedBy = access.Email,
            CreatedAtUtc = now,
        };
        await ApplyAsync(db, sheet, form, request, now, cancellationToken);
        db.Set<SheetForm>().Add(form);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, form, cancellationToken);
    }

    public async Task<bool> CanPublishAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireSheetRoleAsync(sheetId, WorkspaceRole.Viewer, cancellationToken);
        return await CanPublishSheetAsync(sheetId, cancellationToken);
    }

    public async Task<FormDto> UpdateAsync(Guid formId, SaveFormRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await RequireEditableAsync(db, formId, cancellationToken);
        if (request.Audience == FormAudience.Public && form.Audience != DomainAudience.Public)
        {
            await RequireCanPublishAsync(form.SheetId, cancellationToken);
        }

        var sheet = await db.Sheets.AsNoTracking().FirstAsync(s => s.Id == form.SheetId, cancellationToken);
        await ApplyAsync(db, sheet, form, request, clock.GetUtcNow().UtcDateTime, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, form, cancellationToken);
    }

    public async Task<FormDto> ResetPublicLinkAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await RequireEditableAsync(db, formId, cancellationToken);
        form.PublicToken = NewToken();
        form.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, form, cancellationToken);
    }

    public async Task DeleteAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await RequireEditableAsync(db, formId, cancellationToken);
        db.Set<SheetForm>().Remove(form);
        await db.Set<FormSubmission>().Where(s => s.FormId == formId).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Filling in, signed in ----

    public async Task<FillFormDto> GetForFillingAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await FindForSignedInAsync(db, formId, cancellationToken);
        return await ToFillAsync(db, form, isPublic: false, cancellationToken);
    }

    public async Task<FormSubmittedDto> SubmitAsync(Guid formId, SubmitFormRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await FindForSignedInAsync(db, formId, cancellationToken);
        var since = clock.GetUtcNow().UtcDateTime - Window;
        var me = access.Email;
        if (await db.Set<FormSubmission>().CountAsync(s => s.FormId == formId && s.SubmittedBy == me && s.SubmittedAtUtc >= since, cancellationToken) >= SignedInPerPerson)
        {
            throw new ArgumentException("You've sent a lot of responses to this form in the last few minutes. Wait a little, then try again.");
        }

        return await AddRowAsync(db, form, request, isPublic: false, clientKey: null, cancellationToken);
    }

    // ---- Filling in, public link ----

    public async Task<FillFormDto?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await FindPublicAsync(db, token, cancellationToken);
        return form is null ? null : await ToFillAsync(db, form, isPublic: true, cancellationToken);
    }

    public async Task<FormSubmittedDto> SubmitAsync(
        string token, SubmitFormRequest request, string? honeypot, string? clientAddress, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var form = await FindPublicAsync(db, token, cancellationToken) ?? throw new WorkbenchNotFoundException("This form isn't available.");
        var now = clock.GetUtcNow().UtcDateTime;
        var clientKey = ClientKey(form.Id, clientAddress);

        if (!string.IsNullOrWhiteSpace(honeypot))
        {
            // A person never sees the hidden field; a bot fills it in. Say thanks, add nothing.
            db.Set<FormSubmission>().Add(new FormSubmission
            {
                FormId = form.Id, SheetId = form.SheetId, ClientKey = clientKey, IsPublic = true, RejectedReason = "honeypot", SubmittedAtUtc = now,
            });
            await db.SaveChangesAsync(cancellationToken);
            logger?.LogInformation("Public form {FormId}: response dropped by the spam check.", form.Id);
            return new FormSubmittedDto(Thanks(form));
        }

        var since = now - Window;
        var hour = now.AddHours(-1);
        var recent = db.Set<FormSubmission>().Where(s => s.FormId == form.Id && s.IsPublic);
        if (await recent.CountAsync(s => s.ClientKey == clientKey && s.SubmittedAtUtc >= since, cancellationToken) >= PublicPerClient
            || await recent.CountAsync(s => s.SubmittedAtUtc >= hour, cancellationToken) >= PublicPerFormPerHour)
        {
            throw new FormRateLimitException();
        }

        return await AddRowAsync(db, form, request, isPublic: true, clientKey, cancellationToken);
    }

    // ---- Shared ----

    private async Task<FormSubmittedDto> AddRowAsync(
        IWorkbenchDbContext db, SheetForm form, SubmitFormRequest request, bool isPublic, string? clientKey, CancellationToken cancellationToken)
    {
        if (!form.IsOpen)
        {
            throw new ArgumentException("This form is closed and isn't taking responses.");
        }

        var sheet = await db.Sheets.AsNoTracking().FirstAsync(s => s.Id == form.SheetId, cancellationToken);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == form.SheetId).ToListAsync(cancellationToken);
        var answers = request.Values ?? new Dictionary<string, string?>();
        string? title = null;
        DateOnly? start = null, finish = null;
        var values = new Dictionary<string, string?>();
        foreach (var field in UsableFields(form, sheet, columns, isPublic))
        {
            var label = LabelOf(field.Field, field.Column);
            var raw = answers.FirstOrDefault(a => string.Equals(a.Key, field.Field.Key, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            if (string.IsNullOrEmpty(raw) || (field.Column?.Type == ColumnType.Checkbox && raw is "false"))
            {
                if (field.Field.Required || field.Field.Key == "title")
                {
                    throw new ArgumentException($"Please fill in \"{label}\".");
                }

                continue;
            }

            if (raw.Length > MaxAnswerLength)
            {
                throw new ArgumentException($"\"{label}\" is too long (at most {MaxAnswerLength:N0} characters).");
            }

            switch (field.Field.Key)
            {
                case "title":
                    title = raw.Length > 500 ? raw[..500] : raw;
                    break;
                case "start":
                    start = ParseDate(raw, label);
                    break;
                case "finish":
                    finish = ParseDate(raw, label);
                    break;
                default:
                    values[field.Column!.Id.ToString()] = Normalize(raw, field.Column, label);
                    break;
            }
        }

        if (start is { } s && finish is { } f && f < s)
        {
            throw new ArgumentException("The finish date can't be before the start date.");
        }

        ItemDto created;
        // The form's row also fires "added by a form" sheet automations (Automations.SheetAutomationEvents).
        using (grant.Allow(form.SheetId))
        using (Automations.SheetAutomationEvents.AddedByForm(form.Title))
        {
            created = await items.CreateAsync(form.SheetId, new CreateItemRequest(title ?? "Form response", Start: start, Finish: finish, Values: values), cancellationToken);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        db.Set<FormSubmission>().Add(new FormSubmission
        {
            FormId = form.Id,
            SheetId = form.SheetId,
            ItemId = created.Id,
            SubmittedBy = isPublic ? null : access.Email,
            ClientKey = clientKey,
            IsPublic = isPublic,
            SubmittedAtUtc = now,
        });
        IntegrationOutbox.Raise(db, IntegrationEventType.SheetFormSubmitted,
            new SheetFormSubmittedEventData(form.Id, form.SheetId, created.Id, form.Title, isPublic), isPublic ? null : access.Email, now);
        await db.SaveChangesAsync(cancellationToken);

        if (form.NotifyOnSubmit && !string.Equals(form.CreatedBy, access.Email, StringComparison.OrdinalIgnoreCase))
        {
            var who = isPublic ? "Someone using the public link" : access.Email;
            await notifications.PublishAsync(form.CreatedBy, NotificationKind, $"New response to \"{form.Title}\"",
                $"{who} added \"{created.Title}\" to {sheet.Name}.", sheetId: form.SheetId, itemId: created.Id);
        }

        return new FormSubmittedDto(Thanks(form));
    }

    /// <summary>The notification kind for form responses.</summary>
    public const string NotificationKind = "form.submitted";

    private static string Thanks(SheetForm form) => string.IsNullOrWhiteSpace(form.ConfirmationMessage) ? DefaultThanks : form.ConfirmationMessage.Trim();

    /// <summary>The form's fields that still work: deleted columns, and ones that became computed or restricted, drop out.</summary>
    private static IEnumerable<(SheetFormField Field, Column? Column)> UsableFields(SheetForm form, Sheet sheet, IReadOnlyList<Column> columns, bool isPublic)
    {
        foreach (var field in form.Fields)
        {
            switch (field.Key)
            {
                case "title":
                    yield return (field, null);
                    break;
                case "start" or "finish" when sheet.SchedulingEnabled:
                    yield return (field, null);
                    break;
                default:
                    if (Guid.TryParse(field.Key, out var id) && columns.FirstOrDefault(c => c.Id == id) is { } column && CanBeField(column, isPublic) is null)
                    {
                        yield return (field, column);
                    }

                    break;
            }
        }
    }

    /// <summary>Why a column can't be on a form (null when it can).</summary>
    private static string? CanBeField(Column column, bool isPublic) => column switch
    {
        { Formula: not null } => $"\"{column.Name}\" is a formula, so it's worked out, not filled in.",
        { Type: ColumnType.Lookup } => $"\"{column.Name}\" is a lookup, so it's worked out, not filled in.",
        { Type: ColumnType.LinkedRow } => $"\"{column.Name}\" links to another sheet's rows, which people filling in a form can't see.",
        { Type: ColumnType.AutoNumber or ColumnType.CreatedBy or ColumnType.CreatedDate or ColumnType.ModifiedBy or ColumnType.ModifiedDate } =>
            $"\"{column.Name}\" is filled in automatically.",
        { IsRestricted: true } => $"\"{column.Name}\" is restricted to Finance and owners, so it can't be on a form.",
        { Type: ColumnType.Person } when isPublic => $"\"{column.Name}\" picks a person, which isn't offered on a public form.",
        _ => null,
    };

    private async Task ApplyAsync(IWorkbenchDbContext db, Sheet sheet, SheetForm form, SaveFormRequest request, DateTime now, CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > 200)
        {
            throw new ArgumentException("Give the form a title (up to 200 characters).");
        }

        var isPublic = request.Audience == FormAudience.Public;
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheet.Id).ToListAsync(cancellationToken);
        var restrictedFormulas = Sheets.RestrictedColumns.Of(columns);
        var fields = new List<SheetFormField>();
        foreach (var field in request.Fields ?? [])
        {
            var key = field.Key?.Trim().ToLowerInvariant() ?? "";
            if (fields.Any(f => f.Key == key))
            {
                continue;
            }

            if (key is "start" or "finish" && !sheet.SchedulingEnabled)
            {
                throw new ArgumentException("Start and finish dates are only on sheets with scheduling.");
            }

            if (key is not ("title" or "start" or "finish"))
            {
                var column = Guid.TryParse(key, out var id) ? columns.FirstOrDefault(c => c.Id == id) : null;
                if (column is null)
                {
                    throw new ArgumentException("A form field points at a column that isn't on this sheet.");
                }

                var problem = CanBeField(column, isPublic) ?? (restrictedFormulas.Contains(column.Id) ? $"\"{column.Name}\" is restricted." : null);
                if (problem is not null)
                {
                    throw new ArgumentException(problem);
                }
            }

            fields.Add(new SheetFormField
            {
                Key = key,
                Label = Clip(field.Label, 200),
                HelpText = Clip(field.HelpText, 1_000),
                Required = field.Required || key == "title",
            });
        }

        if (fields.All(f => f.Key != "title"))
        {
            fields.Insert(0, new SheetFormField { Key = "title", Label = "Title", Required = true });
        }

        if (fields.Count > MaxFields)
        {
            throw new ArgumentException($"A form can have up to {MaxFields} fields.");
        }

        form.Title = title;
        form.Description = Clip(request.Description, 4_000);
        form.ConfirmationMessage = Clip(request.ConfirmationMessage, 2_000);
        form.Fields = fields;
        form.IsOpen = request.IsOpen;
        form.Audience = (DomainAudience)(int)request.Audience;
        form.NotifyOnSubmit = request.NotifyOnSubmit;
        form.UpdatedAtUtc = now;
    }

    private async Task RequireCanPublishAsync(Guid sheetId, CancellationToken cancellationToken)
    {
        if (!await CanPublishSheetAsync(sheetId, cancellationToken))
        {
            throw await access.DenyAsync(
                "Only an owner of this sheet's workspace with the \"Publish public forms\" permission can share a form by public link.", cancellationToken);
        }
    }

    private async Task<bool> CanPublishSheetAsync(Guid sheetId, CancellationToken cancellationToken) =>
        await access.GetSheetRoleAsync(sheetId, cancellationToken) == WorkspaceRole.Owner
        && await access.HasAsync(Permission.SheetFormsPublic, cancellationToken);

    private async Task<SheetForm> RequireEditableAsync(IWorkbenchDbContext db, Guid formId, CancellationToken cancellationToken)
    {
        access.RequireSignedIn();
        var form = await db.Set<SheetForm>().FirstOrDefaultAsync(f => f.Id == formId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Form", formId);
        var role = await access.GetSheetRoleAsync(form.SheetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Form", formId);
        if (role < WorkspaceRole.Editor)
        {
            await access.RequireSheetRoleAsync(form.SheetId, WorkspaceRole.Editor, cancellationToken);
        }

        return form;
    }

    /// <summary>The form, when the signed-in user is in its audience (others get "not found").</summary>
    private async Task<SheetForm> FindForSignedInAsync(IWorkbenchDbContext db, Guid formId, CancellationToken cancellationToken)
    {
        var form = await db.Set<SheetForm>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == formId, cancellationToken);
        if (form is null || (form.Audience == DomainAudience.Workspace && await access.GetSheetRoleAsync(form.SheetId, cancellationToken) is null))
        {
            throw WorkbenchNotFoundException.For("Form", formId);
        }

        return form;
    }

    private static async Task<SheetForm?> FindPublicAsync(IWorkbenchDbContext db, string token, CancellationToken cancellationToken)
    {
        token = token?.Trim() ?? "";
        if (token.Length is < 20 or > 64)
        {
            return null;
        }

        var form = await db.Set<SheetForm>().AsNoTracking().FirstOrDefaultAsync(f => f.PublicToken == token, cancellationToken);
        return form is { Audience: DomainAudience.Public } ? form : null;
    }

    private static async Task<FillFormDto> ToFillAsync(IWorkbenchDbContext db, SheetForm form, bool isPublic, CancellationToken cancellationToken)
    {
        var sheet = await db.Sheets.AsNoTracking().FirstAsync(s => s.Id == form.SheetId, cancellationToken);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == form.SheetId).ToListAsync(cancellationToken);
        var fields = UsableFields(form, sheet, columns, isPublic)
            .Select(f => new FillFieldDto(
                f.Field.Key,
                LabelOf(f.Field, f.Column),
                f.Field.HelpText,
                f.Field.Required,
                f.Column?.Type.ToContractKind() ?? (f.Field.Key == "title" ? ColumnKind.Text : ColumnKind.Date),
                f.Column is { } c ? ColumnValues.Choices(Sheets.Mapping.ToDto(c)).ToList() : []))
            .ToList();
        return new FillFormDto(form.Id, form.Title, form.Description, fields, form.IsOpen);
    }

    private async Task<FormDto> ToDtoAsync(IWorkbenchDbContext db, SheetForm form, CancellationToken cancellationToken)
    {
        var role = await access.GetSheetRoleAsync(form.SheetId, cancellationToken);
        var canEdit = role >= WorkspaceRole.Editor;
        var sheetName = await db.Sheets.Where(s => s.Id == form.SheetId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == form.SheetId).ToListAsync(cancellationToken);
        var responses = await db.Set<FormSubmission>().CountAsync(s => s.FormId == form.Id && s.ItemId != null, cancellationToken);
        var fields = form.Fields
            .Select(f => new FormFieldDto(f.Key, LabelOf(f, Guid.TryParse(f.Key, out var id) ? columns.FirstOrDefault(c => c.Id == id) : null), f.HelpText, f.Required))
            .ToList();
        return new FormDto(form.Id, form.SheetId, sheetName, form.Title, form.Description, fields, form.ConfirmationMessage, form.IsOpen,
            (FormAudience)(int)form.Audience, canEdit && form.Audience == DomainAudience.Public ? $"f/{form.PublicToken}" : null,
            form.NotifyOnSubmit, form.CreatedBy, responses, form.UpdatedAtUtc, canEdit, canEdit && await CanPublishSheetAsync(form.SheetId, cancellationToken));
    }

    private static string LabelOf(SheetFormField field, Column? column) =>
        !string.IsNullOrWhiteSpace(field.Label) ? field.Label.Trim()
        : column is not null ? column.Name
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(field.Key);

    /// <summary>An answer in the column's stored form, or a plain-language error.</summary>
    private static string Normalize(string raw, Column column, string label)
    {
        switch (column.Type)
        {
            case ColumnType.Number or ColumnType.Currency:
                var cleaned = raw.Replace("$", "").Replace(",", "").Replace(" ", "");
                if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    throw new ArgumentException($"\"{label}\" needs a number.");
                }

                return number.ToString("0.############################", CultureInfo.InvariantCulture);
            case ColumnType.Date:
                return ParseDate(raw, label).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case ColumnType.Checkbox:
                return raw is "true" or "on" or "yes" or "1" ? "true" : "false";
            case ColumnType.Dropdown:
                return column.Options.FirstOrDefault(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Pick one of the choices for \"{label}\".");
            case ColumnType.Person:
                return MailAddress.TryCreate(raw, out var address) && address.Address == raw
                    ? raw.ToLowerInvariant()
                    : throw new ArgumentException($"\"{label}\" needs an email address.");
            case ColumnType.MultiSelect:
                var picked = ColumnValues.Selected(raw);
                return picked.All(p => column.Options.Contains(p, StringComparer.OrdinalIgnoreCase))
                    && ColumnValues.TryNormalize(raw, ColumnKind.MultiSelect, column.Options, out var selected, out _) && selected is not null
                    ? selected
                    : throw new ArgumentException($"Pick from the choices for \"{label}\".");
            case ColumnType.Hyperlink or ColumnType.Symbol or ColumnType.Progress:
                return ColumnValues.TryNormalize(raw.Trim(), column.Type.ToContractKind(), column.Options, out var value, out var error) && value is not null
                    ? value
                    : throw new ArgumentException($"\"{label}\": {error}");
            default:
                return raw;
        }
    }

    private static DateOnly ParseDate(string raw, string label) =>
        DateOnly.TryParseExact(raw, ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"\"{label}\" needs a date, like 10/22/2026.");

    private static string? Clip(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim() is { } t && t.Length > max ? t[..max] : text.Trim();

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>A hash of the client address and the form: enough to count one client's responses, useless for tracking.</summary>
    private static string? ClientKey(Guid formId, string? address) =>
        string.IsNullOrWhiteSpace(address) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{formId:N}|{address.Trim()}")))[..32];
}

/// <summary>Too many public responses from one client (or to one form) just now. Maps to 429.</summary>
public sealed class FormRateLimitException() : ArgumentException("Too many responses were sent from here in the last few minutes. Wait a little, then try again.");

internal static class FormMapping
{
    public static ColumnKind ToContractKind(this ColumnType type) => (ColumnKind)(int)type;
}
