using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Workbench.Application.Receipts;

/// <summary>
/// Reads a PDF's text layer (PdfPig) into lines in reading order: words that share a baseline become one line, left to
/// right, with two spaces where there's a column gap. Scanned PDFs and images have no text layer and give no lines.
/// Nothing leaves the server.
/// </summary>
public static class ReceiptText
{
    /// <summary>Folios are a page or two; past this the text is a statement or something else and isn't worth reading.</summary>
    public const int MaxPages = 10;

    public static bool IsPdf(string fileName, ReadOnlySpan<byte> content) =>
        content.StartsWith("%PDF"u8) || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PDF's lines, or an empty list when it has no text layer or can't be read (damaged, encrypted).</summary>
    public static IReadOnlyList<string> ReadPdfLines(byte[] content, int maxPages = MaxPages)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            var lines = new List<string>();
            var pageNumber = 0;
            foreach (var page in document.GetPages())
            {
                if (++pageNumber > maxPages)
                {
                    break;
                }

                lines.AddRange(PageLines(page));
            }

            return lines;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static IEnumerable<string> PageLines(Page page)
    {
        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
        {
            yield break;
        }

        // Top to bottom; a word joins the current line when its baseline is within half a line height of the line's.
        var rows = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var row = rows.Count == 0 ? null : rows[^1];
            var height = Math.Max(word.BoundingBox.Height, 4);
            if (row is not null && Math.Abs(row[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= height * 0.5)
            {
                row.Add(word);
            }
            else
            {
                rows.Add([word]);
            }
        }

        foreach (var row in rows)
        {
            var text = new StringBuilder();
            Word? previous = null;
            foreach (var word in row.OrderBy(w => w.BoundingBox.Left))
            {
                if (previous is not null)
                {
                    var charWidth = previous.BoundingBox.Width / Math.Max(1, previous.Text.Length);
                    text.Append(word.BoundingBox.Left - previous.BoundingBox.Right > charWidth * 2.5 ? "  " : " ");
                }

                text.Append(word.Text);
                previous = word;
            }

            yield return text.ToString();
        }
    }
}
