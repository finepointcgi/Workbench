using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.ContractDetail;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;
using Workbench.Domain.Billing;
using Workbench.Domain.Crm;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Automations;

/// <summary>A field computed from related records (a total, a remaining percentage) rather than stored on the entity.</summary>
public sealed record ComputedField(string Key, string Label, string FieldType, Func<AutomationRecordContext, Task<object?>> Read);

/// <summary>What the record reader is given to compute a field: the database, the loaded entity, and services.</summary>
public sealed record AutomationRecordContext(IWorkbenchDbContext Db, object Entity, IServiceProvider Services, CancellationToken Ct);

/// <summary>
/// A kind of record automations can watch: its key (the <see cref="IEntityFieldCatalog"/> entity type), the domain type and
/// the audit entity type its module writes, how to name and link to a record, how to find its company, org unit, project
/// and people, which built-in fields an automation may set, and who may change or read it.
/// </summary>
public sealed class AutomationEntity
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Type ClrType { get; init; }

    /// <summary>The <c>AuditEvent.EntityType</c> the module records (the class name).</summary>
    public string AuditEntityType => ClrType.Name;

    public required string LinkFormat { get; init; }
    public string? ApprovalDocumentType { get; init; }

    /// <summary>Properties that name a record, joined with a space (the first ones found).</summary>
    public IReadOnlyList<string> NameProperties { get; init; } = ["Number", "Code", "Name", "Title"];

    public string? CompanyProperty { get; init; }
    public string? OrgUnitProperty { get; init; }
    public string? ProjectProperty { get; init; }

    /// <summary>An employee id property: gives the record's company, org unit and supervisor when it has none of its own.</summary>
    public string? EmployeeProperty { get; init; }

    /// <summary>An email property naming who submitted it.</summary>
    public string? SubmitterProperty { get; init; }

    /// <summary>Built-in fields automations may set (descriptive fields no module rule depends on). Everything else is read-only.</summary>
    public IReadOnlySet<string> Writable { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fields that only Finance (and administrators) may read or set: money a module treats as business-sensitive.</summary>
    public IReadOnlySet<string> FinanceFields { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ComputedField> Computed { get; init; } = [];

    /// <summary>Org roles that may change any record of this type (and so set its fields).</summary>
    public required Func<OrgRoles, bool> CanWrite { get; init; }

    /// <summary>Org roles that see every record (others see records they own or whose project they manage).</summary>
    public required Func<OrgRoles, bool> CanReadAll { get; init; }

    public string Link(Guid id, object? entity = null)
    {
        var link = LinkFormat.Replace("{id}", id.ToString(), StringComparison.Ordinal);
        if (entity is not null && link.Contains('{'))
        {
            foreach (var property in ClrType.GetProperties())
            {
                link = link.Replace("{" + property.Name + "}", Convert.ToString(property.GetValue(entity), CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }
        }

        return link;
    }

    public string Describe(object entity)
    {
        var parts = NameProperties.Select(p => ClrType.GetProperty(p)?.GetValue(entity)?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(2).ToList();
        return parts.Count == 0 ? Label : string.Join(" ", parts);
    }

    public PropertyInfo? Property(string key) =>
        ClrType.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is { } p && AutomationEntities.IsScalar(p.PropertyType) ? p : null;
}

/// <summary>The record types automations know, and reading their built-in fields by reflection.</summary>
public static class AutomationEntities
{
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "Id", "Version", "RowVersion", "CertificationText", "PayloadJson" };

    private static Func<OrgRoles, bool> Finance => r => r.IsFinance();

    public static IReadOnlyList<AutomationEntity> All { get; } =
    [
        new()
        {
            Key = "contract", Label = "Contract", ClrType = typeof(Contract), LinkFormat = "setup/contracts/{id}", CompanyProperty = nameof(Contract.CompanyId),
            NameProperties = [nameof(Contract.ContractNumber), nameof(Contract.Name)],
            FinanceFields = Set(nameof(Contract.CeilingValue), nameof(Contract.FundedValue), "fundingRemainingPercent", "fundingRemaining", "incurredCost"),
            Computed =
            [
                new("fundingRemainingPercent", "Funding remaining (%)", "number", async c => (await ContractFundingAsync(c)).RemainingPercent),
                new("fundingRemaining", "Funding remaining", "money", async c => (await ContractFundingAsync(c)).Remaining),
                new("incurredCost", "Incurred cost", "money", async c => (await ContractFundingAsync(c)).Incurred),
            ],
            CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "project", Label = "Project", ClrType = typeof(Project), LinkFormat = "projects/{id}", CompanyProperty = nameof(Project.CompanyId), ProjectProperty = nameof(Project.Id),
            NameProperties = [nameof(Project.Code), nameof(Project.Name)],
            FinanceFields = Set("budgetUsedPercent", "budget", "actualCost"),
            Computed =
            [
                new("budgetUsedPercent", "Budget used (%)", "number", async c => (await ProjectBudgetAsync(c)).UsedPercent),
                new("budget", "Current budget", "money", async c => (await ProjectBudgetAsync(c)).Budget),
                new("actualCost", "Actual cost", "money", async c => (await ProjectBudgetAsync(c)).Actual),
            ],
            CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "employee", Label = "Employee", ClrType = typeof(Employee), LinkFormat = "hr/employees/{id}", CompanyProperty = nameof(Employee.CompanyId),
            OrgUnitProperty = nameof(Employee.OrgUnitId), EmployeeProperty = nameof(Employee.Id),
            NameProperties = [nameof(Employee.EmployeeNumber), nameof(Employee.DisplayName)],
            CanWrite = r => r.IsHr() || r.IsFinance(), CanReadAll = r => r.IsHr() || r.IsFinance() || r.CanViewAllTimesheets(),
        },
        new()
        {
            Key = "timesheet", Label = "Timesheet", ClrType = typeof(Timesheet), LinkFormat = "time/history/{id}", ApprovalDocumentType = ApprovalDocumentTypes.Timesheet,
            EmployeeProperty = nameof(Timesheet.EmployeeId), NameProperties = [nameof(Timesheet.PeriodStart), nameof(Timesheet.PeriodEnd)],
            Computed =
            [
                new("totalHours", "Total hours", "number", async c => await c.Db.Set<TimeEntry>().AsNoTracking().Where(e => e.TimesheetId == ((Timesheet)c.Entity).Id).SumAsync(e => e.Hours, c.Ct)),
                new("daysLate", "Days late (first submitted after the period ended)", "number", c => Task.FromResult<object?>(((Timesheet)c.Entity) is { } t
                    && (t.FirstSubmittedAtUtc ?? t.SubmittedAtUtc) is { } at ? Math.Max(0, DateOnly.FromDateTime(at).DayNumber - t.PeriodEnd.DayNumber) : null)),
            ],
            CanWrite = r => r.CanViewAllTimesheets(), CanReadAll = r => r.CanViewAllTimesheets(),
        },
        new()
        {
            Key = "expense-report", Label = "Expense report", ClrType = typeof(ExpenseReport), LinkFormat = "expenses/{id}", ApprovalDocumentType = ApprovalDocumentTypes.ExpenseReport,
            EmployeeProperty = nameof(ExpenseReport.EmployeeId), SubmitterProperty = nameof(ExpenseReport.SubmittedBy), NameProperties = [nameof(ExpenseReport.Title)],
            Writable = Set(nameof(ExpenseReport.Purpose)),
            Computed =
            [
                new("total", "Total", "money", async c => await Lines(c).SumAsync(l => l.Amount, c.Ct)),
                new("lineCount", "Number of lines", "number", async c => await Lines(c).CountAsync(c.Ct)),
                new("maxLineWithoutReceipt", "Largest line without a receipt", "money",
                    async c => (await Lines(c).Where(l => l.ReceiptAttachmentId == null).Select(l => l.Amount).ToListAsync(c.Ct)).DefaultIfEmpty(0m).Max()),
            ],
            CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "expense-line", Label = "Expense line or ODC", ClrType = typeof(OdcEntry), LinkFormat = "projects/{ProjectId}/odcs", ApprovalDocumentType = ApprovalDocumentTypes.VendorOdc,
            EmployeeProperty = nameof(OdcEntry.EmployeeId), ProjectProperty = nameof(OdcEntry.ProjectId), SubmitterProperty = nameof(OdcEntry.SubmittedBy),
            NameProperties = [nameof(OdcEntry.Description)],
            Computed = [new("hasReceipt", "Has a receipt", "boolean", c => Task.FromResult<object?>(((OdcEntry)c.Entity).ReceiptAttachmentId is not null))],
            CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "invoice", Label = "Invoice", ClrType = typeof(Invoice), LinkFormat = "billing/invoices/{id}", ApprovalDocumentType = ApprovalDocumentTypes.Invoice,
            CompanyProperty = nameof(Invoice.CompanyId), SubmitterProperty = nameof(Invoice.CreatedBy), Writable = Set(nameof(Invoice.Notes)),
            CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "ap-bill", Label = "AP bill", ClrType = typeof(ApBill), LinkFormat = "purchasing/bills/{id}", ApprovalDocumentType = ApprovalDocumentTypes.ApBill,
            CompanyProperty = nameof(ApBill.CompanyId), SubmitterProperty = nameof(ApBill.EnteredBy), NameProperties = [nameof(ApBill.Number), nameof(ApBill.InvoiceNumber)],
            Writable = Set(nameof(ApBill.Description)), CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "purchase-order", Label = "Purchase order", ClrType = typeof(PurchaseOrder), LinkFormat = "purchasing/orders/{id}",
            Writable = Set(nameof(PurchaseOrder.Notes)), CanWrite = Finance, CanReadAll = r => r.IsFinance() || r.CanViewMaterials(),
        },
        new()
        {
            Key = "requisition", Label = "Purchase requisition", ClrType = typeof(PurchaseRequisition), LinkFormat = "purchasing/requisitions/{id}",
            ApprovalDocumentType = ApprovalDocumentTypes.Requisition, EmployeeProperty = nameof(PurchaseRequisition.RequesterEmployeeId),
            OrgUnitProperty = nameof(PurchaseRequisition.OrgUnitId), SubmitterProperty = nameof(PurchaseRequisition.RequesterEmail),
            CanWrite = Finance, CanReadAll = Finance,
        },
        new()
        {
            Key = "vendor", Label = "Vendor", ClrType = typeof(Vendor), LinkFormat = "purchasing/vendors/{id}",
            Writable = Set(nameof(Vendor.ContactName), nameof(Vendor.ContactEmail), nameof(Vendor.ContactPhone)),
            CanWrite = Finance, CanReadAll = r => r.IsFinance() || r.CanViewMaterials(),
        },
        new()
        {
            Key = "opportunity", Label = "Opportunity", ClrType = typeof(Opportunity), LinkFormat = "bd/opportunities/{id}", ProjectProperty = nameof(Opportunity.ProjectId),
            Writable = Set(nameof(Opportunity.Pwin), nameof(Opportunity.Pgo), nameof(Opportunity.CaptureManagerEmail), nameof(Opportunity.ProposalManagerEmail)),
            FinanceFields = Set(nameof(Opportunity.EstimatedValue), nameof(Opportunity.AwardedValue)),
            CanWrite = r => r.IsBusinessDevelopment(), CanReadAll = r => r.IsBusinessDevelopment() || r.IsFinance(),
        },
    ];

    public static AutomationEntity? Find(string? key) => key is null ? null : All.FirstOrDefault(e => e.Key == key);

    public static AutomationEntity Require(string? key) =>
        Find(key) ?? throw new ArgumentException($"\"{key}\" isn't a record type automations know.");

    public static AutomationEntity? ForAudit(string auditEntityType) => All.FirstOrDefault(e => e.AuditEntityType == auditEntityType);

    public static AutomationEntity? ForApprovalDocument(string documentType) => All.FirstOrDefault(e => e.ApprovalDocumentType == documentType);

    /// <summary>The entity type an integration event's subject is (Timesheet → timesheet).</summary>
    public static AutomationEntity? ForSubject(string subject) => All.FirstOrDefault(e => e.ClrType.Name == subject);

    public static string FieldKey(PropertyInfo property) => char.ToLowerInvariant(property.Name[0]) + property.Name[1..];

    public static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsEnum || t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long) || t == typeof(decimal)
            || t == typeof(double) || t == typeof(DateOnly) || t == typeof(DateTime) || t == typeof(Guid);
    }

    /// <summary>The built-in fields: scalar public properties (by reflection) and the computed ones.</summary>
    public static IReadOnlyList<EntityFieldDto> Fields(AutomationEntity entity)
    {
        var fields = new List<EntityFieldDto>();
        var nullability = new NullabilityInfoContext();
        foreach (var property in entity.ClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Hidden.Contains(property.Name) || !IsScalar(property.PropertyType) || property.GetMethod is null)
            {
                continue;
            }

            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var required = property.PropertyType.IsValueType
                ? Nullable.GetUnderlyingType(property.PropertyType) is null && type != typeof(bool)
                : nullability.Create(property).ReadState == NullabilityState.NotNull;
            var key = FieldKey(property);
            fields.Add(new EntityFieldDto(entity.Key, key, Humanize(property.Name), FieldType(type, property.Name), false, required,
                type.IsEnum ? Enum.GetNames(type) : null, entity.FinanceFields.Contains(key) ? AutomationPermissionKeys.FinanceFields : null));
        }

        fields.AddRange(entity.Computed.Select(c => new EntityFieldDto(entity.Key, c.Key, c.Label, c.FieldType, false, false, null,
            entity.FinanceFields.Contains(c.Key) ? AutomationPermissionKeys.FinanceFields : null)));
        return fields;
    }

    public static string FieldType(Type type, string name) =>
        type.IsEnum ? "choice"
        : type == typeof(bool) ? "boolean"
        : type == typeof(DateOnly) ? "date"
        : type == typeof(DateTime) ? "datetime"
        : type == typeof(Guid) ? "reference"
        : type == typeof(decimal) && (name.Contains("Hours", StringComparison.Ordinal) || name.Contains("Rate", StringComparison.Ordinal) || name.Contains("Pwin", StringComparison.Ordinal)) ? "number"
        : type == typeof(decimal) ? "money"
        : type == typeof(int) || type == typeof(long) || type == typeof(double) ? "number"
        : "text";

    public static string Humanize(string name)
    {
        var text = name.EndsWith("Utc", StringComparison.Ordinal) ? name[..^3] : name;
        var chars = new List<char>();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]))
            {
                chars.Add(' ');
            }

            chars.Add(i == 0 ? text[i] : char.ToLowerInvariant(text[i]));
        }

        var result = new string([.. chars]);
        return result.EndsWith(" id", StringComparison.Ordinal) ? result[..^3] : result;
    }

    /// <summary>Loads a record by id, preferring the instance the context already tracks (so its unsaved changes count).</summary>
    public static async Task<object?> LoadAsync(IWorkbenchDbContext db, AutomationEntity entity, Guid id, CancellationToken ct)
    {
        if (db is DbContext context)
        {
            return await context.FindAsync(entity.ClrType, [id], ct);
        }

        return null;
    }

    private static IQueryable<OdcEntry> Lines(AutomationRecordContext c) =>
        c.Db.Set<OdcEntry>().AsNoTracking().Where(l => l.ExpenseReportId == ((ExpenseReport)c.Entity).Id);

    private sealed record FundingFigures(decimal Funded, decimal Incurred, decimal Remaining, decimal? RemainingPercent);

    /// <summary>Funded less incurred cost: from the CLINs when the contract has them, else the contract's funded value and its projects' actuals.</summary>
    private static async Task<FundingFigures> ContractFundingAsync(AutomationRecordContext c)
    {
        var contract = (Contract)c.Entity;
        if (c.Services.GetService<ClinFundingCalculator>() is { } calculator
            && await c.Db.Set<Clin>().AsNoTracking().AnyAsync(x => x.ContractId == contract.Id, c.Ct))
        {
            var status = await calculator.ComputeAsync(contract.Id, c.Ct);
            var funded = status.Clins.Sum(x => x.FundedValue);
            var incurred = status.Clins.Sum(x => x.Incurred);
            return new FundingFigures(funded, incurred, funded - incurred, funded > 0 ? Math.Round((funded - incurred) / funded * 100m, 2) : null);
        }

        var projectIds = await c.Db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contract.Id).Select(p => p.Id).ToListAsync(c.Ct);
        var (labor, odcs) = await Financials.FinancialsLoader.LoadActualsAsync(c.Db, projectIds, contract, [], c.Ct);
        var spent = labor.Sum(l => l.Cost) + odcs.Sum(o => o.Amount);
        var fundedValue = contract.FundedValue;
        return new FundingFigures(fundedValue, spent, fundedValue - spent, fundedValue > 0 ? Math.Round((fundedValue - spent) / fundedValue * 100m, 2) : null);
    }

    private sealed record BudgetFigures(decimal Budget, decimal Actual, decimal? UsedPercent);

    private static async Task<BudgetFigures> ProjectBudgetAsync(AutomationRecordContext c)
    {
        var project = (Project)c.Entity;
        var versions = await c.Db.Set<BudgetVersion>().AsNoTracking().Where(v => v.ProjectId == project.Id).ToListAsync(c.Ct);
        var budget = versions.FirstOrDefault(v => v.Kind == BudgetKind.Current) ?? versions.FirstOrDefault(v => v.Kind == BudgetKind.Baseline);
        var total = budget is null ? 0m : await c.Db.Set<BudgetLine>().AsNoTracking().Where(l => l.BudgetVersionId == budget.Id).SumAsync(l => l.Amount, c.Ct);
        var contract = project.ContractId is { } cid ? await c.Db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == cid, c.Ct) : null;
        var (labor, odcs) = await Financials.FinancialsLoader.LoadActualsAsync(c.Db, [project.Id], contract, [], c.Ct);
        var actual = labor.Sum(l => l.Cost) + odcs.Sum(o => o.Amount);
        return new BudgetFigures(total, actual, total > 0 ? Math.Round(actual / total * 100m, 2) : null);
    }

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);
}
