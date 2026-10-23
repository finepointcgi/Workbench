using Workbench.Contracts.Sheets;

namespace Workbench.Contracts.Forms;

/// <summary>Who may fill in a form: the workspace's members, anyone signed in, or anyone with the public link.</summary>
public enum FormAudience
{
    Workspace,
    Organization,
    Public,
}

/// <summary>A form field: <paramref name="Key"/> is <c>title</c>, <c>start</c>, <c>finish</c> or a column id.</summary>
public sealed record FormFieldDto(string Key, string Label, string? HelpText = null, bool Required = false);

/// <summary>
/// A form on a sheet. <paramref name="PublicPath"/> is the app-relative public link (<c>f/{token}</c>), only for public
/// forms and only to people who can edit the form. <paramref name="CanPublish"/>: the user may make it public.
/// </summary>
public sealed record FormDto(
    Guid Id, Guid SheetId, string SheetName, string Title, string? Description, IReadOnlyList<FormFieldDto> Fields,
    string? ConfirmationMessage, bool IsOpen, FormAudience Audience, string? PublicPath, bool NotifyOnSubmit,
    string CreatedBy, int ResponseCount, DateTime UpdatedAtUtc, bool CanEdit, bool CanPublish);

public sealed record SaveFormRequest(
    string Title, IReadOnlyList<FormFieldDto> Fields, string? Description = null, string? ConfirmationMessage = null,
    bool IsOpen = true, FormAudience Audience = FormAudience.Workspace, bool NotifyOnSubmit = true);

/// <summary>A field as the person filling in the form sees it.</summary>
public sealed record FillFieldDto(string Key, string Label, string? HelpText, bool Required, ColumnKind Type, IReadOnlyList<string> Options);

/// <summary>The form to fill in: no sheet data beyond its own fields (and, for dropdowns, their options).</summary>
public sealed record FillFormDto(Guid FormId, string Title, string? Description, IReadOnlyList<FillFieldDto> Fields, bool IsOpen);

/// <summary>Answers by field key, in the stored cell forms (dates <c>yyyy-MM-dd</c> or MM/DD/YYYY, checkboxes <c>true</c>).</summary>
public sealed record SubmitFormRequest(IReadOnlyDictionary<string, string?> Values);

public sealed record FormSubmittedDto(string Message);

/// <summary>
/// Forms that add a row to a sheet. Editors of the sheet build them; making one public needs the workspace's Owner role
/// and the "Publish public forms" permission. Each response adds a row (raising <c>sheet.form_submitted</c> and the
/// sheet's "row added" rules) and notifies the form's creator.
/// </summary>
public interface IFormService
{
    /// <summary>The sheet's forms. Needs Viewer; only Editors see public links.</summary>
    Task<IReadOnlyList<FormDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<FormDto> CreateAsync(Guid sheetId, SaveFormRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether the user may share forms on this sheet by public link (to offer the choice).</summary>
    Task<bool> CanPublishAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<FormDto> UpdateAsync(Guid formId, SaveFormRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the public link: the old one stops working.</summary>
    Task<FormDto> ResetPublicLinkAsync(Guid formId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid formId, CancellationToken cancellationToken = default);

    /// <summary>The form to fill in, for someone signed in who is in its audience.</summary>
    Task<FillFormDto> GetForFillingAsync(Guid formId, CancellationToken cancellationToken = default);

    /// <summary>Adds the row. Throws <see cref="ArgumentException"/> with a plain message for missing or invalid answers.</summary>
    Task<FormSubmittedDto> SubmitAsync(Guid formId, SubmitFormRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Public forms, for the anonymous page at <c>/f/{token}</c> (web host only; not on the API). Unknown, closed or
/// no-longer-public tokens are "not found". Responses are rate limited per client and per form, and a filled-in honeypot
/// field is quietly thrown away.
/// </summary>
public interface IPublicFormService
{
    Task<FillFormDto?> GetAsync(string token, CancellationToken cancellationToken = default);

    /// <summary><paramref name="clientAddress"/> is only hashed with the form for rate limits; it isn't stored.</summary>
    Task<FormSubmittedDto> SubmitAsync(string token, SubmitFormRequest request, string? honeypot, string? clientAddress, CancellationToken cancellationToken = default);
}
