using System.Text;

namespace Workbench.Ui.Grid;

/// <summary>
/// The clipboard's text form of a block of cells, as Excel, Google Sheets and Smartsheet write it: rows separated by
/// new lines, cells by tabs, and a cell holding a tab, new line or quote wrapped in quotes (with quotes doubled).
/// </summary>
public static class ClipboardText
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var atCellStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when atCellStart:
                    quoted = true;
                    atCellStart = false;
                    break;
                case '\t':
                    row.Add(cell.ToString());
                    cell.Clear();
                    atCellStart = true;
                    break;
                case '\r' or '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    atCellStart = true;
                    break;
                default:
                    cell.Append(c);
                    atCellStart = false;
                    break;
            }
        }

        // Excel ends a copied block with a new line; don't turn it into an empty last row.
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    public static string Format(IEnumerable<IEnumerable<string?>> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.AppendJoin('\t', row.Select(Quote)).Append("\r\n");
        }

        return text.ToString();
    }

    private static string Quote(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny(['\t', '\n', '\r', '"']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
