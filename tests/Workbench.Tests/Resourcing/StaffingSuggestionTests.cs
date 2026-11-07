using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Assistant;
using Workbench.Application.Resourcing;
using Workbench.Contracts;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Tasks;
using Workbench.Application.Identity;
using Workbench.Domain.Assistant;
using Workbench.Domain.Identity;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// Suggesting who to put on a task: the rules (free hours, skills the task names, labor category), the assistant on top of
/// them, the people who are left out, and who may ask. Today is Wed Oct 14 2026; Design runs Oct 12–23.
/// </summary>
public sealed class StaffingSuggestionTests
{
    private static SuggestionCandidate Person(string name, decimal free, decimal capacity = 40, params EmployeeSkillDto[] skills) =>
        new(Guid.NewGuid(), name, free, capacity, null, null, false, skills);

    private static EmployeeSkillDto Skill(string name, SkillLevel level) => new(Guid.NewGuid(), name, null, level, null);

    [Fact]
    public void Skills_the_task_names_count_and_more_free_hours_beat_fewer()
    {
        var sql = StaffingSuggestionRules.Score(Person("Sam", 40, 40, Skill("SQL", SkillLevel.Advanced)), "Migrate the SQL reporting database", 40, null);
        var plain = StaffingSuggestionRules.Score(Person("Pat", 40), "Migrate the SQL reporting database", 40, null);
        var busy = StaffingSuggestionRules.Score(Person("Bo", 10, 40, Skill("SQL", SkillLevel.Advanced)), "Migrate the SQL reporting database", 40, null);

        Assert.True(sql.Score > plain.Score);
        Assert.Contains("SQL (Advanced)", sql.Why);
        Assert.True(sql.Score > busy.Score);
        Assert.Contains(busy.Concerns, c => c.Contains("only 10 of the 40 h", StringComparison.Ordinal));
        Assert.Empty(sql.Concerns);
    }

    [Theory]
    [InlineData("Build the C# API", "C#", true)]
    [InlineData("Build the JavaScript front end", "Java", false)]
    [InlineData("Cost accounting review", "cost accounting", true)]
    [InlineData("Costing review", "cost", false)]
    [InlineData("", "SQL", false)]
    public void A_skill_matches_only_as_a_whole_word_or_phrase(string task, string skill, bool expected) =>
        Assert.Equal(expected, StaffingSuggestionRules.Mentions(task, skill));

    [Fact]
    public async Task The_person_with_the_skill_the_task_names_is_suggested_first_over_the_tasks_own_dates()
    {
        await using var host = await CreateAsync();
        await host.As(ResourceManager).Get<ISkillService>().SetEmployeeSkillAsync(host.AnalystId, new SetEmployeeSkillRequest("Design", SkillLevel.Expert));

        var result = await host.As(Pm).Get<IStaffingSuggestionService>().SuggestAsync(new SuggestStaffRequest(host.Project1, host.Design, 20));

        Assert.Equal((Today, new DateOnly(2026, 10, 23)), (result.From, result.To));
        Assert.False(result.AssistantUsed);
        Assert.InRange(result.Suggestions.Count, 1, 3);
        Assert.Equal(host.AnalystId, result.Suggestions[0].EmployeeId);
        Assert.Contains("Design (Expert)", result.Suggestions[0].Why);
    }

    [Fact]
    public async Task Someone_booked_solid_in_the_window_is_not_suggested_and_someone_already_on_the_task_is_left_out()
    {
        await using var host = await CreateAsync();
        await using (var users = await host.Db.CreateAsync())
        {
            await RoleStore.AddUserAsync(users, new AppUser { Email = Analyst, DisplayName = "Alex" }, OrgRoles.Employee);
            await users.SaveChangesAsync();
        }

        await host.As(ResourceManager).Get<ISkillService>().SetEmployeeSkillAsync(host.AnalystId, new SetEmployeeSkillRequest("Design", SkillLevel.Expert));
        var suggest = host.As(Pm).Get<IStaffingSuggestionService>();
        Assert.Contains(host.AnalystId, (await suggest.SuggestAsync(new SuggestStaffRequest(host.Project1, host.Design, 20))).Suggestions.Select(s => s.EmployeeId));

        // Analyst goes on the task; they're no longer a suggestion for it.
        await using var db = await host.Db.CreateAsync();
        var item = await db.Items.Where(i => i.SheetId == host.SheetId && i.Title == "Design").Select(i => i.Id).SingleAsync();
        await host.As(Pm).Get<ITaskAssignmentService>().SetAssigneesAsync(host.SheetId, item, new SetAssigneesRequest([Analyst]));

        var after = await suggest.SuggestAsync(new SuggestStaffRequest(host.Project1, host.Design, 20));
        Assert.DoesNotContain(host.AnalystId, after.Suggestions.Select(s => s.EmployeeId));
    }

    [Fact]
    public async Task With_the_assistant_on_it_picks_and_the_interaction_is_audited_and_off_it_falls_back_to_the_rules()
    {
        await using var host = await CreateAsync(s => s.Configure<AssistantOptions>(o => o.EnableFakeProvider = true));
        await host.As(ResourceManager).Get<ISkillService>().SetEmployeeSkillAsync(host.AnalystId, new SetEmployeeSkillRequest("Design", SkillLevel.Expert));
        var request = new SuggestStaffRequest(host.Project1, host.Design, 20);
        var off = await host.As(Pm).Get<IStaffingSuggestionService>().SuggestAsync(request);

        await host.As(Admin).Get<IAssistantAdminService>().SaveSettingsAsync(new SaveAssistantSettingsRequest(true, AssistantOptions.FakeConnection, null, AcceptNotice: true));
        var on = await host.As(Pm).Get<IStaffingSuggestionService>().SuggestAsync(request);

        Assert.False(off.AssistantUsed);
        Assert.True(on.AssistantUsed);
        Assert.All(on.Suggestions, s => Assert.True(s.ByAssistant));
        Assert.Equal(off.Suggestions.Select(s => s.EmployeeId), on.Suggestions.Select(s => s.EmployeeId));
        await using var db = await host.Db.CreateAsync();
        var interaction = await db.Set<AssistantInteraction>().AsNoTracking().SingleAsync(i => i.Kind == AssistantInteractionKind.StaffingSuggestion);
        Assert.Equal(AssistantOutcome.Answered, interaction.Outcome);
    }

    [Fact]
    public void The_assistants_answer_is_validated_before_it_is_used()
    {
        var a = new StaffingSuggestionDto(Guid.NewGuid(), "Ann", 80, "rules a", [], 30, false);
        var b = new StaffingSuggestionDto(Guid.NewGuid(), "Bob", 70, "rules b", ["short on hours"], 12, false);
        var json = $$"""{"picks":[{"id":"{{b.EmployeeId}}","reason":"  Best skills.  "},{"id":"{{Guid.NewGuid()}}","reason":"made up"},{"id":"{{b.EmployeeId}}","reason":"again"},{"id":"{{a.EmployeeId}}","reason":""}]}""";

        var parsed = StaffingSuggestionService.TryParse(json, [a, b])!;

        var one = Assert.Single(parsed);
        Assert.Equal((b.EmployeeId, "Best skills.", true, 70), (one.EmployeeId, one.Why, one.ByAssistant, one.Score));
        Assert.Equal(["short on hours"], one.Concerns);
        Assert.Null(StaffingSuggestionService.TryParse("not json", [a, b]));
        Assert.Null(StaffingSuggestionService.TryParse("""{"picks":[]}""", [a, b]));
        _ = JsonSerializer.Serialize(StaffingSuggestionService.Schema());
    }

    [Fact]
    public async Task Only_people_who_assign_on_the_project_can_ask()
    {
        await using var host = await CreateAsync();
        var request = new SuggestStaffRequest(host.Project1, host.Design);

        await host.As(ResourceManager).Get<IStaffingSuggestionService>().SuggestAsync(request);
        await host.As(Pm).Get<IStaffingSuggestionService>().SuggestAsync(request);
        await Refused(() => host.As(Dev).Get<IStaffingSuggestionService>().SuggestAsync(request));
        await Refused(() => host.As(Pm2).Get<IStaffingSuggestionService>().SuggestAsync(request));
    }

    /// <summary>Not allowed, or not even visible (a project you can't see answers "not found").</summary>
    private static async Task Refused(Func<Task> act)
    {
        var ex = await Record.ExceptionAsync(act);
        Assert.True(ex is WorkbenchForbiddenException or WorkbenchNotFoundException, ex?.ToString() ?? "no exception");
    }
}
