using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Company;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Reports;
using Workbench.Ui.Financials;
using Workbench.Ui.Navigation;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.CompanyTypes;

/// <summary>
/// Stops government wording creeping back into what a commercial company reads. Three guards:
/// the source of every Razor page (a literal that names a federal term must go through the terminology table),
/// the menu/palette/New catalogs, and server-built text (reports, notifications' wording). Intentional exceptions live in
/// <c>company-wording-allowlist.txt</c> next to this file: add a path (a whole file) or <c>path|text</c> (one literal).
/// How to add a term: docs/admin-guide/company-type.md.
/// </summary>
public sealed class CompanyWordingGuardTests
{
    /// <summary>Words the table knows how to change. A commercial type must not show them unreworded.</summary>
    private static readonly Regex TableTerms = new(
        @"\bCLINs?\b|\b[Cc]ontracts?\b|[Cc]ontract(?:ing)? [Oo]fficer|[Cc]eilings?\b|[Pp]eriod of [Pp]erformance|\b[Ff]und(?:ed|ing)\b|[Uu]nfunded|[Mm]odifications?\b|[Tt]ask orders?\b|[Oo]ption years?\b",
        RegexOptions.CultureInvariant);

    /// <summary>Federal rule sets and systems with no commercial equivalent: those pages are hidden, not reworded.</summary>
    private static readonly Regex FederalNames = new(@"\bWAWF\b|\bDFARS\b|\bDCAA\b|\bSAM\.gov\b|\bFAR\b", RegexOptions.CultureInvariant);

    private static readonly Regex Attribute = new(
        @"(?<![\w@])(Label|Title|Text|Placeholder|HelperText|Subtitle|Description|Header|Tooltip|ToolTip|aria-label|Caption|Hint|Message|AdornmentText|SubTitle|Heading)=""([^""@{}]*)""", RegexOptions.CultureInvariant);

    private static readonly Regex TextNode = new(@">([^<>@{}""]*[A-Za-z][^<>@{}""]*)<", RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Workbench.Server.slnf")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }

    private static (HashSet<string> Files, List<(string Path, string Text)> Literals) Allowlist()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var literals = new List<(string, string)>();
        foreach (var raw in File.ReadAllLines(Path.Combine(RepoRoot(), "tests", "Workbench.Tests", "CompanyTypes", "company-wording-allowlist.txt")))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var bar = line.IndexOf('|');
            if (bar < 0)
            {
                files.Add(line);
            }
            else
            {
                literals.Add((line[..bar].Trim(), line[(bar + 1)..].Trim()));
            }
        }

        return (files, literals);
    }

    [Fact]
    public void Razor_literals_that_name_a_federal_term_go_through_the_terminology_table()
    {
        var root = Path.Combine(RepoRoot(), "src", "Workbench.Ui");
        var (files, literals) = Allowlist();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || files.Contains(relative))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            var code = source.IndexOf("@code", StringComparison.Ordinal);
            var markup = code >= 0 ? source[..code] : source;
            var found = Attribute.Matches(markup).Select(m => m.Groups[2].Value)
                .Concat(TextNode.Matches(markup).Select(m => m.Groups[1].Value.Trim()))
                .Where(t => TableTerms.IsMatch(t) || FederalNames.IsMatch(t));

            foreach (var text in found)
            {
                if (!literals.Any(l => relative.EndsWith(l.Path, StringComparison.OrdinalIgnoreCase) && text.Contains(l.Text, StringComparison.Ordinal)))
                {
                    offenders.Add($"{relative}: \"{text}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These literals name a federal term. Wrap them in Tm.Reword(\"…\") (see docs/admin-guide/company-type.md), or list them in company-wording-allowlist.txt if they are genuinely government-only:\n"
            + string.Join('\n', offenders.Take(200)));
    }

    [Theory]
    [InlineData(CompanyType.ProfessionalServices)]
    [InlineData(CompanyType.EngineeringConstruction)]
    public void The_menu_the_palette_and_the_New_menu_read_in_company_words(CompanyType type)
    {
        var terms = CompanyTerms.For(type);
        var access = new NavAccess(new CurrentUserInfo("admin@example.com", "Admin", OrgRoles.Admin, null), HideGovernment: true);
        var titles = NavCatalog.PaletteEntries(access).SelectMany(e => new[] { e.Title, e.Group ?? "", e.Detail ?? "" })
            .Concat(NavCatalog.Sections.Select(s => s.Title))
            .Concat(NewCatalog.For(access).SelectMany(a => new[] { a.Title, a.PaletteTitle }))
            .Where(t => t.Length > 0);

        // Engineering firms keep "contract" (it is their word); the rest of the table still applies.
        var banned = type == CompanyType.EngineeringConstruction
            ? new Regex(TableTerms.ToString().Replace(@"\b[Cc]ontracts?\b|", ""), RegexOptions.CultureInvariant)
            : TableTerms;
        var offenders = titles.Select(terms.ReplaceTerms).Where(t => banned.IsMatch(t) || FederalNames.IsMatch(t)).Distinct().ToList();

        Assert.True(offenders.Count == 0, "Not reworded: " + string.Join("; ", offenders));
    }

    [Fact]
    public async Task Standard_reports_read_in_company_words_for_a_professional_services_firm()
    {
        await using var kit = await Workbench.Tests.Reports.ReportsKit.CreateAsync();
        await kit.As(Admin).Get<ICompanyProfileService>().UpdateAsync(new UpdateCompanyProfileRequest(CompanyType.ProfessionalServices, null));
        var standard = kit.As(Finance).Standard;

        var texts = new List<string>();
        foreach (var report in await standard.ListAsync())
        {
            texts.AddRange([report.Name, report.Description, report.Category]);
            if (report.Available && !report.PageOnly && report.Key != StandardReportKeys.ProjectStatus)
            {
                var result = await standard.RunAsync(report.Key, Workbench.Tests.Reports.ReportsKit.Day with { });
                texts.AddRange([result.Title, result.Subtitle ?? "", .. result.Columns.Select(c => c.Label), .. result.Notes]);
            }
        }

        var allowed = Allowlist().Literals.Where(l => l.Path == "server-text").Select(l => l.Text).ToList();
        var offenders = texts.Where(t => TableTerms.IsMatch(t) && !allowed.Any(a => t.Contains(a, StringComparison.Ordinal))).Distinct().ToList();

        Assert.True(offenders.Count == 0, "Reports still say: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Server_built_sentences_are_reworded_where_they_are_stored()
    {
        // Notifier and MyTodoService run every title and body through ReplaceTerms; this keeps the table able to handle the sentences they build.
        var terms = CompanyTerms.For(CompanyType.ProfessionalServices);
        string[] sentences =
        [
            "Contract C-100 has no funded value",
            "Funding for contract C-100 is 90% used; the ceiling is $1,000.",
            "Add the CLINs and period of performance before the plan can go live.",
            "Modification P0003 was executed against the contract",
            "The contracting officer asked for a new task order",
        ];

        Assert.All(sentences.Select(terms.ReplaceTerms), s => Assert.DoesNotMatch(TableTerms, s));
    }

    [Fact]
    public async Task A_funding_panel_reads_in_company_words()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var funding = new ContractFundingDto(Guid.NewGuid(), "C-1", "Harbor", Workbench.Contracts.ProjectAccounting.ContractKind.TimeAndMaterials, new DateOnly(2027, 1, 1), 1, 100m, 80m, 20m, 60m, 80m, 0.25m, 5m, null, new DateOnly(2026, 11, 1), []);

        var page = ctx.Render<FundingPanel>(p =>
        {
            p.Add(f => f.Funding, funding);
            p.AddCascadingValue<ITerms>(CompanyTerms.For(CompanyType.ProfessionalServices));
        });

        Assert.DoesNotMatch(TableTerms, page.Nodes.Select(n => n.TextContent).Aggregate("", (a, b) => a + " " + b));
        Assert.Contains("authorized budget", page.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snackbar_messages_follow_the_company_type()
    {
        using var ctx = new BunitContext();
        ctx.Services.AddMudServices();
        Workbench.Ui.Company.TermsSnackbarRegistration.AddCompanyWordsInSnackbars(ctx.Services);
        var snackbar = ctx.Services.GetRequiredService<MudBlazor.ISnackbar>();
        Assert.IsType<Workbench.Ui.Company.TermsSnackbar>(snackbar);
        ((Workbench.Ui.Company.TermsSnackbar)snackbar).Terms = CompanyTerms.For(CompanyType.ProfessionalServices);

        snackbar.Add("This ceiling was deactivated; the contract is unchanged.");

        var shown = Assert.Single(snackbar.ShownSnackbars);
        Assert.Contains("not-to-exceed", shown.Message.ToString() ?? "");
        Assert.DoesNotContain("ceiling", shown.Message.ToString() ?? "");
    }
}
