using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using static Workbench.Tests.Identity.ProjectAccountingFixture;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// Two audit findings. Contracts follow project visibility: someone who can't see a project doesn't see its contract (or
/// its funding); Finance and Admin see every contract. An employee's charge codes are for the employee, their supervisor,
/// timekeepers and Finance/Admin; anyone else is refused. Over <see cref="ProjectAccountingFixture"/>: 1001 is on C1 (pm@
/// manages it, analyst@ charges it), 2002 on C2 (outsider@ charges it).
/// </summary>
public sealed class ContractAndChargeCodeAccessTests
{
    private const string ProjectManagerRole = "newpm@example.com";

    [Theory]
    [InlineData(Finance, "C1,C2,C3")]
    [InlineData(Admin, "C1,C2,C3")]
    [InlineData(Owner, "C1")] // manages 1001
    [InlineData(Editor, "C1")] // charges 1001
    [InlineData(Outsider, "C2")] // charges 2002 only
    [InlineData(Timekeeper, "")] // reviews every timesheet, but is on no project
    [InlineData(ProjectManagerRole, "C3")] // the PM role may request the first project on a contract that has none
    public async Task The_contract_list_follows_project_visibility(string email, string expected)
    {
        await using var seeded = await SeedAsync();

        var contracts = await seeded.As(email).Host.Get<IContractService>().ListContractsAsync();

        Assert.Equal(expected, string.Join(",", contracts.Select(c => c.ContractNumber).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task A_contract_on_a_project_the_user_cant_see_is_not_found_and_its_funding_stays_hidden()
    {
        await using var seeded = await SeedAsync();
        var f = seeded.Fixture;

        var outsider = seeded.As(Outsider).Host.Get<IContractService>();
        Assert.Null(await outsider.GetContractAsync(f.Contract1));
        var own = await outsider.GetContractAsync(f.Contract2);
        Assert.NotNull(own);
        Assert.Equal((0m, 0m), (own.CeilingValue, own.FundedValue)); // on the project, but not its manager: no funding

        var finance = await seeded.As(Finance).Host.Get<IContractService>().GetContractAsync(f.Contract1);
        Assert.Equal(400_000m, finance!.FundedValue);
        var pm = await seeded.As(Owner).Host.Get<IContractService>().GetContractAsync(f.Contract1);
        Assert.Equal(400_000m, pm!.FundedValue); // the manager of a project on it
        Assert.Null(await seeded.As(Owner).Host.Get<IContractService>().GetContractAsync(f.Contract2));
    }

    [Theory]
    [InlineData(Editor)] // the employee themselves
    [InlineData(Owner)] // their supervisor
    [InlineData(Timekeeper)]
    [InlineData(Finance)]
    [InlineData(Admin)]
    public async Task An_employees_charge_codes_are_for_them_their_supervisor_timekeepers_and_finance(string email)
    {
        await using var seeded = await SeedAsync();

        var codes = await seeded.As(email).Host.Get<IProjectService>().ListChargeCodesAsync(seeded.Fixture.AnalystId, Day);

        Assert.Equal(["1001.01"], codes.Select(c => c.Code));
    }

    [Theory]
    [InlineData(Outsider)] // no relation to analyst@
    [InlineData(Viewer)] // a co-worker under the same supervisor
    [InlineData(ProjectManagerRole)] // the PM role alone gives no one's codes
    public async Task Anyone_else_is_refused_someones_charge_codes_and_the_refusal_is_logged(string email)
    {
        await using var seeded = await SeedAsync();
        var projects = seeded.As(email).Host.Get<IProjectService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.ListChargeCodesAsync(seeded.Fixture.AnalystId, Day));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => projects.ListChargeCodesForDatesAsync(seeded.Fixture.AnalystId, [Day]));

        await using var db = await seeded.Host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        Assert.True(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == SecurityEventTypes.PermissionDenied && e.Actor == email));
    }

    [Fact]
    public async Task The_timesheet_still_lists_the_signed_in_users_own_codes()
    {
        await using var seeded = await SeedAsync();

        var codes = await seeded.As(Outsider).Host.Get<IProjectService>().ListChargeCodesAsync(seeded.Fixture.OutsiderId, Day);

        Assert.DoesNotContain(codes, c => c.Code == "1001.01");
    }

    private static async Task<Seeded> SeedAsync()
    {
        var seeded = await CreateAsync();
        var f = await ProjectAccountingFixture.SeedAsync(seeded);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        await RoleStore.AddUserAsync(db, new AppUser { Email = ProjectManagerRole, DisplayName = "Nia" }, OrgRoles.ProjectManager);
        var customer = await db.Set<Customer>().FirstAsync();
        db.Set<Contract>().Add(new Contract
        {
            CustomerId = customer.Id, ContractNumber = "C3", Name = "New award", PeriodStart = Day, PeriodEnd = Day.AddYears(1), CeilingValue = 50_000m, FundedValue = 10_000m,
        });
        db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = f.Wbs1, EmployeeId = f.AnalystId });
        await db.SaveChangesAsync();
        return new Seeded(seeded, f);
    }

    private sealed record Seeded(SeededHost Host, ProjectAccountingFixture Fixture) : IAsyncDisposable
    {
        public SeededHost As(string email) => Host.As(email);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
