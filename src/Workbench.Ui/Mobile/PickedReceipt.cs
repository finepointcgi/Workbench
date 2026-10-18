namespace Workbench.Ui.Mobile;

/// <summary>A receipt picked on the phone, read into memory until it's attached to an expense line.</summary>
public sealed record PickedReceipt(string FileName, byte[] Content)
{
    public string SizeText => Content.Length >= 1024 * 1024 ? $"{Content.Length / (1024d * 1024):0.0} MB" : $"{Math.Max(1, Content.Length / 1024)} KB";
}
