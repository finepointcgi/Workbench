using Microsoft.EntityFrameworkCore;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>Accounts, contacts and activities. Permissions: <c>PermissionCheckingCrmAccountService</c>.</summary>
public sealed class CrmAccountService(IWorkbenchDbContextFactory dbFactory, CrmAccess access, INotificationPublisher notifications, TimeProvider clock) : ICrmAccountService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<CrmAccountDto>> ListAccountsAsync(CrmAccountKind? kind = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.CrmAccount>().AsNoTracking();
        if (kind is { } k)
        {
            var domain = k.ToDomain();
            query = query.Where(a => a.Kind == domain);
        }

        var accounts = await query.ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            accounts = accounts.Where(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (a.Acronym?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return await MapAsync(db, accounts, cancellationToken);
    }

    public async Task<CrmAccountDto> GetAccountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<D.CrmAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Account", id);
        return (await MapAsync(db, [account], cancellationToken))[0];
    }

    public async Task<CrmAccountDto> CreateAccountAsync(SaveCrmAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = new D.CrmAccount { Name = "", CreatedBy = access.Email, CreatedAtUtc = UtcNow };
        await ApplyAsync(db, account, request, cancellationToken);
        db.Set<D.CrmAccount>().Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAccountAsync(account.Id, cancellationToken);
    }

    public async Task<CrmAccountDto> UpdateAccountAsync(Guid id, SaveCrmAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<D.CrmAccount>().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Account", id);
        await ApplyAsync(db, account, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAccountAsync(id, cancellationToken);
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, D.CrmAccount account, SaveCrmAccountRequest r, CancellationToken ct)
    {
        account.Name = CrmMapping.Required(r.Name, 200, "Name");
        account.Kind = r.Kind.ToDomain();
        account.Acronym = CrmMapping.Clean(r.Acronym, 32, "Acronym");
        account.Uei = CrmMapping.Clean(r.Uei, 16, "UEI")?.ToUpperInvariant();
        account.CageCode = CrmMapping.Clean(r.CageCode, 8, "CAGE code")?.ToUpperInvariant();
        account.Website = CrmMapping.Clean(r.Website, 300, "Website");
        account.Phone = CrmMapping.Clean(r.Phone, 64, "Phone");
        account.Address = CrmMapping.Clean(r.Address, 500, "Address");
        account.Notes = CrmMapping.Clean(r.Notes, 4000, "Notes");
        account.IsActive = r.IsActive;
        if (r.ParentId is { } parentId)
        {
            var parent = await db.Set<D.CrmAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == parentId, ct)
                ?? throw new ArgumentException("Choose an existing parent account.");
            if (parent.Kind.ToContract().IsGovernment() != account.Kind.ToContract().IsGovernment())
            {
                throw new ArgumentException("A government office goes under an agency, and a company under a company.");
            }

            if (parentId == account.Id || (await WithDescendantsAsync(db, account.Id, ct)).Contains(parentId))
            {
                throw new ArgumentException($"{parent.Name} is inside {account.Name}; it can't also be its parent.");
            }
        }

        account.ParentId = r.ParentId;
    }

    public async Task<CrmAccountDto> LinkCustomerAsync(Guid id, Guid? customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var account = await db.Set<D.CrmAccount>().FirstOrDefaultAsync(a => a.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Account", id);
        if (customerId is { } c && !await db.Set<Customer>().AnyAsync(x => x.Id == c, cancellationToken))
        {
            throw new ArgumentException("Choose an existing customer.");
        }

        account.CustomerId = customerId;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAccountAsync(id, cancellationToken);
    }

    /// <summary>The account and every account below it (offices under an agency).</summary>
    internal static async Task<HashSet<Guid>> WithDescendantsAsync(IWorkbenchDbContext db, Guid accountId, CancellationToken ct)
    {
        var parents = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => a.ParentId != null).Select(a => new { a.Id, Parent = a.ParentId!.Value }).ToListAsync(ct);
        var children = parents.ToLookup(p => p.Parent, p => p.Id);
        var result = new HashSet<Guid> { accountId };
        var queue = new Queue<Guid>([accountId]);
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (result.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    private static async Task<List<CrmAccountDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<D.CrmAccount> accounts, CancellationToken ct)
    {
        var ids = accounts.Select(a => a.Id).ToList();
        var parentIds = accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).Distinct().ToList();
        var parents = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => parentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var customerIds = accounts.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var contacts = (await db.Set<D.CrmContact>().AsNoTracking().Where(c => ids.Contains(c.AccountId) && c.IsActive).Select(c => c.AccountId).ToListAsync(ct))
            .GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        var open = (await db.Set<D.Opportunity>().AsNoTracking().Where(o => o.AgencyAccountId != null && ids.Contains(o.AgencyAccountId.Value) && CrmMapping.OpenStages.Contains(o.Stage))
            .Select(o => o.AgencyAccountId!.Value).ToListAsync(ct)).GroupBy(a => a).ToDictionary(g => g.Key, g => g.Count());
        return accounts.OrderBy(a => a.Kind).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new CrmAccountDto(
            a.Id, a.Name, a.Acronym, a.Kind.ToContract(), a.ParentId, a.ParentId is { } p ? parents.GetValueOrDefault(p) : null, a.Uei, a.CageCode, a.Website,
            a.Phone, a.Address, a.Notes, a.CustomerId, a.CustomerId is { } c ? customers.GetValueOrDefault(c) : null, a.IsActive,
            contacts.GetValueOrDefault(a.Id), open.GetValueOrDefault(a.Id))).ToList();
    }

    // ---- Contacts ----

    public async Task<IReadOnlyList<CrmContactDto>> ListContactsAsync(Guid? accountId = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.CrmContact>().AsNoTracking();
        if (accountId is { } a)
        {
            query = query.Where(c => c.AccountId == a);
        }

        var contacts = await query.ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            contacts = contacts.Where(c => $"{c.FirstName} {c.LastName}".Contains(term, StringComparison.OrdinalIgnoreCase)
                                           || (c.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                           || (c.Title?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return await MapContactsAsync(db, contacts, cancellationToken);
    }

    public async Task<CrmContactDto> CreateContactAsync(SaveCrmContactRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contact = new D.CrmContact { FirstName = "", LastName = "", CreatedAtUtc = UtcNow };
        await ApplyAsync(db, contact, request, cancellationToken);
        db.Set<D.CrmContact>().Add(contact);
        await db.SaveChangesAsync(cancellationToken);
        return (await MapContactsAsync(db, [contact], cancellationToken))[0];
    }

    public async Task<CrmContactDto> UpdateContactAsync(Guid id, SaveCrmContactRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contact = await db.Set<D.CrmContact>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Contact", id);
        await ApplyAsync(db, contact, request, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await MapContactsAsync(db, [contact], cancellationToken))[0];
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, D.CrmContact contact, SaveCrmContactRequest r, CancellationToken ct)
    {
        if (!await db.Set<D.CrmAccount>().AnyAsync(a => a.Id == r.AccountId, ct))
        {
            throw new ArgumentException("Choose the contact's account.");
        }

        contact.AccountId = r.AccountId;
        contact.FirstName = CrmMapping.Required(r.FirstName, 100, "First name");
        contact.LastName = CrmMapping.Required(r.LastName, 100, "Last name");
        contact.Title = CrmMapping.Clean(r.Title, 200, "Title");
        contact.Role = r.Role.ToDomain();
        contact.Email = CrmMapping.Email(r.Email);
        contact.Phone = CrmMapping.Clean(r.Phone, 64, "Phone");
        contact.Notes = CrmMapping.Clean(r.Notes, 2000, "Notes");
        contact.IsActive = r.IsActive;
    }

    private static async Task<List<CrmContactDto>> MapContactsAsync(IWorkbenchDbContext db, IReadOnlyList<D.CrmContact> contacts, CancellationToken ct)
    {
        var accountIds = contacts.Select(c => c.AccountId).Distinct().ToList();
        var accounts = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return contacts.OrderBy(c => c.LastName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.FirstName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CrmContactDto(c.Id, c.AccountId, accounts.GetValueOrDefault(c.AccountId) ?? "", c.FirstName, c.LastName, c.Title, c.Role.ToContract(),
                c.Email, c.Phone, c.Notes, c.IsActive)).ToList();
    }

    // ---- Activities ----

    public async Task<IReadOnlyList<CrmActivityDto>> ListActivitiesAsync(CrmActivityFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.CrmActivity>().AsNoTracking();
        if (filter.AccountId is { } account)
        {
            // An agency's activity includes its offices' and its contacts'.
            var accounts = await WithDescendantsAsync(db, account, cancellationToken);
            var contacts = db.Set<D.CrmContact>().Where(c => accounts.Contains(c.AccountId)).Select(c => c.Id);
            query = query.Where(a => (a.AccountId != null && accounts.Contains(a.AccountId.Value)) || (a.ContactId != null && contacts.Contains(a.ContactId.Value)));
        }

        if (filter.ContactId is { } contact)
        {
            query = query.Where(a => a.ContactId == contact);
        }

        if (filter.OpportunityId is { } opportunity)
        {
            query = query.Where(a => a.OpportunityId == opportunity);
        }

        if (filter.OpenTasksOnly)
        {
            query = query.Where(a => a.Kind == D.CrmActivityKind.Task && !a.IsDone);
        }

        if (filter.MineOnly)
        {
            var me = access.Email;
            query = query.Where(a => a.AssignedTo == me || (a.AssignedTo == null && a.CreatedBy == me));
        }

        var rows = await query.ToListAsync(cancellationToken);
        return await MapActivitiesAsync(db, rows, cancellationToken);
    }

    public async Task<CrmActivityDto> LogActivityAsync(SaveCrmActivityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (request.AccountId is null && request.ContactId is null && request.OpportunityId is null)
        {
            throw new ArgumentException("Attach the activity to an account, a contact or an opportunity.");
        }

        Guid? accountId = request.AccountId;
        if (request.ContactId is { } contactId)
        {
            var contact = await db.Set<D.CrmContact>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, cancellationToken)
                ?? throw new ArgumentException("Choose an existing contact.");
            accountId ??= contact.AccountId;
        }

        if (accountId is { } a && !await db.Set<D.CrmAccount>().AnyAsync(x => x.Id == a, cancellationToken))
        {
            throw new ArgumentException("Choose an existing account.");
        }

        var isTask = request.Kind == CrmActivityKind.Task;
        var activity = new D.CrmActivity
        {
            Kind = request.Kind.ToDomain(),
            Subject = CrmMapping.Required(request.Subject, 300, "Subject"),
            Body = CrmMapping.Clean(request.Body, 4000, "Details"),
            AccountId = accountId,
            ContactId = request.ContactId,
            OpportunityId = request.OpportunityId,
            OccurredOn = isTask ? null : request.OccurredOn ?? DateOnly.FromDateTime(UtcNow),
            DueOn = isTask ? request.DueOn ?? throw new ArgumentException("Give the task a due date.") : null,
            AssignedTo = isTask ? CrmMapping.Email(request.AssignedTo) ?? access.Email : null,
            CreatedBy = access.Email,
            CreatedAtUtc = UtcNow,
        };
        db.Set<D.CrmActivity>().Add(activity);
        await db.SaveChangesAsync(cancellationToken);
        if (isTask && activity.AssignedTo != access.Email)
        {
            await notifications.PublishAsync(activity.AssignedTo!, CrmNotificationKinds.TaskAssigned, $"Business development task: {activity.Subject}",
                $"Due {activity.DueOn:ddd MMM d}", link: ActivityLink(activity));
        }

        return (await MapActivitiesAsync(db, [activity], cancellationToken))[0];
    }

    public async Task<CrmActivityDto> SetTaskDoneAsync(Guid activityId, bool done, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var activity = await db.Set<D.CrmActivity>().FirstOrDefaultAsync(a => a.Id == activityId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Activity", activityId);
        if (activity.Kind != D.CrmActivityKind.Task)
        {
            throw new ArgumentException("Only tasks are marked done.");
        }

        activity.IsDone = done;
        activity.CompletedAtUtc = done ? UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        return (await MapActivitiesAsync(db, [activity], cancellationToken))[0];
    }

    /// <summary>Where an activity is shown: its opportunity, else its account.</summary>
    public static string ActivityLink(D.CrmActivity a) =>
        a.OpportunityId is { } o ? $"bd/opportunities/{o}?tab=activity" : a.AccountId is { } acc ? $"bd/accounts/{acc}" : "bd/accounts";

    private static async Task<List<CrmActivityDto>> MapActivitiesAsync(IWorkbenchDbContext db, IReadOnlyList<D.CrmActivity> rows, CancellationToken ct)
    {
        var accountIds = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var opportunityIds = rows.Where(r => r.OpportunityId != null).Select(r => r.OpportunityId!.Value).Distinct().ToList();
        var accounts = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var contacts = await db.Set<D.CrmContact>().AsNoTracking().Where(c => contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FirstName + " " + c.LastName, ct);
        var opportunities = await db.Set<D.Opportunity>().AsNoTracking().Where(o => opportunityIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number + " " + o.Name, ct);
        return rows.OrderByDescending(r => r.Kind == D.CrmActivityKind.Task && !r.IsDone).ThenByDescending(r => r.DueOn ?? r.OccurredOn).ThenByDescending(r => r.CreatedAtUtc)
            .Select(r => new CrmActivityDto(r.Id, r.Kind.ToContract(), r.Subject, r.Body, r.OccurredOn, r.DueOn, r.AssignedTo, r.IsDone,
                r.AccountId, r.AccountId is { } a ? accounts.GetValueOrDefault(a) : null, r.ContactId, r.ContactId is { } c ? contacts.GetValueOrDefault(c) : null,
                r.OpportunityId, r.OpportunityId is { } o ? opportunities.GetValueOrDefault(o) : null, r.CreatedBy, r.CreatedAtUtc)).ToList();
    }
}
