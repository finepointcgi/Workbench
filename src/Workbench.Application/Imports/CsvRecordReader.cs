using System.Text;

namespace Workbench.Application.Imports;

/// <summary>The CSV tokenizer the importers share (and the Costpoint sync's file adapter, in the enterprise module).</summary>
public static class CsvRecordReader
{
    /// <summary>RFC 4180 records: quoted fields may contain commas, doubled quotes and line breaks.</summary>
    public static IEnumerable<(int Line, IReadOnlyList<string> Fields)> ReadRecords(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        line++;
                    }

                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return (recordLine, fields);
                    fields = [];
                    any = false;
                    recordLine = ++line;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (any)
        {
            fields.Add(field.ToString());
            yield return (recordLine, fields);
        }
    }
}
