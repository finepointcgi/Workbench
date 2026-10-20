using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Integrations;

namespace Workbench.Tests.Integrations;

public sealed class PayloadRedactionTests
{
    // Words that would mean pay, cost or bill rates, salaries, bank, tax or EEO data, or sheet cell values.
    private static readonly string[] Forbidden = ["rate", "salary", "wage", "compensation", "hourly", "cost", "bank", "routing", "iban", "ssn", "taxid", "tin",
        "eeo", "gender", "race", "ethnic", "veteran", "disability", "birth", "values", "cell", "column", "password", "secret"];

    [Fact]
    public void Every_event_payload_is_an_allow_list_without_sensitive_fields()
    {
        foreach (var type in IntegrationEventType.All)
        {
            foreach (var property in type.DataType.GetProperties())
            {
                var name = property.Name.ToLowerInvariant();
                Assert.False(Forbidden.Any(f => name == f || name.StartsWith(f) || name.EndsWith(f)), $"{type.Key}.{property.Name} looks sensitive.");
                var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                Assert.True(t.IsPrimitive || t == typeof(string) || t == typeof(Guid) || t == typeof(decimal) || t == typeof(DateOnly) || t == typeof(DateTime),
                    $"{type.Key}.{property.Name} is a {t.Name}; payloads carry only ids and summary values, never nested records.");
            }
        }
    }

    [Fact]
    public void The_domain_catalog_and_the_contracts_catalog_list_the_same_events()
    {
        Assert.Equal(IntegrationEventType.All.Select(t => t.Key), WebhookEventCatalog.All.Select(e => e.Key));
        Assert.Equal(IntegrationEventType.All.Count, IntegrationEventType.All.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public async Task An_employee_hired_payload_has_ids_and_summary_fields_only()
    {
        await using var host = await IntegrationHost.CreateAsync();
        var created = await host.Get<IPeopleService>().CreateEmployeeAsync(new SaveEmployeeRequest("E77", "new.hire@example.com", "New Hire", null, null, null, new DateOnly(2026, 10, 1)));
        await host.Get<IPeopleService>().AddCostRateAsync(created.Id, new AddCostRateRequest(new DateOnly(2026, 10, 1), 123.45m, "Offer"));

        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var row = await db.Set<IntegrationOutboxEvent>().AsNoTracking().SingleAsync(e => e.EventType == "employee.hired");
        using var doc = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(["id", "type", "version", "occurredAt", "subject", "subjectId", "actor", "data"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["employeeId", "employeeNumber", "displayName", "email", "companyId", "orgUnitId", "supervisorId", "hireDate", "terminationDate"],
            doc.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("123.45", row.PayloadJson);
        Assert.Equal("2026-10-01", doc.RootElement.GetProperty("data").GetProperty("hireDate").GetString());
    }
}
