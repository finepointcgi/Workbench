using Workbench.Contracts.Billing;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using E = Workbench.Contracts.Customization.CustomizableEntities;

namespace Workbench.Application.Customization;

/// <summary>
/// The built-in fields of each customizable form and the layout the page was built with. Field keys are the camel-case
/// names of the save request's properties (<see cref="FormDefinition.RequestTypes"/>), which is how the server checks
/// fields a layout makes required. Each page renders its fields through <c>ConfigurableForm</c> with the same keys, in
/// the same order, so with no saved layout nothing changes. Keep a page and its definition in step.
/// </summary>
public static class BuiltInForms
{
    public const string Text = "Text";
    public const string LongText = "LongText";
    public const string Number = "Number";
    public const string Money = "Money";
    public const string Date = "Date";
    public const string Bool = "Bool";
    public const string Select = "Select";
    public const string MultiSelect = "MultiSelect";
    public const string Person = "Person";

    /// <summary>Shown, not entered (a computed figure). Can be hidden, never required.</summary>
    public const string Display = "Display";

    public sealed record Field(string Key, string Label, string Type, bool Required = false, string? PermissionKey = null);

    public sealed record Section(string Key, string Title, IReadOnlyList<Field> Fields);

    /// <param name="RequestTypes">Save requests whose properties carry the fields' values (checked on create and update).</param>
    public sealed record FormDefinition(string EntityType, string Name, string ShownOn, IReadOnlyList<Section> Sections, IReadOnlyList<Type> RequestTypes)
    {
        public IEnumerable<Field> Fields => Sections.SelectMany(s => s.Fields);
    }

    private static Section One(params Field[] fields) => new("details", "", fields);

    public static IReadOnlyList<FormDefinition> All { get; } =
    [
        new(E.Project, "Project", "Setup › Projects › Details",
        [
            One(
                new("code", "Code", Text, Required: true),
                new("name", "Name", Text, Required: true),
                new("contractId", "Contract", Select),
                new("projectManagerId", "Project manager", Person),
                new("status", "Status", Select),
                new("start", "Start", Date),
                new("finish", "Finish", Date)),
        ], [typeof(SaveProjectRequest)]),

        new(E.Contract, "Contract", "Setup › Contracts › Details",
        [
            One(
                new("contractNumber", "Contract number", Text, Required: true),
                new("customerId", "Customer", Select, Required: true),
                new("name", "Name", Text, Required: true),
                new("type", "Type", Select),
                new("status", "Status", Select),
                new("periodStart", "Period of performance start", Date, Required: true),
                new("periodEnd", "Period of performance end", Date, Required: true),
                new("ceilingValue", "Ceiling value", Money),
                new("fundedValue", "Funded value", Money),
                new("isPrime", "We are the prime contractor", Bool)),
        ], [typeof(SaveContractRequest)]),

        new(E.Employee, "Employee", "Setup › People (edit employee)",
        [
            One(
                new("employeeNumber", "Employee number", Text, Required: true),
                new("displayName", "Name", Text, Required: true),
                new("email", "Email", Text, Required: true),
                new("orgUnitId", "Org unit", Select),
                new("supervisorId", "Supervisor", Person),
                new("defaultLaborCategoryId", "Default labor category", Select),
                new("standardHoursPerWeek", "Standard hours per week", Number),
                new("hireDate", "Hire date", Date, Required: true),
                new("terminationDate", "Termination date", Date),
                new("isExempt", "Exempt (salaried, FLSA)", Bool)),
        ], [typeof(SaveEmployeeRequest)]),

        new(E.Vendor, "Vendor", "Purchasing › Vendors (new or edit vendor)",
        [
            One(
                new("code", "Code", Text, Required: true),
                new("name", "Legal name", Text, Required: true),
                new("uei", "UEI (SAM.gov)", Text),
                new("status", "Status", Select),
                new("paymentTermsDays", "Terms (net days)", Number),
                new("businessSize", "Business size", Select),
                new("socioEconomicCategories", "Socio-economic categories", MultiSelect),
                new("is1099", "1099 vendor", Bool),
                new("form1099Box", "1099 box", Select),
                new("remitToAddress", "Remit-to address", LongText),
                new("contactName", "Contact", Text),
                new("contactEmail", "Email", Text),
                new("contactPhone", "Phone", Text)),
        ], [typeof(SaveVendorRequest)]),

        new(E.Customer, "Customer", "Setup › Customers (new or edit customer)",
        [
            One(
                new("code", "Code", Text, Required: true),
                new("name", "Name", Text, Required: true),
                new("category", "Category", Text)),
        ], [typeof(SaveCustomerRequest)]),

        new(E.PurchaseOrder, "Purchase order", "Purchasing › Purchase orders (new PO or change order)",
        [
            One(
                new("vendorId", "Vendor", Select, Required: true),
                new("orderDate", "Order date", Date, Required: true),
                new("shipTo", "Ship to", Text),
                new("receivers", "Receivers (emails, comma separated)", Text),
                new("changeReason", "Reason for the change order", Text, Required: true),
                new("terms", "Terms", Text)),
        ], [typeof(SavePurchaseOrderRequest)]),

        new(E.ApBill, "AP bill", "Purchasing › Bills › New or edit bill",
        [
            One(
                new("vendorId", "Vendor", Select, Required: true),
                new("invoiceNumber", "Invoice number", Text, Required: true),
                new("invoiceDate", "Invoice date", Date, Required: true),
                new("dueDate", "Due date", Date),
                new("currencyCode", "Invoice currency", Select),
                new("description", "Description", Text)),
        ], [typeof(SaveBillRequest)]),

        new(E.Invoice, "Invoice", "Billing › New invoice",
        [
            new("details", "1. Contract and period",
            [
                new("contractId", "Contract", Select, Required: true),
                new("type", "Invoice type", Select),
                new("periodFrom", "Period from", Date, Required: true),
                new("periodTo", "Period to", Date, Required: true),
                new("invoiceDate", "Invoice date", Date),
                new("releaseWithhold", "Final invoice: release withheld fee", Bool),
                new("notes", "Notes (printed on the invoice)", LongText),
            ]),
        ], [typeof(GenerateInvoiceRequest)]),

        new(E.ExpenseReport, "Expense report", "Expenses (new or edit expense report)",
        [
            One(
                new("title", "Title", Text, Required: true),
                new("tripStart", "Trip start", Date),
                new("tripEnd", "Trip end", Date),
                new("purpose", "Business purpose", LongText)),
        ], [typeof(SaveExpenseReportRequest)]),

        new(E.Part, "Part", "Materials › Parts (new or edit part)",
        [
            One(
                new("number", "Part number", Text, Required: true),
                new("revision", "Revision", Text),
                new("description", "Description", Text, Required: true),
                new("unitOfMeasure", "Unit of measure", Text),
                new("source", "Make or buy", Select),
                new("tracking", "Lot/serial tracking", Select),
                new("costMethod", "Cost method", Select),
                new("standardCost", "Standard cost", Money),
                new("preferredVendorId", "Preferred vendor", Select),
                new("leadTimeDays", "Lead time (days)", Number),
                new("safetyStock", "Safety stock", Number),
                new("minimumOrderQuantity", "Minimum order", Number),
                new("orderMultiple", "Order multiple", Number),
                new("defaultBinId", "Default location", Select),
                new("notes", "Notes", LongText),
                new("isActive", "Active", Bool)),
        ], [typeof(SavePartRequest)]),

        new(E.Opportunity, "Opportunity", "Business development › Opportunity › Details",
        [
            new("opportunity", "The opportunity",
            [
                new("name", "Name", Text, Required: true),
                new("solicitationNumber", "Solicitation number", Text),
                new("description", "Description", LongText),
                new("agencyAccountId", "Agency or office", Select),
                new("agencyName", "Agency (not an account yet)", Text),
                new("naicsCode", "NAICS", Text),
                new("setAside", "Set-aside", Select),
                new("contractVehicle", "Contract vehicle", Text),
                new("contractType", "Contract type", Select),
                new("placeOfPerformance", "Place of performance", Text),
                new("kind", "New or recompete", Select),
                new("incumbentAccountId", "Incumbent", Select),
            ]),
            new("value", "Value and dates",
            [
                new("estimatedValue", "Estimated value ($)", Money),
                new("periodOfPerformanceMonths", "Period of performance (months)", Number),
                new("estimatedAwardDate", "Estimated award date", Date),
                new("rfiDueOn", "RFI response due", Date),
                new("rfpReleaseOn", "RFP release", Date),
                new("proposalDueOn", "Proposal due", Date),
            ]),
            new("people", "People and probability",
            [
                new("captureManagerEmail", "Capture manager", Person),
                new("proposalManagerEmail", "Proposal manager", Person),
                new("pwin", "Pwin (%)", Number),
                new("pgo", "Pgo (%)", Number),
                new("weightedValue", "Weighted value", Display),
            ]),
        ], [typeof(SaveOpportunityRequest)]),
    ];

    /// <summary>Entity types that only lists use (their built-in fields are list columns; custom fields may be added).</summary>
    public static IReadOnlyList<string> ListOnly { get; } = [E.Timesheet, E.SalesOrder];

    public static FormDefinition? Find(string entityType) => All.FirstOrDefault(f => f.EntityType == entityType);

    public static FormDefinition Get(string entityType) =>
        Find(entityType) ?? throw new ArgumentException($"There is no form for \"{entityType}\".", nameof(entityType));

    /// <summary>The page as built, as a layout.</summary>
    public static FormLayoutDto Layout(FormDefinition form) =>
        new(null, form.EntityType, null, null,
            form.Sections.Select(s => new FormSectionLayout(s.Key, s.Title, s.Fields.Select(f => new FormFieldPlacement(f.Key)).ToList())).ToList(),
            null, null);

    public static EntityFieldDto ToDto(string entityType, Field f) =>
        new(entityType, f.Key, f.Label, f.Type, false, f.Required, null, f.PermissionKey);
}
