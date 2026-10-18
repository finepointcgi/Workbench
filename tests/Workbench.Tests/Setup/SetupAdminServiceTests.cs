using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Setup;

public sealed class SetupAdminServiceTests
{
    private readonly PeopleService _people;
    private readonly ContractService _contracts;
    private readonly SetupAdminService _setup;

    public SetupAdminServiceTests()
    {
        var factory = TestDb.CreateFactory();
        var user = new TestUser { Email = "finance@example.com" };
        var recorder = new AuditRecorder(user, TimeProvider.System);
        _people = new PeopleService(factory, recorder, user, TimeProvider.System);
        _contracts = new ContractService(factory, recorder);
        _setup = new SetupAdminService(factory, recorder);
    }

    [Fact]
    public async Task Org_units_can_be_renamed_and_moved_but_not_under_themselves()
    {
        var ops = await _people.CreateOrgUnitAsync(new CreateOrgUnitRequest("OPS", "Operations"));
        var eng = await _people.CreateOrgUnitAsync(new CreateOrgUnitRequest("ENG", "Engineering", ops.Id));

        var renamed = await _setup.UpdateOrgUnitAsync(eng.Id, new CreateOrgUnitRequest("ENG", "Engineering and IT", ops.Id));

        Assert.Equal("Engineering and IT", renamed.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => _setup.UpdateOrgUnitAsync(ops.Id, new CreateOrgUnitRequest("OPS", "Operations", eng.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => _setup.UpdateOrgUnitAsync(eng.Id, new CreateOrgUnitRequest("OPS", "Dup", null)));
    }

    [Fact]
    public async Task Audit_search_filters_by_type_entity_and_date()
    {
        var customer = await _contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "Army"));
        await _setup.UpdateCustomerAsync(customer.Id, new SaveCustomerRequest("ARMY", "U.S. Army", "DoD"));
        await _people.CreateLaborCategoryAsync(new SaveLaborCategoryRequest("PM3", "Program Manager"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var forCustomer = await _setup.SearchAuditAsync(new AuditQuery("Customer", customer.Id));
        Assert.Equal(["Updated", "Created"], forCustomer.Select(a => a.Action));
        Assert.Equal(2, (await _setup.SearchAuditAsync(new AuditQuery(From: today, To: today))).Count(a => a.EntityType == "Customer"));
        Assert.Empty(await _setup.SearchAuditAsync(new AuditQuery(To: today.AddDays(-1))));
        Assert.Equal(["Customer", "LaborCategory"], await _setup.ListAuditEntityTypesAsync());
    }

    [Fact]
    public async Task Checklist_ticks_off_as_setup_data_appears()
    {
        Assert.Equal(new SetupChecklistDto(false, false, false, false, false, false, false), await _setup.GetChecklistAsync());

        var category = await _people.CreateLaborCategoryAsync(new SaveLaborCategoryRequest("PM3", "Program Manager"));
        var employee = await _people.CreateEmployeeAsync(new SaveEmployeeRequest("E1", "a@example.com", "Alex", null, null, category.Id, new DateOnly(2025, 1, 1)));
        await _people.AddCostRateAsync(employee.Id, new AddCostRateRequest(new DateOnly(2025, 1, 1), 50m, "Hire"));

        var checklist = await _setup.GetChecklistAsync();
        Assert.True(checklist.HasLaborCategories && checklist.HasEmployees && checklist.HasCostRates);
        Assert.False(checklist.HasCustomerAndContract);
    }
}
