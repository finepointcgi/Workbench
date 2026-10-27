namespace Workbench.Tests.Setup;

/// <summary>Every date picker takes dates typed the same way (yyyy-MM-dd), so a date that works in one form works in all of them.</summary>
public sealed class DatePickerFormatTests
{
    [Fact]
    public void Every_date_picker_sets_its_format()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Workbench.Ui")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var without = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Workbench.Ui"), "*.razor", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            for (var at = text.IndexOf("<MudDatePicker", StringComparison.Ordinal); at >= 0; at = text.IndexOf("<MudDatePicker", at + 1, StringComparison.Ordinal))
            {
                char? quote = null;
                var end = at + "<MudDatePicker".Length;
                for (; end < text.Length; end++)
                {
                    var ch = text[end];
                    if (quote is not null)
                    {
                        quote = ch == quote ? null : quote;
                    }
                    else if (ch is '"' or '\'')
                    {
                        quote = ch;
                    }
                    else if (ch == '>' && text[end - 1] != '=')
                    {
                        break;
                    }
                }

                if (!text[at..Math.Min(end + 1, text.Length)].Contains("DateFormat", StringComparison.Ordinal))
                {
                    without.Add($"{Path.GetRelativePath(root.FullName, file)}:{text[..at].Count(c => c == '\n') + 1}");
                }
            }
        }

        Assert.True(without.Count == 0, "Date pickers without a DateFormat (add DateFormat=\"yyyy-MM-dd\"): " + string.Join(", ", without));
    }

    [Fact]
    public void Billing_details_still_missing_are_named_in_the_pages_words()
    {
        var empty = new Workbench.Contracts.Billing.ContractBillingSettingsDto(Guid.NewGuid(), "INV", 0m, null, null, null, null, null, null, 30, null);
        Assert.Equal(["the CAGE code", "the paying office", "the administering office", "where to send the invoice"], Workbench.Ui.Setup.SetupFormat.MissingBillingDetails(empty));
        Assert.Empty(Workbench.Ui.Setup.SetupFormat.MissingBillingDetails(empty with { CageCode = "1ABC2", PayingOffice = "DFAS", AdminOffice = "DCMA", CustomerBillingAddress = "PO Box 1" }));
    }
}
