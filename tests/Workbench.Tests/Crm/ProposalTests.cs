using DocumentFormat.OpenXml.Packaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Assistant;
using Workbench.Application.Crm;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Crm;
using Workbench.Domain.Assistant;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Crm;

/// <summary>Proposal drafting: the template, what stays out of it, edits surviving a re-draft, the assistant, Word export and access.</summary>
public sealed class ProposalTests
{
    private static IProposalService Proposals(CrmKit kit) => kit.Get<IProposalService>();

    private static async Task<OpportunityDto> PursuitAsync(CrmKit kit)
    {
        var o = await kit.NewAsync("Cloud migration", solicitation: "W91QUZ-27-R-0012", capture: CrmKit.Bd, proposal: CrmKit.Bd2);
        kit.As(CrmKit.Bd);
        var plan = kit.Get<ICapturePlanService>();
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.WinTheme, "Zero-downtime migration"));
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Discriminator, "Cleared DevSecOps bench", "40 engineers"));
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.HotButton, "Keep mission systems online"));
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Strength, "Prior Army cloud work"));
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Weakness, "No FedRAMP High authorization yet"));
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Threat, "Incumbent Acme has the CIO's ear"));
        await plan.AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Risk, "Key architect may leave"));
        return o;
    }

    [Fact]
    public async Task A_draft_has_the_six_standard_sections_built_from_the_plan_with_placeholders_where_facts_run_out()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);

        var result = await Proposals(kit).DraftAsync(o.Id);

        Assert.Equal(6, result.Created);
        Assert.Equal(0, result.WrittenByAssistant);
        Assert.Equal(["summary", "requirement", "approach", "management", "past", "risks"], result.Proposal.Sections.Select(s => s.Key));
        var all = string.Join("\n", result.Proposal.Sections.Select(s => s.Content));
        Assert.Contains("Zero-downtime migration", all);
        Assert.Contains("Cleared DevSecOps bench: 40 engineers", all);
        Assert.Contains("Keep mission systems online", all);
        Assert.Contains("Prior Army cloud work", all);
        Assert.Contains("W91QUZ-27-R-0012", all);
        Assert.Contains("[ADD:", result.Proposal.Sections.Single(s => s.Key == "past").Content);
        Assert.All(result.Proposal.Sections, s => Assert.False(s.ByAssistant));
    }

    [Fact]
    public async Task Internal_notes_never_reach_the_draft()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        await kit.Get<ICapturePlanService>().AddItemAsync(o.Id, new SavePlanItemRequest(CapturePlanItemKind.Weakness, "Priced 12% above the incumbent"));
        kit.As(CrmKit.Bd);
        await kit.Opportunities.UpdateAsync(o.Id, new SaveOpportunityRequest("Cloud migration", EstimatedValue: 987_654m, IncumbentName: "Acme Corp", CaptureManagerEmail: CrmKit.Bd, ProposalManagerEmail: CrmKit.Bd2));

        var all = string.Join("\n", (await Proposals(kit).DraftAsync(o.Id)).Proposal.Sections.Select(s => s.Content));

        Assert.DoesNotContain("FedRAMP High", all);
        Assert.DoesNotContain("Acme", all);
        Assert.DoesNotContain("Key architect may leave", all);
        Assert.DoesNotContain("12%", all);
        Assert.DoesNotContain("987", all);
        Assert.Contains("Transition:", all);
    }

    [Fact]
    public async Task Drafting_again_keeps_sections_edited_by_hand_and_redrafting_one_replaces_it()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var proposals = Proposals(kit);
        var first = (await proposals.DraftAsync(o.Id)).Proposal;
        var summary = first.Sections.Single(s => s.Key == "summary");

        var saved = await proposals.SaveSectionAsync(summary.Id, new SaveProposalSectionRequest("Executive summary", "Our hand-written summary."));
        Assert.True(saved.EditedByHand);
        var again = await proposals.DraftAsync(o.Id);

        Assert.Equal(0, again.Created);
        Assert.Equal(5, again.Rewritten);
        Assert.Equal(1, again.KeptEdited);
        Assert.Equal("Our hand-written summary.", again.Proposal.Sections.Single(s => s.Key == "summary").Content);

        var redone = await proposals.RedraftSectionAsync(summary.Id);
        Assert.False(redone.EditedByHand);
        Assert.Contains("Zero-downtime migration", redone.Content);
    }

    [Fact]
    public async Task Saving_validates_the_section()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var section = (await Proposals(kit).DraftAsync(o.Id)).Proposal.Sections[0];

        await Assert.ThrowsAsync<ArgumentException>(() => Proposals(kit).SaveSectionAsync(section.Id, new SaveProposalSectionRequest(" ", "x")));
        await Assert.ThrowsAsync<ArgumentException>(() => Proposals(kit).SaveSectionAsync(section.Id, new SaveProposalSectionRequest("T", new string('x', 30_001))));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Proposals(kit).SaveSectionAsync(Guid.NewGuid(), new SaveProposalSectionRequest("T", "x")));
    }

    [Fact]
    public async Task The_assistant_writes_the_sections_when_it_is_on_and_the_request_is_audited()
    {
        await using var kit = await CrmKit.CreateAsync(s => s.Configure<AssistantOptions>(o => o.EnableFakeProvider = true));
        kit.As(Admin);
        await kit.Get<IAssistantAdminService>().SaveSettingsAsync(new SaveAssistantSettingsRequest(true, AssistantOptions.FakeConnection, null, AcceptNotice: true));
        var o = await PursuitAsync(kit);

        var result = await Proposals(kit).DraftAsync(o.Id);

        Assert.Equal(6, result.WrittenByAssistant);
        Assert.All(result.Proposal.Sections, s => Assert.True(s.ByAssistant));
        await using var db = await kit.Factory.CreateAsync();
        var interaction = await db.Set<AssistantInteraction>().AsNoTracking().SingleAsync(i => i.Kind == AssistantInteractionKind.ProposalDraft);
        Assert.Equal(AssistantOutcome.Answered, interaction.Outcome);
    }

    [Fact]
    public void The_assistants_answer_is_validated_before_it_is_used()
    {
        var asked = new HashSet<string> { "summary", "approach" };
        var json = """{"sections":[{"key":"summary","content":"  Good text.  "},{"key":"risks","content":"not asked"},{"key":"approach","content":"  "},{"key":"x","content":"y"}]}""";

        var parsed = ProposalService.TryParse(json, asked);

        Assert.Equal("Good text.", Assert.Single(parsed).Value);
        Assert.Empty(ProposalService.TryParse("not json", asked));
    }

    [Fact]
    public async Task Export_is_a_word_document_with_every_section()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        await Assert.ThrowsAsync<ArgumentException>(() => Proposals(kit).ExportAsync(o.Id));
        await Proposals(kit).DraftAsync(o.Id);

        var file = await Proposals(kit).ExportAsync(o.Id);

        Assert.EndsWith(".docx", file.FileName);
        using var stream = new MemoryStream(file.Content);
        using var doc = WordprocessingDocument.Open(stream, false);
        var text = doc.MainDocumentPart!.Document.InnerText;
        Assert.Contains("Executive summary", text);
        Assert.Contains("Risk management", text);
        Assert.Contains("Zero-downtime migration", text);
    }

    [Fact]
    public async Task Managers_edit_pipeline_readers_read_and_everyone_else_is_refused()
    {
        await using var kit = await CrmKit.CreateAsync();
        var o = await PursuitAsync(kit);
        var proposals = Proposals(kit);

        kit.As(CrmKit.Bd2); // proposal manager
        Assert.True((await proposals.DraftAsync(o.Id)).Proposal.CanEdit);

        kit.As(Finance); // reads the pipeline, doesn't edit
        var seen = await proposals.GetAsync(o.Id);
        Assert.False(seen.CanEdit);
        Assert.Equal(6, seen.Sections.Count);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => proposals.DraftAsync(o.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => proposals.SaveSectionAsync(seen.Sections[0].Id, new SaveProposalSectionRequest("T", "x")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => proposals.RedraftSectionAsync(seen.Sections[0].Id));

        kit.As(Viewer); // an employee with no part in the pursuit
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => proposals.GetAsync(o.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => proposals.ExportAsync(o.Id));
    }
}
