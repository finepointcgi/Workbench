namespace Workbench.Domain.Compliance;

/// <summary>CASB disclosure statement forms: DS-1 (contractors) and DS-2 (educational institutions).</summary>
public enum DisclosureForm
{
    Ds1,
    Ds2,
}

public enum DisclosureStatus
{
    /// <summary>Being written; answers can change.</summary>
    Draft,

    /// <summary>The practices in force from <see cref="DisclosureStatement.EffectiveFrom"/>; read-only.</summary>
    Effective,

    /// <summary>Replaced by a later effective version (its <see cref="DisclosureStatement.EffectiveTo"/> is set).</summary>
    Superseded,
}

/// <summary>
/// One version of the organization's CAS disclosure statement. A revision is a new version copied from the latest one;
/// making it effective supersedes the version in force, so the history of disclosed practices stays.
/// </summary>
public sealed class DisclosureStatement : Entity
{
    public DisclosureForm Form { get; set; }
    public int Version { get; set; }
    public required string Title { get; set; }
    public DisclosureStatus Status { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public Guid? PreviousVersionId { get; set; }

    /// <summary>What changed from the previous version (a CAS 9903.302 change description).</summary>
    public string? ChangeSummary { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? MadeEffectiveBy { get; set; }
    public DateTime? MadeEffectiveAtUtc { get; set; }
}

/// <summary>An answer to one disclosure statement item: a coded choice (when the item has choices) and/or narrative text.</summary>
public sealed class DisclosureAnswer : Entity
{
    public Guid StatementId { get; set; }

    /// <summary>The item number, for example "4.2.0(b)".</summary>
    public required string ItemKey { get; set; }

    public string? Choice { get; set; }
    public string? Text { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
