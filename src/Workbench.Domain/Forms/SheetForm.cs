namespace Workbench.Domain.Forms;

/// <summary>Who may fill in a form.</summary>
public enum FormAudience
{
    /// <summary>Members of the sheet's workspace, signed in.</summary>
    Workspace,

    /// <summary>Anyone signed in to Workbench.</summary>
    Organization,

    /// <summary>Anyone with the public link, without signing in (also anyone signed in).</summary>
    Public,
}

/// <summary>
/// A form that adds a row to a sheet: chosen fields (Title and columns) with labels, help and required flags. People
/// filling it in never see the sheet or its other rows.
/// </summary>
public sealed class SheetForm : Entity
{
    public Guid SheetId { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public List<SheetFormField> Fields { get; set; } = [];

    /// <summary>Shown after submitting; a default thanks when empty.</summary>
    public string? ConfirmationMessage { get; set; }

    /// <summary>Closed forms take no responses.</summary>
    public bool IsOpen { get; set; } = true;

    public FormAudience Audience { get; set; }

    /// <summary>The secret in the public link <c>/f/{token}</c>; replaced when the owner resets the link.</summary>
    public required string PublicToken { get; set; }

    /// <summary>Notify the form's creator of each response.</summary>
    public bool NotifyOnSubmit { get; set; } = true;

    public required string CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A field: <see cref="Key"/> is <c>title</c>, <c>start</c>, <c>finish</c> or a column id.</summary>
public sealed class SheetFormField
{
    public string Key { get; set; } = "";

    public string? Label { get; set; }

    public string? HelpText { get; set; }

    public bool Required { get; set; }
}

/// <summary>
/// One response (or a refused one: spam or over the limit). Kept for rate limits and the response count; holds no answers,
/// which live in the row it added.
/// </summary>
public sealed class FormSubmission : Entity
{
    public Guid FormId { get; set; }

    public Guid SheetId { get; set; }

    /// <summary>The row it added; null when it was refused.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>The signed-in person's email, or null for the public link.</summary>
    public string? SubmittedBy { get; set; }

    /// <summary>For public responses, a hash of the client's address and the form (never the address itself).</summary>
    public string? ClientKey { get; set; }

    public bool IsPublic { get; set; }

    /// <summary>Why it was refused (spam), or null when it was accepted.</summary>
    public string? RejectedReason { get; set; }

    public DateTime SubmittedAtUtc { get; set; }
}
