using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Assistant;
using Workbench.Application.Crm;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Crm;
using Workbench.Domain.Assistant;
using static Workbench.Tests.Identity.SeededHost;
using D = Workbench.Domain.Crm;

namespace Workbench.Tests.Crm;

/// <summary>The solicitation shredder: the rules parser, the matrix, dates on the schedule, files, the assistant, the proposal and access.</summary>
public sealed class RequirementTests
{
    private const string Solicitation = """
        Section C - Statement of Work
        C.3.1 The contractor shall provide 24x7 monitoring of mission networks and report incidents within 15 minutes.
        C.3.2 The contractor shall migrate legacy applications to the cloud with no more than 4 hours of downtime.
        Background. The agency operates 40 systems across three sites.

        SECTION L - INSTRUCTIONS TO OFFERORS
        L.1 Proposals shall be submitted in four volumes, and the technical volume shall not exceed 25 pages.
        L.2 The offeror shall describe its approach to transition and key personnel in the management volume.
        Questions are due by October 30, 2026 at 2:00 PM Eastern.
        Proposals are due November 20, 2026.

        SECTION M - EVALUATION FACTORS FOR AWARD
        M.1 Technical approach will be evaluated for soundness and completeness of the proposed solution.
        M.2 Past performance will be evaluated on relevance and recency of three references.
        """;

    [Fact]
    public void The_parser_keeps_shall_statements_with_their_reference_and_kind_from_the_section_they_sit_in()
    {
        var (items, _) = SolicitationParser.Parse(Solicitation);

        Assert.Equal(6, items.Count);
        var monitoring = items.Single(i => i.Text.Contains("24x7 monitoring"));
        Assert.Equal(("C.3.1", D.RequirementKind.Technical, "approach"), (monitoring.Reference, monitoring.Kind, monitoring.SectionKey));
        Assert.Equal(D.RequirementKind.Instruction, items.Single(i => i.Reference == "L.1").Kind);
        Assert.Equal(D.RequirementKind.Evaluation, items.Single(i => i.Reference == "M.1").Kind);
        Assert.Equal("past", items.Single(i => i.Reference == "M.2").SectionKey);
        Assert.Equal("management", items.Single(i => i.Reference == "L.2").SectionKey);
        Assert.DoesNotContain(items, i => i.Text.Contains("operates 40 systems", StringComparison.Ordinal));
        Assert.All(items, i => Assert.DoesNotMatch(@"^[A-M]\.\d", i.Text)); // the reference is its own field
    }

    [Fact]
    public void Wrapped_lines_join_and_repeated_sentences_count_once()
    {
        var text = "C.1 The contractor shall maintain the\nsystem of record and provide\nmonthly status reports.\n\nC.2 The contractor shall maintain the system of record and provide monthly status reports.";

        var (items, _) = SolicitationParser.Parse(text);

        var one = Assert.Single(items);
        Assert.Equal("The contractor shall maintain the system of record and provide monthly status reports.", one.Text);
    }

    [Fact]
    public void Dates_are_read_from_lines_that_say_what_is_due()
    {
        var (_, dates) = SolicitationParser.Parse(Solicitation);

        Assert.Equal(new DateOnly(2026, 10, 30), dates.Single(d => d.Kind == MilestoneKind.QuestionsDue).Date);
        Assert.Equal(new DateOnly(2026, 11, 20), dates.Single(d => d.Kind == MilestoneKind.Submission).Date);
        Assert.Equal(2, dates.Count);
        Assert.Empty(SolicitationParser.Dates("Nothing is due. Posted on 3/4/2026 for reference."));
        Assert.Equal(new DateOnly(2027, 1, 5), SolicitationParser.Dates("Industry day: 2027-01-05").Single().Date);
    }

    [Fact]
    public void Plain_text_without_cues_gives_nothing_and_the_cap_holds()
    {
        Assert.Empty(SolicitationParser.Parse("This is a notice about a thing. It is nice.").Requirements);
        var many = string.Join("\n\n", Enumerable.Range(1, 500).Select(i => $"The contractor shall deliver report number {i} on time."));
        Assert.Equal(SolicitationParser.MaxRequirements, SolicitationParser.Parse(many).Requirements.Count);
    }

    private static async Task<OpportunityDto> PursuitAsync(CrmKit kit)
    {
        var o = await kit.NewAsync("Cloud migration", solicitation: "W91QUZ-27-R-0012", capture: CrmKit.Bd, proposal: CrmKit.Bd2);
        kit.As(CrmKit.Bd);
        return o;
    }

    [Fact]
    public async Task Reading_a_solicitation_fills_the_matrix_adds_dates_to_the_schedule_and_skips_what_it_already_has()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var service = kit.Get<IRequirementService>();

        var first = await service.ShredAsync(o.Id, new ShredRequest(Solicitation));

        Assert.Equal((6, 0, 0), (first.Added, first.Duplicates, first.ByAssistant));
        Assert.Equal(6, first.Requirements.Items.Count);
        Assert.All(first.Requirements.Items, i => Assert.Equal(RequirementStatus.Open, i.Status));
        Assert.Equal([true, true], first.Dates.Select(d => d.Added));
        var plan = await kit.Get<ICapturePlanService>().GetAsync(o.Id);
        Assert.Contains(plan.Milestones, m => m.Kind == MilestoneKind.Submission && m.DueOn == new DateOnly(2026, 11, 20));

        // Reading an amendment adds only what's new, and doesn't add a second Submission milestone.
        var amended = await service.ShredAsync(o.Id, new ShredRequest(Solicitation + "\nL.9 The offeror shall include a one-page executive summary. Proposals are due December 4, 2026."));
        Assert.Equal((1, 6), (amended.Added, amended.Duplicates));
        Assert.DoesNotContain(amended.Dates, d => d.Added);
        Assert.Single((await kit.Get<ICapturePlanService>().GetAsync(o.Id)).Milestones, m => m.Kind == MilestoneKind.Submission);
    }

    [Fact]
    public async Task Lines_can_be_marked_addressed_moved_to_a_section_added_and_removed_and_the_matrix_exports_to_csv()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var service = kit.Get<IRequirementService>();
        var read = await service.ShredAsync(o.Id, new ShredRequest(Solicitation));
        var line = read.Requirements.Items.Single(i => i.Reference == "L.1");

        var saved = await service.UpdateAsync(line.Id, new SaveRequirementRequest(line.Text, line.Kind, RequirementStatus.Addressed, line.Reference, "summary", "See Vol 1, p. 2"));
        var added = await service.AddAsync(o.Id, new SaveRequirementRequest("=HYPERLINK(\"http://x\")", RequirementKind.Administrative, RequirementStatus.Open));
        await service.DeleteAsync(read.Requirements.Items.Single(i => i.Reference == "M.2").Id);

        Assert.Equal((RequirementStatus.Addressed, "summary", "See Vol 1, p. 2"), (saved.Status, saved.ProposalSectionKey, saved.Notes));
        var matrix = await service.GetAsync(o.Id);
        Assert.Equal(6, matrix.Items.Count); // 6 read, 1 added, 1 removed
        Assert.Equal(added.Id, matrix.Items[^1].Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(o.Id, new SaveRequirementRequest(" ", RequirementKind.Technical, RequirementStatus.Open)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(line.Id, new SaveRequirementRequest("x", line.Kind, line.Status, null, "nonsense")));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.DeleteAsync(Guid.NewGuid()));

        var file = await service.ExportAsync(o.Id);
        var csv = Encoding.UTF8.GetString(file.Content);
        Assert.Equal("text/csv", file.ContentType);
        Assert.StartsWith("compliance-matrix-W91QUZ-27-R-0012", file.FileName, StringComparison.Ordinal);
        Assert.Contains("Reference,Requirement,Type,Proposal section,Status,Notes", csv);
        Assert.Contains("\"L.1\"", csv);
        Assert.Contains("\"Executive summary\",\"Addressed\",\"See Vol 1, p. 2\"", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", csv); // formulas are defanged
    }

    [Fact]
    public async Task A_word_file_and_a_text_file_are_read_like_pasted_text_and_bad_files_say_why()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var service = kit.Get<IRequirementService>();
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("L.4 The offeror shall provide a staffing plan."))),
                new Paragraph(new Run(new Text("Proposals are due November 20, 2026.")))));
        }

        var fromDocx = await service.ShredAsync(o.Id, new ShredRequest(null, "rfp.docx", stream.ToArray()));
        var fromText = await service.ShredAsync(o.Id, new ShredRequest(null, "notes.txt", Encoding.UTF8.GetBytes("C.9 The contractor shall back up data nightly.")));

        Assert.Equal(1, fromDocx.Added);
        Assert.Equal("L.4", fromDocx.Requirements.Items.Single().Reference);
        Assert.Equal(1, fromText.Added);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ShredAsync(o.Id, new ShredRequest(null, "scan.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 not really a pdf"))));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ShredAsync(o.Id, new ShredRequest("   ")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ShredAsync(o.Id, new ShredRequest(null, "empty.txt", [])));
    }

    [Fact]
    public async Task A_pdf_with_a_text_layer_is_read_and_one_without_says_so()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter);
        var y = 700.0;
        foreach (var line in new[] { "SECTION L - INSTRUCTIONS TO OFFERORS", "L.1 The offeror shall submit a technical volume of no more", "than 25 pages.", "Proposals are due November 20, 2026." })
        {
            page.AddText(line, 11, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 16;
        }

        var result = await kit.Get<IRequirementService>().ShredAsync(o.Id, new ShredRequest(null, "rfp.pdf", builder.Build()));

        var line1 = Assert.Single(result.Requirements.Items);
        Assert.Equal(("L.1", RequirementKind.Instruction), (line1.Reference, line1.Kind));
        Assert.Equal("The offeror shall submit a technical volume of no more than 25 pages.", line1.Text);
        Assert.Contains(result.Dates, d => d.Kind == MilestoneKind.Submission && d.Date == new DateOnly(2026, 11, 20));

        var blank = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        blank.AddPage(UglyToad.PdfPig.Content.PageSize.Letter);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Get<IRequirementService>().ShredAsync(o.Id, new ShredRequest(null, "scan.pdf", blank.Build())));
        Assert.Contains("no text layer", ex.Message);
    }

    [Fact]
    public async Task With_the_assistant_on_it_sorts_the_lines_and_the_interaction_is_audited()
    {
        await using var kit = await CrmKit.CreateAsync(s => s.Configure<AssistantOptions>(o => o.EnableFakeProvider = true));
        kit.As(Admin);
        await kit.Get<IAssistantAdminService>().SaveSettingsAsync(new SaveAssistantSettingsRequest(true, AssistantOptions.FakeConnection, null, AcceptNotice: true));
        var o = await PursuitAsync(kit);

        var result = await kit.Get<IRequirementService>().ShredAsync(o.Id, new ShredRequest(Solicitation));

        Assert.Equal(6, result.Added);
        Assert.Equal(6, result.ByAssistant);
        Assert.All(result.Requirements.Items, i => Assert.True(i.ByAssistant));
        await using var db = await kit.Factory.CreateAsync();
        var interaction = await db.Set<AssistantInteraction>().AsNoTracking().SingleAsync(i => i.Kind == AssistantInteractionKind.RequirementReading);
        Assert.Equal(AssistantOutcome.Answered, interaction.Outcome);
    }

    [Fact]
    public void The_assistants_answer_is_validated_before_it_is_used()
    {
        var json = """{"items":[{"id":"0","keep":false,"kind":"Technical","section":"approach"},{"id":"1","keep":true,"kind":"Bogus","section":"nonsense"},{"id":"1","keep":true,"kind":"Evaluation","section":"PAST"},{"id":"9","keep":true,"kind":"Technical","section":""},{"id":"x","keep":true,"kind":"Technical","section":""}]}""";

        var parsed = RequirementService.TryParse(json, 3)!;

        Assert.False(parsed[0].Keep);
        Assert.Equal((true, D.RequirementKind.Evaluation, "past"), (parsed[1].Keep, parsed[1].Kind, parsed[1].Section)); // the last answer for an id wins
        Assert.DoesNotContain(9, parsed.Keys);
        Assert.Equal(2, parsed.Count);
        Assert.Null(RequirementService.TryParse("not json", 3));
        Assert.Null(RequirementService.TryParse("""{"items":[]}""", 3));
    }

    [Fact]
    public async Task The_proposal_draft_lists_what_the_solicitation_asks_and_skips_lines_marked_not_applicable()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var service = kit.Get<IRequirementService>();
        var read = await service.ShredAsync(o.Id, new ShredRequest(Solicitation));
        var skipped = read.Requirements.Items.Single(i => i.Reference == "M.2");
        await service.UpdateAsync(skipped.Id, new SaveRequirementRequest(skipped.Text, skipped.Kind, RequirementStatus.NotApplicable, skipped.Reference));

        var draft = await kit.Get<IProposalService>().DraftAsync(o.Id);

        var understanding = draft.Proposal.Sections.Single(s => s.Key == "requirement").Content;
        Assert.Contains("How the solicitation says proposals will be judged", understanding);
        Assert.Contains("M.1: Technical approach will be evaluated", understanding);
        Assert.Contains("L.1: Proposals shall be submitted in four volumes", understanding);
        Assert.DoesNotContain("24x7 monitoring", understanding); // work statement lines aren't proposal instructions
        Assert.DoesNotContain("relevance and recency", understanding);
    }

    [Fact]
    public async Task Whoever_may_see_the_opportunity_reads_the_matrix_and_only_editors_change_it()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var read = await kit.Get<IRequirementService>().ShredAsync(o.Id, new ShredRequest(Solicitation));
        var line = read.Requirements.Items[0];

        kit.As(Viewer); // an employee with no part in the pursuit
        var service = kit.Get<IRequirementService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.GetAsync(o.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ShredAsync(o.Id, new ShredRequest(Solicitation)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.UpdateAsync(line.Id, new SaveRequirementRequest("x", line.Kind, line.Status)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.DeleteAsync(line.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ExportAsync(o.Id));

        kit.As(CrmKit.Bd2); // the proposal manager edits
        Assert.True((await kit.Get<IRequirementService>().GetAsync(o.Id)).CanEdit);
    }

    [Fact]
    public void The_proposal_sections_a_line_can_point_at_are_the_ones_the_draft_writes() =>
        Assert.Equal(ProposalTemplate.Sections, RequirementLabels.ProposalSections);
}
