using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Cash.Statements;
using Workbench.Application.Imports;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>SAM.gov saved searches, CSV import and lead review. Permissions: <c>PermissionCheckingOpportunityLeadService</c>.</summary>
public sealed class OpportunityLeadService(
    IWorkbenchDbContextFactory dbFactory,
    CrmAccess access,
    ISamGovClient sam,
    AuditRecorder audit,
    INotificationPublisher notifications,
    TimeProvider clock) : IOpportunityLeadService
{
    public const int MaxCsvRows = 5000;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<IReadOnlyList<SavedLeadSearchDto>> ListSearchesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var searches = await db.Set<D.SavedLeadSearch>().AsNoTracking().ToListAsync(cancellationToken);
        return searches.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(Map).ToList();
    }

    public async Task<SavedLeadSearchDto> CreateSearchAsync(SaveLeadSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var search = new D.SavedLeadSearch { Name = "", OwnerEmail = access.Email, CreatedAtUtc = UtcNow };
        Apply(search, request);
        db.Set<D.SavedLeadSearch>().Add(search);
        await db.SaveChangesAsync(cancellationToken);
        return Map(search);
    }

    public async Task<SavedLeadSearchDto> UpdateSearchAsync(Guid id, SaveLeadSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var search = await db.Set<D.SavedLeadSearch>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Saved search", id);
        Apply(search, request);
        await db.SaveChangesAsync(cancellationToken);
        return Map(search);
    }

    public async Task DeleteSearchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var search = await db.Set<D.SavedLeadSearch>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Saved search", id);
        foreach (var lead in await db.Set<D.OpportunityLead>().Where(l => l.SavedSearchId == id).ToListAsync(cancellationToken))
        {
            lead.SavedSearchId = null;
        }

        db.Set<D.SavedLeadSearch>().Remove(search);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Apply(D.SavedLeadSearch s, SaveLeadSearchRequest r)
    {
        s.Name = CrmMapping.Required(r.Name, 200, "Name");
        var naics = (r.NaicsCodes ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        if (naics.Any(n => n.Length is < 2 or > 6 || !n.All(char.IsDigit)))
        {
            throw new ArgumentException("NAICS codes are 2 to 6 digits.");
        }

        s.NaicsCodes = CrmMapping.Clean(string.Join(",", naics), 500, "NAICS codes");
        s.Agencies = CrmMapping.Clean(string.Join(",", (r.Agencies ?? []).Select(a => a.Trim().Replace(",", " ")).Where(a => a.Length > 0).Distinct()), 500, "Agencies");
        s.SetAsides = CrmMapping.Clean(string.Join(",", (r.SetAsides ?? []).Distinct()), 300, "Set-asides");
        s.Keywords = CrmMapping.Clean(r.Keywords, 500, "Keywords");
        if (r.PostedWithinDays is < 1 or > 364)
        {
            throw new ArgumentException("Look back 1 to 364 days.");
        }

        s.PostedWithinDays = r.PostedWithinDays;
        s.IsActive = r.IsActive;
    }

    public async Task<LeadImportResultDto> RunSearchAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var search = await db.Set<D.SavedLeadSearch>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Saved search", id);
        var dto = Map(search);
        var notices = await sam.SearchAsync(new SamSearchQuery(Today.AddDays(-search.PostedWithinDays), Today, dto.NaicsCodes, dto.SetAsides, dto.Agencies, search.Keywords), cancellationToken);
        var result = await AddLeadsAsync(db, notices.Select(n => (n, D.OpportunitySource.SamGov)).ToList(), search.Id, cancellationToken);
        search.LastRunAtUtc = UtcNow;
        search.LastRunNewLeads = result.Created;
        await db.SaveChangesAsync(cancellationToken);
        if (result.Created > 0 && search.OwnerEmail != access.Email)
        {
            await notifications.PublishAsync(search.OwnerEmail, CrmNotificationKinds.NewLeads, $"{result.Created} new lead{(result.Created == 1 ? "" : "s")} from \"{search.Name}\"",
                sam.IsSample ? "Sample SAM.gov data (no API key configured)" : "SAM.gov", link: "bd/leads");
        }

        return result;
    }

    /// <summary>
    /// Adds notices as new leads (tracked), skipping any whose solicitation number (else notice id) is already an opportunity
    /// or a lead in any state, or appeared earlier in the same batch.
    /// </summary>
    private async Task<LeadImportResultDto> AddLeadsAsync(IWorkbenchDbContext db, IReadOnlyList<(SamNotice Notice, D.OpportunitySource Source)> notices, Guid? searchId,
        CancellationToken ct)
    {
        var keys = notices.Select(n => PipelineMath.SolicitationKey(n.Notice.SolicitationNumber)).OfType<string>().Distinct().ToList();
        var noticeIds = notices.Select(n => n.Notice.NoticeId).Where(n => n.Length > 0).Distinct().ToList();
        var knownKeys = (await db.Set<D.Opportunity>().AsNoTracking().Where(o => o.SolicitationKey != null && keys.Contains(o.SolicitationKey)).Select(o => o.SolicitationKey!).ToListAsync(ct))
            .Concat(await db.Set<D.OpportunityLead>().AsNoTracking().Where(l => l.SolicitationKey != null && keys.Contains(l.SolicitationKey)).Select(l => l.SolicitationKey!).ToListAsync(ct))
            .ToHashSet();
        var knownIds = (await db.Set<D.OpportunityLead>().AsNoTracking().Where(l => l.SourceId != null && noticeIds.Contains(l.SourceId)).Select(l => l.SourceId!).ToListAsync(ct))
            .Concat(await db.Set<D.Opportunity>().AsNoTracking().Where(o => o.SamNoticeId != null && noticeIds.Contains(o.SamNoticeId)).Select(o => o.SamNoticeId!).ToListAsync(ct))
            .ToHashSet();
        var created = 0;
        var duplicates = 0;
        foreach (var (n, source) in notices)
        {
            var key = PipelineMath.SolicitationKey(n.SolicitationNumber);
            if ((key is not null && knownKeys.Contains(key)) || (n.NoticeId.Length > 0 && knownIds.Contains(n.NoticeId)))
            {
                duplicates++;
                continue;
            }

            if (key is not null)
            {
                knownKeys.Add(key);
            }

            if (n.NoticeId.Length > 0)
            {
                knownIds.Add(n.NoticeId);
            }

            db.Set<D.OpportunityLead>().Add(new D.OpportunityLead
            {
                Source = source, SourceId = CrmMapping.Clip(n.NoticeId, 100), SolicitationNumber = CrmMapping.Clip(n.SolicitationNumber, 100), SolicitationKey = CrmMapping.Clip(key, 100),
                Title = CrmMapping.Clip(n.Title, 500)!, Agency = CrmMapping.Clip(n.Agency, 300), Office = CrmMapping.Clip(n.Office, 300), NaicsCode = CrmMapping.Clip(n.NaicsCode, 16),
                SetAside = n.SetAside.ToDomain(), NoticeType = CrmMapping.Clip(n.NoticeType, 64), PostedOn = n.PostedOn, ResponseDueOn = n.ResponseDueOn,
                PlaceOfPerformance = CrmMapping.Clip(n.PlaceOfPerformance, 300), Url = CrmMapping.Clip(n.Url, 500), Description = CrmMapping.Clip(n.Description, 4000),
                EstimatedValue = n.EstimatedValue, SavedSearchId = searchId, Status = D.LeadStatus.New, CreatedAtUtc = UtcNow,
            });
            created++;
        }

        return new LeadImportResultDto(notices.Count, created, duplicates, []);
    }

    public async Task<LeadImportResultDto> ImportCsvAsync(string csv, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            throw new ArgumentException("The file is empty.");
        }

        var rows = CsvStatementParser.ReadRows(csv, ',').ToList();
        if (rows.Count < 2)
        {
            throw new ArgumentException("The file needs a header row and at least one notice.");
        }

        if (rows.Count - 1 > MaxCsvRows)
        {
            throw new ArgumentException($"Import at most {MaxCsvRows} notices at a time.");
        }

        var header = rows[0].Select(FieldMatcher.Normalize).ToList();
        int Column(params string[] names) => names.Select(n => header.IndexOf(FieldMatcher.Normalize(n))).FirstOrDefault(i => i >= 0, -1);
        var title = Column("Title", "Opportunity", "Name");
        if (title < 0)
        {
            throw new ArgumentException("The file needs a Title column.");
        }

        var (solicitation, noticeId, agency, office, naics, setAside, type, posted, due, place, url, description, value) = (
            Column("Solicitation Number", "Solicitation", "Sol#", "Solicitation #"), Column("Notice Id", "NoticeId", "Notice ID"),
            Column("Agency", "Department", "Department/Ind.Agency"), Column("Office", "Sub-Tier", "Subtier"), Column("NAICS", "NAICS Code", "NaicsCode"),
            Column("Set-Aside", "Set Aside", "SetAside", "Type of Set Aside"), Column("Notice Type", "Type"), Column("Posted Date", "Posted", "PostedDate"),
            Column("Response Date", "Response Deadline", "Due Date", "ResponseDeadLine"), Column("Place of Performance", "Place"), Column("URL", "Link"),
            Column("Description"), Column("Estimated Value", "Value", "Award Amount"));
        string? Cell(List<string> row, int index) => index >= 0 && index < row.Count && !string.IsNullOrWhiteSpace(row[index]) ? row[index].Trim() : null;

        var notices = new List<(SamNotice, D.OpportunitySource)>();
        var errors = new List<string>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var name = Cell(row, title);
            if (name is null)
            {
                errors.Add($"Line {i + 1}: no title.");
                continue;
            }

            decimal? amount = null;
            if (Cell(row, value) is { } v)
            {
                if (!decimal.TryParse(v.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    errors.Add($"Line {i + 1}: \"{v}\" isn't an amount.");
                    continue;
                }

                amount = parsed;
            }

            notices.Add((new SamNotice(Cell(row, noticeId) ?? "", name, Cell(row, solicitation), Cell(row, agency), Cell(row, office), Cell(row, naics),
                SamSetAsides.Parse(Cell(row, setAside)), Cell(row, type), DateOf(Cell(row, posted)), DateOf(Cell(row, due)), Cell(row, place), Cell(row, url),
                Cell(row, description), amount), D.OpportunitySource.CsvImport));
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var result = await AddLeadsAsync(db, notices, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return result with { Found = rows.Count - 1, Errors = errors };
    }

    private static DateOnly? DateOf(string? text) =>
        text is null ? null
        : MappedRow.ParseDate(text) is { } d ? (DateOnly?)d
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? DateOnly.FromDateTime(dt.Date)
        : null;

    public async Task<IReadOnlyList<OpportunityLeadDto>> ListLeadsAsync(LeadStatus? status = LeadStatus.New, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.OpportunityLead>().AsNoTracking();
        if (status is { } s)
        {
            var domain = (D.LeadStatus)(int)s;
            query = query.Where(l => l.Status == domain);
        }

        var leads = await query.ToListAsync(cancellationToken);
        return await MapAsync(db, leads.OrderByDescending(l => l.PostedOn ?? DateOnly.FromDateTime(l.CreatedAtUtc)).ThenBy(l => l.Title).ToList(), cancellationToken);
    }

    public async Task<OpportunityDto> AcceptAsync(Guid leadId, AcceptLeadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var lead = await db.Set<D.OpportunityLead>().FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Lead", leadId);
        if (lead.Status != D.LeadStatus.New)
        {
            throw new ArgumentException($"This lead was already {lead.Status.ToString().ToLowerInvariant()}.");
        }

        var agencyId = request.AgencyAccountId ?? await MatchAgencyAsync(db, lead, cancellationToken);
        var now = UtcNow;
        var opportunity = await OpportunityService.NewAsync(db, access.Email, now, lead.Source, cancellationToken);
        opportunity.SamNoticeId = lead.Source == D.OpportunitySource.SamGov ? lead.SourceId : null;
        await OpportunityService.ApplyAsync(db, opportunity, new SaveOpportunityRequest(
            CrmMapping.Clip(request.Name ?? lead.Title, 300)!,
            Description: lead.Description,
            SolicitationNumber: lead.SolicitationNumber,
            AgencyAccountId: agencyId,
            AgencyName: CrmMapping.Clip(string.Join(" / ", new[] { lead.Agency, lead.Office }.Where(x => !string.IsNullOrWhiteSpace(x))), 300),
            NaicsCode: lead.NaicsCode is { Length: >= 2 and <= 6 } n && n.All(char.IsDigit) ? n : null,
            SetAside: lead.SetAside.ToContract(),
            EstimatedValue: request.EstimatedValue ?? lead.EstimatedValue ?? 0m,
            ProposalDueOn: lead.ResponseDueOn,
            PlaceOfPerformance: lead.PlaceOfPerformance,
            CaptureManagerEmail: request.CaptureManagerEmail ?? access.Email,
            SourceUrl: lead.Url), cancellationToken);
        db.Set<D.Opportunity>().Add(opportunity);
        lead.Status = D.LeadStatus.Accepted;
        lead.OpportunityId = opportunity.Id;
        lead.ReviewedBy = access.Email;
        lead.ReviewedAtUtc = now;
        audit.Record(db, OpportunityService.AuditEntityType, opportunity.Id, "Created", after: OpportunityService.Snapshot(opportunity), reason: $"From lead {lead.SourceId ?? lead.Title}");
        await db.SaveChangesAsync(cancellationToken);
        return (await CrmMapping.ToDtosAsync(db, [opportunity], await access.RolesAsync(cancellationToken), access.Email, cancellationToken))[0];
    }

    /// <summary>An agency or office account whose name or acronym is the lead's office or agency (the most specific match).</summary>
    private static async Task<Guid?> MatchAgencyAsync(IWorkbenchDbContext db, D.OpportunityLead lead, CancellationToken ct)
    {
        var accounts = await db.Set<D.CrmAccount>().AsNoTracking()
            .Where(a => a.IsActive && (a.Kind == D.CrmAccountKind.Agency || a.Kind == D.CrmAccountKind.Office)).ToListAsync(ct);
        // Whole words, ignoring case and punctuation: "DEPT OF THE ARMY / PEO EIS" matches "PEO EIS" and "Department of the Army"-style names.
        static string Words(string text) =>
            " " + string.Join(' ', text.ToUpperInvariant().Split([' ', '/', ',', '.', '-', '(', ')', '&'], StringSplitOptions.RemoveEmptyEntries)) + " ";
        bool Matches(D.CrmAccount a, string? text) => text is not null
            && (Words(text).Contains(Words(a.Name), StringComparison.Ordinal) || (a.Acronym is { Length: > 1 } acr && Words(text).Contains(Words(acr), StringComparison.Ordinal)));
        return (accounts.FirstOrDefault(a => a.Kind == D.CrmAccountKind.Office && Matches(a, lead.Office))
                ?? accounts.FirstOrDefault(a => Matches(a, lead.Office)) ?? accounts.FirstOrDefault(a => Matches(a, lead.Agency)))?.Id;
    }

    public async Task<OpportunityLeadDto> DismissAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var lead = await db.Set<D.OpportunityLead>().FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Lead", leadId);
        if (lead.Status != D.LeadStatus.New)
        {
            throw new ArgumentException($"This lead was already {lead.Status.ToString().ToLowerInvariant()}.");
        }

        lead.Status = D.LeadStatus.Dismissed;
        lead.ReviewedBy = access.Email;
        lead.ReviewedAtUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, [lead], cancellationToken))[0];
    }

    private static async Task<List<OpportunityLeadDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<D.OpportunityLead> leads, CancellationToken ct)
    {
        var searchIds = leads.Where(l => l.SavedSearchId != null).Select(l => l.SavedSearchId!.Value).Distinct().ToList();
        var searches = await db.Set<D.SavedLeadSearch>().AsNoTracking().Where(s => searchIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return leads.Select(l => new OpportunityLeadDto(l.Id, l.Source.ToContract(), l.SourceId, l.SolicitationNumber, l.Title, l.Agency, l.Office, l.NaicsCode,
            l.SetAside.ToContract(), l.NoticeType, l.PostedOn, l.ResponseDueOn, l.PlaceOfPerformance, l.Url, l.Description, l.EstimatedValue, l.SavedSearchId,
            l.SavedSearchId is { } s ? searches.GetValueOrDefault(s) : null, l.Status.ToContract(), l.OpportunityId, l.ReviewedBy, l.CreatedAtUtc)).ToList();
    }

    private static SavedLeadSearchDto Map(D.SavedLeadSearch s) =>
        new(s.Id, s.Name, Split(s.NaicsCodes), Split(s.Agencies),
            Split(s.SetAsides).Select(x => Enum.TryParse<SetAside>(x, out var v) ? v : (SetAside?)null).OfType<SetAside>().ToList(),
            s.Keywords, s.PostedWithinDays, s.IsActive, s.OwnerEmail, s.LastRunAtUtc, s.LastRunNewLeads);

    private static List<string> Split(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
