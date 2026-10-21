namespace Workbench.Ui.Customization;

/// <summary>
/// What a field's markup needs from the layout. Bind <see cref="Required"/> to the input's <c>Required</c> on optional fields
/// (an administrator may make them required); fields the domain needs keep their own <c>Required="true"</c>.
/// </summary>
public sealed record FormFieldContext(string Key, string Label, bool Required);
