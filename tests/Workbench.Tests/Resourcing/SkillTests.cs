using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>Skills on people: typing one adds it to the company list, find-people filters by it, and who may change what.</summary>
public sealed class SkillTests
{
    [Fact]
    public async Task Typing_a_new_skill_adds_it_to_the_company_list_and_matches_existing_ones_by_name_in_any_case()
    {
        await using var host = await CreateAsync();
        var skills = host.As(Pm).Get<ISkillService>();

        var first = await skills.SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest("  Cost   accounting ", SkillLevel.Advanced, 6));
        await host.As(Dev).Get<ISkillService>().SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("cost accounting", SkillLevel.Learning));

        var catalog = await skills.ListSkillsAsync();
        var one = Assert.Single(catalog);
        Assert.Equal("Cost accounting", one.Name);
        Assert.Equal(first.SkillId, one.Id);
        Assert.Equal(2, one.People);
    }

    [Fact]
    public async Task Setting_a_skill_again_changes_the_level_instead_of_adding_a_second_row()
    {
        await using var host = await CreateAsync();
        var skills = host.As(Pm).Get<ISkillService>();
        await skills.SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest("C#", SkillLevel.Working));

        await skills.SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest("c#", SkillLevel.Expert, 10));

        var mine = Assert.Single(await skills.GetEmployeeSkillsAsync(host.PmId));
        Assert.Equal((SkillLevel.Expert, 10m), (mine.Level, mine.Years));
        await skills.RemoveEmployeeSkillAsync(host.PmId, mine.SkillId);
        Assert.Empty(await skills.GetEmployeeSkillsAsync(host.PmId));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task A_skill_needs_a_name(string name)
    {
        await using var host = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.As(Pm).Get<ISkillService>().SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest(name, SkillLevel.Working)));
    }

    [Fact]
    public async Task Find_people_keeps_only_those_with_every_skill_at_the_level_asked_for()
    {
        await using var host = await CreateAsync();
        var rm = host.As(ResourceManager).Get<ISkillService>();
        await rm.SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest("C#", SkillLevel.Advanced));
        await rm.SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest("Azure", SkillLevel.Working));
        await rm.SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("C#", SkillLevel.Learning));
        await rm.SetEmployeeSkillAsync(host.Pm2Id, new SetEmployeeSkillRequest("C#", SkillLevel.Expert));
        var catalog = (await rm.ListSkillsAsync()).ToDictionary(s => s.Name, s => s.Id);
        var planning = host.As(ResourceManager).Get<IResourcePlanningService>();
        FindPeopleQuery Query(SkillLevel min, params string[] names) =>
            new(ThisWeek, ThisWeek.AddDays(13), MinAvailableHours: 1, SkillIds: names.Select(n => catalog[n]).ToList(), MinSkillLevel: min);

        var working = await planning.FindAvailablePeopleAsync(Query(SkillLevel.Working, "C#"));
        var both = await planning.FindAvailablePeopleAsync(Query(SkillLevel.Working, "C#", "Azure"));
        var everyone = await planning.FindAvailablePeopleAsync(new FindPeopleQuery(ThisWeek, ThisWeek.AddDays(13)));

        Assert.Equal(new[] { host.PmId, host.Pm2Id }.Order(), working.Select(p => p.EmployeeId).Order());
        Assert.Equal([host.PmId], both.Select(p => p.EmployeeId));
        Assert.Contains(both[0].Skills!, s => s.Name == "Azure" && s.Level == SkillLevel.Working);
        Assert.True(everyone.Count > working.Count, "Without a skill filter nobody is left out.");
    }

    [Fact]
    public async Task You_change_your_own_skills_but_not_a_colleagues_unless_you_manage_resources()
    {
        await using var host = await CreateAsync();

        await host.As(Dev).Get<ISkillService>().SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("SQL", SkillLevel.Working));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.As(Dev).Get<ISkillService>().SetEmployeeSkillAsync(host.AnalystId, new SetEmployeeSkillRequest("SQL", SkillLevel.Expert)));
        await host.As(ResourceManager).Get<ISkillService>().SetEmployeeSkillAsync(host.AnalystId, new SetEmployeeSkillRequest("SQL", SkillLevel.Expert));

        Assert.Single(await host.As(Pm).Get<ISkillService>().GetEmployeeSkillsAsync(host.AnalystId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Dev).Get<ISkillService>().GetEmployeeSkillsAsync(host.AnalystId));
    }

    [Fact]
    public async Task Only_managers_rename_or_delete_a_skill_and_deleting_takes_it_off_everyone()
    {
        await using var host = await CreateAsync();
        var skillId = (await host.As(Dev).Get<ISkillService>().SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("Sharepoint", SkillLevel.Working))).SkillId;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Dev).Get<ISkillService>().RenameSkillAsync(skillId, new RenameSkillRequest("SharePoint")));
        var renamed = await host.As(ResourceManager).Get<ISkillService>().RenameSkillAsync(skillId, new RenameSkillRequest("SharePoint", "Collaboration"));
        Assert.Equal(("SharePoint", "Collaboration", 1), (renamed.Name, renamed.Category, renamed.People));

        await host.As(ResourceManager).Get<ISkillService>().DeleteSkillAsync(skillId);
        Assert.Empty(await host.As(Pm).Get<ISkillService>().ListSkillsAsync());
        Assert.Empty(await host.As(ResourceManager).Get<ISkillService>().GetEmployeeSkillsAsync(host.DevId));
    }

    [Fact]
    public async Task A_supervisor_sets_the_skills_of_their_direct_reports_and_only_theirs()
    {
        await using var host = await CreateAsync();
        await using (var db = await host.Db.CreateAsync(CancellationToken.None))
        {
            var dev = await db.Set<Workbench.Domain.ProjectAccounting.Employee>().FirstAsync(e => e.Id == host.DevId);
            dev.SupervisorId = host.PmId;
            await db.SaveChangesAsync();
        }

        var asPm = host.As(Pm).Get<ISkillService>();
        await asPm.SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("Azure", SkillLevel.Advanced));

        Assert.Equal("Azure", Assert.Single(await asPm.GetEmployeeSkillsAsync(host.DevId)).Name);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Dev).Get<ISkillService>().SetEmployeeSkillAsync(host.PmId, new SetEmployeeSkillRequest("Azure", SkillLevel.Expert)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => asPm.SetEmployeeSkillAsync(host.Pm2Id, new SetEmployeeSkillRequest("Azure", SkillLevel.Expert)));
    }

    [Fact]
    public async Task The_people_you_can_assign_come_with_their_skills_best_first()
    {
        await using var host = await CreateAsync();
        var rm = host.As(ResourceManager).Get<ISkillService>();
        await rm.SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("SQL", SkillLevel.Working));
        await rm.SetEmployeeSkillAsync(host.DevId, new SetEmployeeSkillRequest("C#", SkillLevel.Expert));

        var candidates = await host.As(Pm).Get<IProjectStaffingService>().ListCandidatesAsync(host.Project1, null);

        Assert.Equal(["C#", "SQL"], candidates.Single(c => c.EmployeeId == host.DevId).Skills!.Select(x => x.Name));
        Assert.Empty(candidates.Single(c => c.EmployeeId == host.PmId).Skills!);
        var everyone = await host.As(ResourceManager).Get<ISkillService>().ListPeopleSkillsAsync();
        Assert.Equal(2, everyone.Single(p => p.EmployeeId == host.DevId).Skills.Count);
        Assert.Empty((await host.As(Dev).Get<ISkillService>().ListPeopleSkillsAsync()).Where(p => p.EmployeeId != host.DevId));
    }
}
