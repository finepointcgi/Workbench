using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;
using D = Workbench.Domain;
using E = Workbench.Contracts.Customization.CustomizableEntityTypes;

namespace Workbench.Application.Customization;

public static class CustomizationRegistration
{
    /// <summary>Opts a record type in to custom fields (one line per type below).</summary>
    public static IServiceCollection AddCustomizableEntity(this IServiceCollection services, CustomizableEntity entity)
    {
        services.AddSingleton(entity);
        return services;
    }

    /// <summary>Custom fields, value lists, status labels and numbering; above AddWorkbenchIdentity (its decorators wrap these).</summary>
    public static IServiceCollection AddWorkbenchCustomization(this IServiceCollection services)
    {
        services.AddSingleton<CustomizableEntityRegistry>();
        services.TryAddScoped<ICustomFieldAccessPolicy, FinanceAdminCustomFieldAccessPolicy>(); // The custom roles permission catalog replaces this seam.
        services.AddScoped<CustomListService>();
        services.AddScoped<ICustomListService>(sp => sp.GetRequiredService<CustomListService>());
        services.AddScoped<CustomFieldService>();
        services.AddScoped<ICustomFieldService>(sp => sp.GetRequiredService<CustomFieldService>());
        services.AddScoped<INumberingService, NumberingService>();
        services.AddScoped<DocumentNumbers>();
        services.Replace(ServiceDescriptor.Scoped<IEntityFieldCatalog, EntityFieldCatalog>()); // Wins over built-in-only stubs registered with TryAdd.

        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.Project>
        {
            Key = E.Project, Label = "Project", LabelPlural = "Projects", Title = p => new RecordTitle(p.Id, p.Code + " · " + p.Name), Link = id => $"projects/{id}",
            StatusSet = StatusSetCatalog.ProjectStatus, Status = p => p.Status.ToString(), IsLocked = p => p.Status == D.ProjectAccounting.ProjectStatus.Closed,
            OwnerEmployee = p => p.ProjectManagerId, ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IProjectService>(async (s, id, ct) => await s.GetProjectAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.Contract>
        {
            Key = E.Contract, Label = "Contract", LabelPlural = "Contracts", Title = c => new RecordTitle(c.Id, c.ContractNumber + " · " + c.Name), Link = id => $"setup/contracts/{id}",
            StatusSet = StatusSetCatalog.ContractStatus, Status = c => c.Status.ToString(), IsLocked = c => c.Status == D.ProjectAccounting.ContractStatus.Closed, ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IContractService>(async (s, id, ct) => await s.GetContractAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.WbsElement>
        {
            Key = E.WbsElement, Label = "WBS element", LabelPlural = "WBS elements", Title = w => new RecordTitle(w.Id, w.Code + " · " + w.Name), ManageRoles = r => r.IsFinance(),
            CanReadAsync = async (sp, id, ct) =>
            {
                await using var db = await sp.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(ct);
                var projectId = await db.Set<D.ProjectAccounting.WbsElement>().Where(w => w.Id == id).Select(w => (Guid?)w.ProjectId).FirstOrDefaultAsync(ct);
                return projectId is { } p && await sp.GetRequiredService<IProjectService>().GetProjectAsync(p, ct) is not null;
            },
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.Employee>
        {
            Key = E.Employee, Label = "Employee", LabelPlural = "Employees", Title = e => new RecordTitle(e.Id, e.EmployeeNumber + " · " + e.DisplayName), Link = id => $"hr/employees/{id}",
            ManageRoles = r => r.IsHr() || r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IPeopleService>(async (s, id, ct) => await s.GetEmployeeAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.Purchasing.Vendor>
        {
            Key = E.Vendor, Label = "Vendor", LabelPlural = "Vendors", Title = v => new RecordTitle(v.Id, v.Code + " · " + v.Name), Link = id => $"purchasing/vendors/{id}", ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IVendorService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.Customer>
        {
            Key = E.Customer, Label = "Customer", LabelPlural = "Customers", Title = c => new RecordTitle(c.Id, c.Code + " · " + c.Name), Link = _ => "setup/customers", ManageRoles = r => r.IsFinance(),
            CanReadAsync = async (sp, id, ct) => (await sp.GetRequiredService<IContractService>().ListCustomersAsync(ct)).Any(c => c.Id == id),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.Purchasing.PurchaseOrder>
        {
            Key = E.PurchaseOrder, Label = "Purchase order", LabelPlural = "Purchase orders", Title = p => new RecordTitle(p.Id, p.Number), Link = id => $"purchasing/orders/{id}",
            StatusSet = StatusSetCatalog.PurchaseOrderStatus, Status = p => p.Status.ToString(), IsLocked = p => p.Status != D.Purchasing.PurchaseOrderStatus.Draft, ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IPurchaseOrderService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.Purchasing.ApBill>
        {
            Key = E.ApBill, Label = "AP bill", LabelPlural = "AP bills", Title = b => new RecordTitle(b.Id, b.Number + " · " + b.InvoiceNumber), Link = id => $"purchasing/bills/{id}",
            StatusSet = StatusSetCatalog.ApBillStatus, Status = b => b.Status.ToString(), IsLocked = b => b.Status is D.Purchasing.ApBillStatus.Approved or D.Purchasing.ApBillStatus.Posted or D.Purchasing.ApBillStatus.Void,
            ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IApBillService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.Billing.Invoice>
        {
            Key = E.Invoice, Label = "Invoice", LabelPlural = "Invoices", Title = i => new RecordTitle(i.Id, i.Number), Link = id => $"billing/invoices/{id}",
            StatusSet = StatusSetCatalog.InvoiceStatus, Status = i => i.Status.ToString(), IsLocked = i => i.Status != D.Billing.InvoiceStatus.Draft, ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IInvoiceService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.ExpenseReport>
        {
            Key = E.ExpenseReport, Label = "Expense report", LabelPlural = "Expense reports", Title = r => new RecordTitle(r.Id, r.Title), Link = id => $"expenses/{id}",
            StatusSet = StatusSetCatalog.ExpenseReportStatus, Status = r => r.Status.ToString(), IsLocked = r => r.Status == D.ProjectAccounting.OdcStatus.Approved,
            OwnerEmployee = r => r.EmployeeId, ManageRoles = r => r.IsFinance(),
            CanReadAsync = CustomizableEntity.ReadVia<IExpenseReportService>(async (s, id, ct) => await s.GetReportAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.ProjectAccounting.Timesheet>
        {
            Key = E.Timesheet, Label = "Timesheet", LabelPlural = "Timesheets", Title = t => new RecordTitle(t.Id, "Timesheet " + t.PeriodStart.ToString()), Link = id => $"time/history/{id}",
            StatusSet = StatusSetCatalog.TimesheetStatus, Status = t => t.Status.ToString(), IsLocked = t => t.Status == D.ProjectAccounting.TimesheetStatus.Approved,
            OwnerEmployee = t => t.EmployeeId, ManageRoles = r => r.CanViewAllTimesheets(),
            CanReadAsync = CustomizableEntity.ReadVia<ITimesheetService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.Materials.Part>
        {
            Key = E.Part, Label = "Part", LabelPlural = "Parts", Title = p => new RecordTitle(p.Id, p.Number + " · " + p.Description), Link = id => $"materials/parts/{id}", ManageRoles = r => r.IsMaterials(),
            CanReadAsync = CustomizableEntity.ReadVia<IPartService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        services.AddCustomizableEntity(new CustomizableEntity<D.Crm.Opportunity>
        {
            Key = E.Opportunity, Label = "Opportunity", LabelPlural = "Opportunities", Title = o => new RecordTitle(o.Id, o.Number + " · " + o.Name), Link = id => $"bd/opportunities/{id}",
            StatusSet = StatusSetCatalog.OpportunityStage, Status = o => o.Stage.ToString(), ManageRoles = r => r.IsBusinessDevelopment(),
            CanReadAsync = CustomizableEntity.ReadVia<IOpportunityService>(async (s, id, ct) => await s.GetAsync(id, ct)),
        });
        return services;
    }
}
