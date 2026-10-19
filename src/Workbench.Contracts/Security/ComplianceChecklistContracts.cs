namespace Workbench.Contracts.Security;

public enum ChecklistStatus
{
    NotReviewed,
    Meets,
    Partial,
    DoesNotMeet,
    NotApplicable,
}

/// <summary>
/// One criterion of a compliance checklist: what it asks, which Workbench feature and report answer it, a live check
/// of this organization's data where one is possible, whether a GovCon CPA must review it, and the reviewer's answer.
/// </summary>
public sealed record ChecklistItemDto(
    string Key,
    string Section,
    string Reference,
    string Criterion,
    string WorkbenchFeature,
    string Evidence,
    string? Link,
    bool CpaReview,
    string? CpaNote,
    bool? LiveCheckPassed,
    string? LiveCheckDetail,
    ChecklistStatus Status,
    string? Notes,
    string? ReviewedBy,
    DateTime? ReviewedAtUtc);

public sealed record ChecklistDto(string Key, string Title, string Description, string DocumentPath, IReadOnlyList<ChecklistItemDto> Items);

public sealed record SaveChecklistAnswerRequest(ChecklistStatus Status, string? Notes);

/// <summary>
/// In-app compliance self-assessments (today the DCAA SF 1408 pre-award accounting system survey). Admins only. Every
/// answer is kept (the latest is current) and recorded as a security event, so the history is evidence.
/// </summary>
public interface IComplianceChecklistService
{
    Task<ChecklistDto> GetAsync(string checklistKey, CancellationToken cancellationToken = default);

    Task<ChecklistItemDto> SaveAnswerAsync(string checklistKey, string itemKey, SaveChecklistAnswerRequest request, CancellationToken cancellationToken = default);
}

public static class ComplianceChecklists
{
    public const string DcaaSf1408 = "dcaa-sf1408";
}
