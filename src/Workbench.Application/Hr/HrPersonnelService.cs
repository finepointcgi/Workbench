using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Files;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Files;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

public static class HrSecurityEvents
{
    public const string EeoViewed = "hr.eeo_viewed";
    public const string EeoChanged = "hr.eeo_changed";
    public const string Eeo1Exported = "export.hr_eeo1";
    public const string Vets4212Exported = "export.hr_vets4212";
    public const string BenefitSummaryExported = "export.hr_benefits";
    public const string DeductionsExported = "export.benefit_deductions";
    public const string AccessDeprovisioned = "hr.access_deprovisioned";
}

public static class HrNotificationKinds
{
    public const string ChangeRequested = "hr.change_requested";
    public const string ChangeDecided = "hr.change_decided";
    public const string CompensationRequested = "hr.compensation";
    public const string ChecklistTask = "hr.checklist_task";
    public const string LifeEvent = "hr.life_event";
}

/// <summary>Personnel records. Role gates are in the permission decorators; who sees which section is decided here.</summary>
public sealed class HrPersonnelService(
    IWorkbenchDbContextFactory dbFactory,
    HrAccess hrAccess,
    AuditRecorder audit,
    FileStorage files,
    INotificationPublisher notifications,
    TimeProvider clock,
    ApprovalEngine? approvals = null,
    SecurityEventLog? securityLog = null) : IHrPersonnelService
{
    public const string ProfileAudit = "EmployeeHrProfile";
    public const string EeoAudit = "EmployeeEeoRecord";
    public const string CredentialAudit = "EmployeeCredential";
    public const string RequestAudit = "HrChangeRequest";
    public const string DocumentOwner = "EmployeeDocument";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<EmployeeSummaryDto>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = Today;
        var employees = await db.Set<Employee>().AsNoTracking().ToListAsync(cancellationToken);
        var lookup = await LookupAsync(db, cancellationToken);
        var search = query.Search?.Trim();
        return employees
            .Where(e => (query.IncludeTerminated && viewer.IsHr) || e.TerminationDate is null || e.TerminationDate >= today)
            .Where(e => query.OrgUnitId is null || e.OrgUnitId == query.OrgUnitId)
            .Select(e => Summary(e, lookup))
            .Where(s => string.IsNullOrEmpty(search) || s.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || s.EmployeeNumber.Contains(search, StringComparison.OrdinalIgnoreCase) || (s.JobTitle?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || s.Email.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<EmployeeRecordDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RecordAsync(db, viewer, employeeId, cancellationToken);
    }

    public async Task<EmployeeRecordDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        if (viewer.EmployeeId is not { } me)
        {
            throw new WorkbenchNotFoundException("There's no employee record for your sign-in. Ask HR to link one.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RecordAsync(db, viewer, me, cancellationToken);
    }

    public async Task<EmployeeRecordDto> SaveJobAsync(Guid employeeId, SaveJobInfoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        if (request.SupervisorId is { } supervisorId)
        {
            if (supervisorId == employeeId)
            {
                throw new ArgumentException("An employee can't be their own supervisor.");
            }

            if (!await db.Set<Employee>().AnyAsync(e => e.Id == supervisorId, cancellationToken))
            {
                throw new ArgumentException("That supervisor doesn't exist.");
            }

            if ((await HrAccess.ReportsOfAsync(db, employeeId, cancellationToken)).Contains(supervisorId))
            {
                throw new ArgumentException("That supervisor reports to this employee; the reporting line would loop.");
            }
        }

        if (request.OrgUnitId is { } unit && !await db.Set<OrgUnit>().AnyAsync(u => u.Id == unit, cancellationToken))
        {
            throw new ArgumentException("That org unit doesn't exist.");
        }

        if (request.PositionId is { } positionId)
        {
            var position = await db.Set<D.Position>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == positionId, cancellationToken)
                ?? throw new ArgumentException("That position doesn't exist.");
            var profileNow = await db.Set<D.EmployeeHrProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken);
            if (profileNow?.PositionId != positionId && position.Status != D.PositionStatus.Approved)
            {
                throw new ArgumentException($"Position {position.Code} isn't approved for hiring.");
            }
        }

        var title = HrText.Clean(request.JobTitle, 200, "The job title");
        var location = HrText.Clean(request.WorkLocation, 200, "The work location");
        var state = HrText.State(request.WorkState);

        var profile = await ProfileAsync(db, employeeId, cancellationToken);
        var before = new { profile.JobTitle, profile.PositionId, employee.OrgUnitId, employee.SupervisorId, EmploymentType = profile.EmploymentType.ToString(), Status = profile.Status.ToString(), employee.IsExempt, profile.WorkLocation, profile.WorkState };
        profile.JobTitle = title;
        profile.PositionId = request.PositionId;
        profile.EmploymentType = (EmploymentType)(int)request.EmploymentType;
        profile.Status = (D.EmploymentStatus)(int)request.Status;
        profile.WorkLocation = location;
        profile.WorkState = state;
        Touch(profile);
        employee.OrgUnitId = request.OrgUnitId;
        employee.SupervisorId = request.SupervisorId;
        employee.IsExempt = request.IsExempt;

        // The payroll profile's employment type drives leave eligibility: keep them the same.
        if (await db.Set<EmployeePayProfile>().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, cancellationToken) is { } pay)
        {
            pay.EmploymentType = profile.EmploymentType;
        }

        audit.Record(db, ProfileAudit, profile.Id, "Job updated", before,
            new { profile.JobTitle, profile.PositionId, employee.OrgUnitId, employee.SupervisorId, EmploymentType = profile.EmploymentType.ToString(), Status = profile.Status.ToString(), employee.IsExempt, profile.WorkLocation, profile.WorkState });
        await db.SaveChangesAsync(cancellationToken);
        return await RecordAsync(db, viewer, employeeId, cancellationToken);
    }

    public async Task<EmployeeRecordDto> SavePersonalAsync(Guid employeeId, SavePersonalInfoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        _ = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var clean = CleanPersonal(request);
        var profile = await ProfileAsync(db, employeeId, cancellationToken);
        var changed = ApplyPersonal(profile, clean);
        Touch(profile);

        // Personal values never go into the audit log (Finance reads it): only which fields changed.
        audit.Record(db, ProfileAudit, profile.Id, "Personal details updated", null, new { Fields = changed });
        await db.SaveChangesAsync(cancellationToken);
        return await RecordAsync(db, viewer, employeeId, cancellationToken);
    }

    public async Task<EmployeeRecordDto> SaveEmergencyContactsAsync(Guid employeeId, IReadOnlyList<SaveEmergencyContact> contacts, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        _ = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        await ReplaceContactsAsync(db, employeeId, CleanContacts(contacts), cancellationToken);
        audit.Record(db, ProfileAudit, employeeId, "Emergency contacts updated", null, new { Count = contacts?.Count ?? 0 });
        await db.SaveChangesAsync(cancellationToken);
        return await RecordAsync(db, viewer, employeeId, cancellationToken);
    }

    public async Task<EeoRecordDto> GetEeoAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.RequireHrOrSelfAsync(employeeId, "see EEO and veteran information", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var row = await db.Set<D.EmployeeEeoRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.EmployeeId == employeeId, cancellationToken);
        if (!viewer.IsSelf(employeeId) && securityLog is not null)
        {
            await securityLog.RecordAsync(HrSecurityEvents.EeoViewed, $"employee {employee.EmployeeNumber}", detail: "EEO/veteran record viewed", cancellationToken: cancellationToken);
        }

        return row is null ? EeoRecordDto.Empty : ToDto(row);
    }

    public async Task<EeoRecordDto> SaveEeoAsync(Guid employeeId, SaveEeoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.RequireHrOrSelfAsync(employeeId, "change EEO and veteran information", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        if (!request.IsVeteran && (request.DisabledVeteran || request.RecentlySeparatedVeteran || request.ActiveDutyWartimeVeteran || request.ArmedForcesServiceMedalVeteran))
        {
            throw new ArgumentException("Protected veteran categories apply only to veterans.");
        }

        if (request.RecentlySeparatedVeteran && request.DischargeDate is null)
        {
            throw new ArgumentException("A recently separated veteran needs a discharge date.");
        }

        if (request.DischargeDate > Today)
        {
            throw new ArgumentException("The discharge date can't be in the future.");
        }

        var row = await db.Set<D.EmployeeEeoRecord>().FirstOrDefaultAsync(r => r.EmployeeId == employeeId, cancellationToken);
        var isNew = row is null;
        row ??= new D.EmployeeEeoRecord { EmployeeId = employeeId, UpdatedBy = viewer.Email };
        row.Gender = (D.EeoGender)(int)request.Gender;
        row.Race = (D.EeoRace)(int)request.Race;
        row.Disability = (D.DisabilityStatus)(int)request.Disability;
        row.IsVeteran = request.IsVeteran;
        row.DisabledVeteran = request.DisabledVeteran;
        row.RecentlySeparatedVeteran = request.RecentlySeparatedVeteran;
        row.ActiveDutyWartimeVeteran = request.ActiveDutyWartimeVeteran;
        row.ArmedForcesServiceMedalVeteran = request.ArmedForcesServiceMedalVeteran;
        row.DischargeDate = request.IsVeteran ? request.DischargeDate : null;
        if (viewer.IsHr)
        {
            row.JobCategory = request.JobCategory is { } category ? (D.EeoJobCategory)(int)category : null;
        }

        row.UpdatedAtUtc = UtcNow;
        row.UpdatedBy = viewer.Email;
        if (isNew)
        {
            db.Set<D.EmployeeEeoRecord>().Add(row);
        }

        // Restricted: the audit row says that it changed and by whom, never the answers.
        audit.Record(db, EeoAudit, row.Id, viewer.IsSelf(employeeId) ? "Self-identified" : "Updated by HR");
        await db.SaveChangesAsync(cancellationToken);
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(HrSecurityEvents.EeoChanged, $"employee {employee.EmployeeNumber}", detail: viewer.IsSelf(employeeId) ? "Self-identification" : "Changed by HR", cancellationToken: cancellationToken);
        }

        return ToDto(row);
    }

    public async Task<CredentialDto> SaveCredentialAsync(Guid employeeId, Guid? credentialId, SaveCredentialRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var name = HrText.Clean(request.Name, 200, "The name") ?? throw new ArgumentException("Give the credential a name.");
        if (request.Kind == CredentialKind.SecurityClearance && request.ClearanceLevel is null)
        {
            throw new ArgumentException("A security clearance needs a level.");
        }

        if (request.Kind != CredentialKind.SecurityClearance && request.ClearanceLevel is not null)
        {
            throw new ArgumentException("Only security clearances have a clearance level.");
        }

        if (request.IssuedOn is { } issued && request.ExpiresOn is { } expires && expires < issued)
        {
            throw new ArgumentException("The expiry date can't be before the issue date.");
        }

        D.EmployeeCredential row;
        if (credentialId is { } id)
        {
            row = await db.Set<D.EmployeeCredential>().FirstOrDefaultAsync(c => c.Id == id && c.EmployeeId == employeeId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Credential", id);
        }
        else
        {
            row = new D.EmployeeCredential { EmployeeId = employeeId, Name = name, UpdatedBy = viewer.Email };
            db.Set<D.EmployeeCredential>().Add(row);
        }

        row.Kind = (D.CredentialKind)(int)request.Kind;
        row.Name = name;
        row.Issuer = HrText.Clean(request.Issuer, 200, "The issuer");
        row.Identifier = HrText.Clean(request.Identifier, 100, "The number");
        row.ClearanceLevel = request.ClearanceLevel is { } level ? (D.ClearanceLevel)(int)level : null;
        row.IssuedOn = request.IssuedOn;
        row.ExpiresOn = request.ExpiresOn;
        row.Notes = HrText.Clean(request.Notes, 1000, "Notes");
        row.UpdatedAtUtc = UtcNow;
        row.UpdatedBy = viewer.Email;
        audit.Record(db, CredentialAudit, row.Id, credentialId is null ? "Added" : "Updated", null,
            new { Kind = row.Kind.ToString(), row.Name, Level = row.ClearanceLevel?.ToString(), row.ExpiresOn });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row, employee.DisplayName, Today);
    }

    public async Task DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<D.EmployeeCredential>().FirstOrDefaultAsync(c => c.Id == credentialId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Credential", credentialId);
        db.Set<D.EmployeeCredential>().Remove(row);
        audit.Record(db, CredentialAudit, row.Id, "Removed", new { Kind = row.Kind.ToString(), row.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CredentialDto>> ListExpiringCredentialsAsync(int withinDays, CancellationToken cancellationToken = default)
    {
        if (withinDays is < 0 or > 730)
        {
            throw new ArgumentException("Choose between 0 and 730 days.");
        }

        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = Today;
        var until = today.AddDays(withinDays);
        HashSet<Guid>? scope = null;
        if (!viewer.IsHr)
        {
            scope = viewer.EmployeeId is { } me ? await HrAccess.ReportsOfAsync(db, me, cancellationToken) : [];
            if (viewer.EmployeeId is { } self)
            {
                scope.Add(self);
            }
        }

        var rows = await db.Set<D.EmployeeCredential>().AsNoTracking().Where(c => c.ExpiresOn != null && c.ExpiresOn <= until).ToListAsync(cancellationToken);
        var active = await db.Set<Employee>().AsNoTracking().Where(e => e.TerminationDate == null || e.TerminationDate >= today)
            .ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
        return rows.Where(r => active.ContainsKey(r.EmployeeId) && (scope is null || scope.Contains(r.EmployeeId)))
            .Select(r => ToDto(r, active[r.EmployeeId], today))
            .OrderBy(r => r.ExpiresOn).ThenBy(r => r.EmployeeName)
            .ToList();
    }

    public async Task<EmployeeDocumentDto> UploadDocumentAsync(Guid employeeId, string title, string? category, bool employeeVisible, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        _ = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var cleanTitle = HrText.Clean(title, 200, "The title") ?? Path.GetFileNameWithoutExtension(fileName);
        var doc = new D.EmployeeDocument
        {
            EmployeeId = employeeId,
            Title = cleanTitle.Length > 200 ? cleanTitle[..200] : cleanTitle,
            Category = HrText.Clean(category, 100, "The category"),
            EmployeeVisible = employeeVisible,
            UploadedBy = viewer.Email,
            UploadedAtUtc = UtcNow,
        };
        var file = await files.SaveAsync(db, DocumentOwner, doc.Id, fileName, content, cancellationToken);
        doc.FileId = file.Id;
        db.Set<D.EmployeeDocument>().Add(doc);
        audit.Record(db, ProfileAudit, employeeId, "Document added", null, new { doc.Title, doc.Category, doc.EmployeeVisible });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await files.DiscardAsync(file);
            throw;
        }

        return new EmployeeDocumentDto(doc.Id, doc.Title, doc.Category, file.FileName, file.SizeBytes, doc.EmployeeVisible, doc.UploadedBy, doc.UploadedAtUtc);
    }

    public async Task<DocumentContent> OpenDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var doc = await db.Set<D.EmployeeDocument>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (doc is null || !(viewer.IsHr || (viewer.IsSelf(doc.EmployeeId) && doc.EmployeeVisible)))
        {
            // Someone who may not see it can't tell it exists.
            throw WorkbenchNotFoundException.For("Document", documentId);
        }

        var (file, stream) = await files.OpenAsync(db, doc.FileId, cancellationToken);
        return new DocumentContent(file.FileName, file.ContentType, stream);
    }

    public async Task<HrChangeRequestDto> RequestChangeAsync(SubmitHrChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        if (viewer.EmployeeId is not { } me)
        {
            throw new ArgumentException("There's no employee record for your sign-in. Ask HR to link one.");
        }

        if (request.Personal is null && request.EmergencyContacts is null)
        {
            throw new ArgumentException("There's nothing to change.");
        }

        var personal = request.Personal is null ? null : CleanPersonal(request.Personal);
        var contacts = request.EmergencyContacts is null ? null : CleanContacts(request.EmergencyContacts);
        var comment = HrText.Clean(request.Comment, 1000, "The comment");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == me, cancellationToken);
        if (await db.Set<D.HrChangeRequest>().AnyAsync(r => r.EmployeeId == me && r.Status == D.HrRequestStatus.Pending, cancellationToken))
        {
            throw new ArgumentException("You already have a change waiting for HR. Cancel it first, or wait for HR to decide it.");
        }

        var profile = await db.Set<D.EmployeeHrProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == me, cancellationToken) ?? new D.EmployeeHrProfile { EmployeeId = me };
        var fields = personal is null ? [] : ApplyPersonal(Clone(profile), personal);
        if (contacts is not null)
        {
            fields.Add("Emergency contacts");
        }

        if (fields.Count == 0)
        {
            throw new ArgumentException("Nothing differs from your record.");
        }

        var row = new D.HrChangeRequest
        {
            EmployeeId = me,
            RequestedBy = viewer.Email,
            CreatedAtUtc = UtcNow,
            ChangesJson = JsonSerializer.Serialize(new SubmitHrChangeRequest(personal, contacts, null), Json),
            Summary = Clip(string.Join(", ", fields), 500),
            Comment = comment,
        };
        db.Set<D.HrChangeRequest>().Add(row);
        audit.Record(db, RequestAudit, row.Id, "Requested", null, new { Fields = fields });
        var route = await _approvals.StartAsync(db, Subject(row, employee), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await notifications.PublishAsync(route.NotifyEmails, HrNotificationKinds.ChangeRequested, $"{employee.DisplayName} asked to update their record", row.Summary, link: "hr/requests");
        return ToDto(row, employee.DisplayName, includeChanges: true);
    }

    public async Task<IReadOnlyList<HrChangeRequestDto>> ListChangeRequestsAsync(bool pendingOnly = true, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.HrChangeRequest>().AsNoTracking();
        if (!viewer.IsHr)
        {
            query = query.Where(r => r.EmployeeId == viewer.EmployeeId);
        }

        if (pendingOnly)
        {
            query = query.Where(r => r.Status == D.HrRequestStatus.Pending);
        }

        var rows = await query.ToListAsync(cancellationToken);
        var names = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
        return rows.OrderByDescending(r => r.CreatedAtUtc).Select(r => ToDto(r, names.GetValueOrDefault(r.EmployeeId, ""), includeChanges: true)).ToList();
    }

    public Task<HrChangeRequestDto> ApproveChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(requestId, approve: true, request?.Comment, cancellationToken);

    public Task<HrChangeRequestDto> RejectChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(requestId, approve: false, request?.Comment, cancellationToken);

    public async Task<HrChangeRequestDto> CancelChangeAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<D.HrChangeRequest>().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);
        if (row is null || !(viewer.IsSelf(row.EmployeeId) || viewer.IsHr))
        {
            throw WorkbenchNotFoundException.For("Change request", requestId);
        }

        if (!viewer.IsSelf(row.EmployeeId))
        {
            throw new WorkbenchForbiddenException("Only the employee can cancel their own request; HR rejects it instead.");
        }

        if (row.Status != D.HrRequestStatus.Pending)
        {
            throw new ArgumentException($"This request is already {row.Status.ToString().ToLowerInvariant()}.");
        }

        row.Status = D.HrRequestStatus.Cancelled;
        row.DecidedAtUtc = UtcNow;
        row.DecidedBy = viewer.Email;
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.HrChange, row.Id, viewer.Email, "Cancelled by the employee", cancellationToken);
        audit.Record(db, RequestAudit, row.Id, "Cancelled");
        await db.SaveChangesAsync(cancellationToken);
        var name = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == row.EmployeeId).Select(e => e.DisplayName).FirstAsync(cancellationToken);
        return ToDto(row, name, includeChanges: true);
    }

    public async Task<IReadOnlyList<OrgChartNodeDto>> GetOrgChartAsync(CancellationToken cancellationToken = default)
    {
        await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = Today;
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => e.TerminationDate == null || e.TerminationDate >= today).ToListAsync(cancellationToken);
        var lookup = await LookupAsync(db, cancellationToken);
        var ids = employees.Select(e => e.Id).ToHashSet();
        var reports = employees.Where(e => e.SupervisorId is { } s && ids.Contains(s)).GroupBy(e => e.SupervisorId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return employees
            .Select(e => new OrgChartNodeDto(e.Id, e.DisplayName, lookup.Titles.GetValueOrDefault(e.Id), e.OrgUnitId is { } u ? lookup.OrgUnits.GetValueOrDefault(u) : null,
                e.SupervisorId is { } s && ids.Contains(s) ? s : null, reports.GetValueOrDefault(e.Id)))
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<HrChangeRequestDto> DecideAsync(Guid requestId, bool approve, string? comment, CancellationToken ct)
    {
        var viewer = await hrAccess.ViewerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<D.HrChangeRequest>().FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw WorkbenchNotFoundException.For("Change request", requestId);
        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == row.EmployeeId, ct);
        if (viewer.IsSelf(row.EmployeeId))
        {
            throw new WorkbenchForbiddenException("You can't decide a change to your own record.");
        }

        if (row.Status != D.HrRequestStatus.Pending)
        {
            throw new ArgumentException($"This request is already {row.Status.ToString().ToLowerInvariant()}.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.HrChange, row.Id, viewer.Email, _ => Task.FromResult(Subject(row, employee)), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("Only HR (or an administrator) can decide this request.");
        }

        var note = HrText.Clean(comment, 1000, "The comment");
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for rejecting the request.");
        }

        var outcome = await _approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, viewer.Email, note, ct);
        if (approve && !outcome.IsComplete)
        {
            audit.Record(db, RequestAudit, row.Id, "StepApproved", null, null, note);
            await db.SaveChangesAsync(ct);
            return ToDto(row, employee.DisplayName, includeChanges: true);
        }

        row.Status = approve ? D.HrRequestStatus.Approved : D.HrRequestStatus.Rejected;
        row.DecidedBy = viewer.Email;
        row.DecidedAtUtc = UtcNow;
        row.DecisionComment = note;
        if (approve)
        {
            var changes = JsonSerializer.Deserialize<SubmitHrChangeRequest>(row.ChangesJson, Json)!;
            if (changes.Personal is { } personal)
            {
                var profile = await ProfileAsync(db, row.EmployeeId, ct);
                ApplyPersonal(profile, personal);
                Touch(profile);
            }

            if (changes.EmergencyContacts is { } contacts)
            {
                await ReplaceContactsAsync(db, row.EmployeeId, contacts, ct);
            }
        }

        audit.Record(db, RequestAudit, row.Id, approve ? "Approved" : "Rejected", null, new { Fields = row.Summary }, note);
        await db.SaveChangesAsync(ct);
        await notifications.PublishAsync(employee.Email, HrNotificationKinds.ChangeDecided,
            approve ? "HR approved your record change" : "HR didn't approve your record change", approve ? row.Summary : note, link: "hr/me");
        return ToDto(row, employee.DisplayName, includeChanges: true);
    }

    internal static ApprovalSubject Subject(D.HrChangeRequest row, Employee employee) =>
        new(ApprovalDocumentTypes.HrChange, row.Id, $"{employee.DisplayName}: personnel change", row.RequestedBy, "hr/requests", null,
            row.Summary, employee.Id, [new ApprovalCharge(null, 0m, employee.OrgUnitId, "HR")]);

    private async Task<EmployeeRecordDto> RecordAsync(IWorkbenchDbContext db, HrAccess.Viewer viewer, Guid employeeId, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct) ?? throw HrAccess.EmployeeNotFound(employeeId);
        var level = await HrAccess.LevelAsync(db, viewer, employeeId, ct);
        var lookup = await LookupAsync(db, ct);
        var summary = Summary(employee, lookup);
        if (level == HrRecordAccess.Directory)
        {
            return new EmployeeRecordDto(summary, level, null, null, null, null, null, null);
        }

        var profile = await db.Set<D.EmployeeHrProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
        var pay = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
        var position = profile?.PositionId is { } pid ? await db.Set<D.Position>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
        var job = new JobInfoDto(profile?.JobTitle, position?.Id, position?.Code, position?.Title, employee.OrgUnitId,
            employee.OrgUnitId is { } u ? lookup.OrgUnits.GetValueOrDefault(u) : null, employee.SupervisorId,
            employee.SupervisorId is { } s ? lookup.Names.GetValueOrDefault(s) : null,
            (EmploymentTypeKind)(int)(profile?.EmploymentType ?? pay?.EmploymentType ?? EmploymentType.FullTime),
            summary.Status, employee.IsExempt, profile?.WorkLocation, profile?.WorkState, employee.HireDate, employee.TerminationDate,
            profile?.TerminationReason is { } reason ? (TerminationReason)(int)reason : null);
        var today = Today;
        var credentials = (await db.Set<D.EmployeeCredential>().AsNoTracking().Where(c => c.EmployeeId == employeeId).ToListAsync(ct))
            .OrderBy(c => c.Kind).ThenBy(c => c.Name).Select(c => ToDto(c, employee.DisplayName, today)).ToList();
        if (level == HrRecordAccess.Job)
        {
            return new EmployeeRecordDto(summary, level, job, null, null, credentials, null, null);
        }

        var personal = new PersonalInfoDto(profile?.PreferredName, profile?.PersonalEmail, profile?.PersonalPhone, profile?.AddressLine1, profile?.AddressLine2,
            profile?.City, profile?.State, profile?.PostalCode, profile?.BirthDate);
        var contacts = (await db.Set<D.EmergencyContact>().AsNoTracking().Where(c => c.EmployeeId == employeeId).ToListAsync(ct))
            .OrderBy(c => c.Sequence).Select(c => new EmergencyContactDto(c.Id, c.Name, c.Relationship, c.Phone, c.Email, c.IsPrimary)).ToList();
        var docs = await db.Set<D.EmployeeDocument>().AsNoTracking().Where(d => d.EmployeeId == employeeId && (level == HrRecordAccess.Full || d.EmployeeVisible))
            .Join(db.Set<StoredFile>(), d => d.FileId, f => f.Id, (d, f) => new { d, f.FileName, f.SizeBytes }).ToListAsync(ct);
        var requests = (await db.Set<D.HrChangeRequest>().AsNoTracking().Where(r => r.EmployeeId == employeeId && r.Status == D.HrRequestStatus.Pending).ToListAsync(ct))
            .Select(r => ToDto(r, employee.DisplayName, includeChanges: true)).ToList();
        return new EmployeeRecordDto(summary, level, job, personal, contacts, credentials,
            docs.OrderByDescending(x => x.d.UploadedAtUtc).Select(x => new EmployeeDocumentDto(x.d.Id, x.d.Title, x.d.Category, x.FileName, x.SizeBytes, x.d.EmployeeVisible, x.d.UploadedBy, x.d.UploadedAtUtc)).ToList(),
            requests);
    }

    internal sealed record Lookup(Dictionary<Guid, string> Names, Dictionary<Guid, string> OrgUnits, Dictionary<Guid, string?> Titles, Dictionary<Guid, D.EmploymentStatus> Statuses);

    internal static async Task<Lookup> LookupAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var names = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var units = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var profiles = await db.Set<D.EmployeeHrProfile>().AsNoTracking().Select(p => new { p.EmployeeId, p.JobTitle, p.Status }).ToListAsync(ct);
        return new Lookup(names, units, profiles.ToDictionary(p => p.EmployeeId, p => p.JobTitle), profiles.ToDictionary(p => p.EmployeeId, p => p.Status));
    }

    internal EmployeeSummaryDto Summary(Employee e, Lookup lookup) =>
        new(e.Id, e.EmployeeNumber, e.DisplayName, e.Email, lookup.Titles.GetValueOrDefault(e.Id), e.OrgUnitId is { } u ? lookup.OrgUnits.GetValueOrDefault(u) : null,
            e.SupervisorId, e.SupervisorId is { } s ? lookup.Names.GetValueOrDefault(s) : null,
            StatusOf(e, lookup.Statuses.TryGetValue(e.Id, out var st) ? st : null, Today), e.HireDate, e.TerminationDate);

    /// <summary>The stored status, except that a termination date in the past or a hire date ahead decides it.</summary>
    internal static EmploymentStatus StatusOf(Employee e, D.EmploymentStatus? stored, DateOnly today) =>
        e.TerminationDate is { } end && end < today ? EmploymentStatus.Terminated
        : e.HireDate > today ? EmploymentStatus.PendingStart
        : stored is { } s && s != D.EmploymentStatus.PendingStart ? (EmploymentStatus)(int)s
        : EmploymentStatus.Active;

    internal static async Task<D.EmployeeHrProfile> ProfileAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct)
    {
        var profile = await db.Set<D.EmployeeHrProfile>().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
        if (profile is null)
        {
            var pay = await db.Set<EmployeePayProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId, ct);
            profile = new D.EmployeeHrProfile { EmployeeId = employeeId, EmploymentType = pay?.EmploymentType ?? EmploymentType.FullTime };
            db.Set<D.EmployeeHrProfile>().Add(profile);
        }

        return profile;
    }

    private void Touch(D.EmployeeHrProfile profile)
    {
        profile.UpdatedAtUtc = UtcNow;
        profile.UpdatedBy = audit.Actor;
    }

    private static SavePersonalInfoRequest CleanPersonal(SavePersonalInfoRequest r)
    {
        var email = HrText.Clean(r.PersonalEmail, 320, "The personal email")?.ToLowerInvariant();
        if (email is not null && !MailAddress.TryCreate(email, out _))
        {
            throw new ArgumentException("Enter a valid personal email.");
        }

        return new SavePersonalInfoRequest(
            HrText.Clean(r.PreferredName, 200, "The preferred name"),
            email,
            HrText.Clean(r.PersonalPhone, 40, "The phone number"),
            HrText.Clean(r.AddressLine1, 200, "The address"),
            HrText.Clean(r.AddressLine2, 200, "The address"),
            HrText.Clean(r.City, 100, "The city"),
            HrText.State(r.State),
            HrText.Clean(r.PostalCode, 10, "The postal code"),
            r.BirthDate);
    }

    private static List<string> ApplyPersonal(D.EmployeeHrProfile p, SavePersonalInfoRequest r)
    {
        var changed = new List<string>();
        void Set<T>(string name, T current, T value, Action<T> apply)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                changed.Add(name);
                apply(value);
            }
        }

        Set("Preferred name", p.PreferredName, r.PreferredName, v => p.PreferredName = v);
        Set("Personal email", p.PersonalEmail, r.PersonalEmail, v => p.PersonalEmail = v);
        Set("Phone", p.PersonalPhone, r.PersonalPhone, v => p.PersonalPhone = v);
        Set("Address", (p.AddressLine1, p.AddressLine2, p.City, p.State, p.PostalCode), (r.AddressLine1, r.AddressLine2, r.City, r.State, r.PostalCode), v =>
            (p.AddressLine1, p.AddressLine2, p.City, p.State, p.PostalCode) = v);
        Set("Birth date", p.BirthDate, r.BirthDate, v => p.BirthDate = v);
        return changed;
    }

    private static D.EmployeeHrProfile Clone(D.EmployeeHrProfile p) => new()
    {
        EmployeeId = p.EmployeeId, PreferredName = p.PreferredName, PersonalEmail = p.PersonalEmail, PersonalPhone = p.PersonalPhone, AddressLine1 = p.AddressLine1,
        AddressLine2 = p.AddressLine2, City = p.City, State = p.State, PostalCode = p.PostalCode, BirthDate = p.BirthDate,
    };

    private static List<SaveEmergencyContact> CleanContacts(IReadOnlyList<SaveEmergencyContact>? contacts)
    {
        contacts ??= [];
        if (contacts.Count > 5)
        {
            throw new ArgumentException("Give at most five emergency contacts.");
        }

        var cleaned = contacts.Select(c => new SaveEmergencyContact(
            HrText.Clean(c.Name, 200, "The contact's name") ?? throw new ArgumentException("Every emergency contact needs a name."),
            HrText.Clean(c.Relationship, 100, "The relationship"),
            HrText.Clean(c.Phone, 40, "The phone number") ?? throw new ArgumentException("Every emergency contact needs a phone number."),
            HrText.Clean(c.Email, 320, "The contact's email")?.ToLowerInvariant(),
            c.IsPrimary)).ToList();
        if (cleaned.Count(c => c.IsPrimary) > 1)
        {
            throw new ArgumentException("Only one emergency contact can be the primary contact.");
        }

        if (cleaned.Count > 0 && !cleaned.Any(c => c.IsPrimary))
        {
            cleaned[0] = cleaned[0] with { IsPrimary = true };
        }

        return cleaned;
    }

    private static async Task ReplaceContactsAsync(IWorkbenchDbContext db, Guid employeeId, IReadOnlyList<SaveEmergencyContact> contacts, CancellationToken ct)
    {
        db.Set<D.EmergencyContact>().RemoveRange(await db.Set<D.EmergencyContact>().Where(c => c.EmployeeId == employeeId).ToListAsync(ct));
        db.Set<D.EmergencyContact>().AddRange(contacts.Select((c, i) => new D.EmergencyContact
        {
            EmployeeId = employeeId, Name = c.Name, Relationship = c.Relationship, Phone = c.Phone, Email = c.Email, IsPrimary = c.IsPrimary, Sequence = i,
        }));
    }

    internal static CredentialDto ToDto(D.EmployeeCredential c, string employeeName, DateOnly today) =>
        new(c.Id, c.EmployeeId, employeeName, (CredentialKind)(int)c.Kind, c.Name, c.Issuer, c.Identifier,
            c.ClearanceLevel is { } level ? (ClearanceLevel)(int)level : null, c.IssuedOn, c.ExpiresOn, c.Notes,
            c.ExpiresOn is { } expires ? expires.DayNumber - today.DayNumber : null);

    private static EeoRecordDto ToDto(D.EmployeeEeoRecord r) =>
        new((EeoGender)(int)r.Gender, (EeoRace)(int)r.Race, r.JobCategory is { } c ? (EeoJobCategory)(int)c : null, (DisabilityStatus)(int)r.Disability,
            r.IsVeteran, r.DisabledVeteran, r.RecentlySeparatedVeteran, r.ActiveDutyWartimeVeteran, r.ArmedForcesServiceMedalVeteran, r.DischargeDate, r.UpdatedAtUtc);

    private static HrChangeRequestDto ToDto(D.HrChangeRequest r, string employeeName, bool includeChanges) =>
        new(r.Id, r.EmployeeId, employeeName, r.RequestedBy, r.CreatedAtUtc, r.Summary, r.Comment, (HrRequestStatus)(int)r.Status, r.DecidedBy, r.DecidedAtUtc,
            r.DecisionComment, includeChanges ? JsonSerializer.Deserialize<SubmitHrChangeRequest>(r.ChangesJson, Json) : null);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}

internal static class HrText
{
    /// <summary>Trimmed text, null when blank; throws when longer than <paramref name="max"/>.</summary>
    public static string? Clean(string? value, int max, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > max ? throw new ArgumentException($"{what} can be at most {max} characters.") : trimmed;
    }

    public static string? State(string? value)
    {
        var state = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        return state.Length == 2 && state.All(char.IsLetter) ? state : throw new ArgumentException("Use the two-letter state code (for example VA).");
    }

    public static string Code(string? value, string what)
    {
        var code = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code))
        {
            throw new ArgumentException($"{what} needs a code.");
        }

        return code.Length > 32 ? throw new ArgumentException("Codes can be at most 32 characters.") : code;
    }
}
