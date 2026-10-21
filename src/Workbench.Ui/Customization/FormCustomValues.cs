namespace Workbench.Ui.Customization;

/// <summary>
/// Custom field values handed back by a dialog that returns a save request (the page creates the record, then saves these
/// with <see cref="ConfigurableForm.SaveCustomFieldsAsync(IServiceProvider, string, Guid, IReadOnlyDictionary{string, object?}?)"/>).
/// Pass one in as a dialog parameter; the dialog fills it when it closes with Save.
/// </summary>
public sealed class FormCustomValues : Dictionary<string, object?>
{
    public FormCustomValues()
        : base(StringComparer.Ordinal)
    {
    }

    public void CopyFrom(ConfigurableForm? form)
    {
        Clear();
        foreach (var (key, value) in form?.CustomValues ?? new Dictionary<string, object?>())
        {
            this[key] = value;
        }
    }
}
