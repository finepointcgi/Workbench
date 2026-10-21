using Microsoft.EntityFrameworkCore;
using Workbench.Application.Customization;
using Workbench.Domain.Customization;
using Workbench.Domain.ProjectAccounting;
using E = Workbench.Contracts.Customization.CustomizableEntityTypes;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for customization: a "Program offices" value list; custom fields "Program office" (picklist) and
/// "Customer POC" on projects, "Clearance sponsor" and a sensitive "Badge number" on employees, and "Contracting officer
/// approval" (locked once issued) on purchase orders, with values on the seeded projects and people; and the sub-statuses
/// "Awaiting funding" and "At risk" under active projects. Runs once, after the other seeds.
/// </summary>
internal static class CustomizationSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<CustomFieldDefinition>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var offices = new ValueList { Key = "program-offices", Name = "Program offices", Description = "Government program offices we support.", UpdatedAtUtc = now, UpdatedBy = Actor };
        (string Code, string Label)[] items = [("peo-eis", "PEO Enterprise Information Systems"), ("peo-c3t", "PEO Command, Control, Communications-Tactical"),
            ("pmo-space", "Space Systems Command PMO"), ("pma-209", "NAVAIR PMA-209"), ("dhs-cio", "DHS Office of the CIO")];
        offices.Items.AddRange(items.Select((x, i) => new ValueListItem { ValueListId = offices.Id, Code = x.Code, Label = x.Label, Order = i }));
        db.Add(offices);

        CustomFieldDefinition Field(string entity, string key, string label, CustomFieldType type, int order, string? help = null) =>
            new() { EntityType = entity, Key = key, Label = label, FieldType = type, Order = order, HelpText = help, CreatedAtUtc = now, UpdatedAtUtc = now, UpdatedBy = Actor };
        var office = Field(E.Project, "program_office", "Program office", CustomFieldType.Picklist, 0, "The government program office the project supports.");
        office.ValueListKey = offices.Key;
        var poc = Field(E.Project, "customer_poc", "Customer POC", CustomFieldType.Text, 1, "The customer's point of contact (name and email).");
        var sponsor = Field(E.Employee, "clearance_sponsor", "Clearance sponsor", CustomFieldType.Text, 0, "The agency or contract sponsoring the person's clearance.");
        var badge = Field(E.Employee, "badge_number", "Badge number", CustomFieldType.Text, 1, "Facility badge number.");
        badge.IsSensitive = true;
        var coApproval = Field(E.PurchaseOrder, "co_approval", "Contracting officer approval", CustomFieldType.YesNo, 0, "Consent to subcontract received (FAR 44.2).");
        coApproval.ReadOnlyAfterApproval = true;
        coApproval.DefaultValue = "false";
        db.AddRange(office, poc, sponsor, badge, coApproval);

        void Value(CustomFieldDefinition field, Guid recordId, string text, string display) =>
            db.Add(new CustomFieldValue { FieldId = field.Id, EntityType = field.EntityType, RecordId = recordId, TextValue = text, SearchText = display.ToLowerInvariant(), UpdatedAtUtc = now, UpdatedBy = Actor });
        var projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Code, p => p.Id, cancellationToken);
        if (projects.TryGetValue("1001", out var p1001))
        {
            Value(office, p1001, "peo-eis", "PEO Enterprise Information Systems");
            Value(poc, p1001, "LTC Jordan Ellis (jordan.ellis@army.example.mil)", "LTC Jordan Ellis (jordan.ellis@army.example.mil)");
        }

        if (projects.TryGetValue("1002", out var p1002))
        {
            Value(office, p1002, "dhs-cio", "DHS Office of the CIO");
        }

        if (projects.TryGetValue("3001", out var p3001))
        {
            Value(office, p3001, "peo-c3t", "PEO Command, Control, Communications-Tactical");
        }

        var people = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.EmployeeNumber, e => e.Id, cancellationToken);
        if (people.TryGetValue("E1001", out var pat))
        {
            Value(sponsor, pat, "U.S. Army PEO EIS", "U.S. Army PEO EIS");
            Value(badge, pat, "B-20417", "b-20417");
        }

        if (people.TryGetValue("E1002", out var alex))
        {
            Value(sponsor, alex, "DHS", "DHS");
        }

        db.AddRange(
            new SubStatus { SetKey = StatusSetCatalog.ProjectStatus, ParentValue = nameof(ProjectStatus.Active), Code = "awaiting_funding", Label = "Awaiting funding", Order = 0 },
            new SubStatus { SetKey = StatusSetCatalog.ProjectStatus, ParentValue = nameof(ProjectStatus.Active), Code = "at_risk", Label = "At risk", Order = 1 },
            new SubStatus { SetKey = StatusSetCatalog.ProjectStatus, ParentValue = nameof(ProjectStatus.OnHold), Code = "stop_work", Label = "Stop-work order", Order = 0 });
        await db.SaveChangesAsync(cancellationToken);
    }
}
