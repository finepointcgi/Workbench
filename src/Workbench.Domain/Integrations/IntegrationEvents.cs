namespace Workbench.Domain.Integrations;

/// <summary>
/// The typed catalog of events Workbench sends to webhooks and connectors. Each type names the one data record it carries,
/// so a payload can't drift from its documentation (docs/integrations/webhooks.md). Keys are lower-case, dot-separated,
/// area first, and never change once shipped; add new types at the end.
/// </summary>
public sealed record IntegrationEventType(string Key, string Subject, Type DataType)
{
    public static readonly IntegrationEventType TimesheetSubmitted = new("timesheet.submitted", "Timesheet", typeof(TimesheetEventData));
    public static readonly IntegrationEventType TimesheetApproved = new("timesheet.approved", "Timesheet", typeof(TimesheetEventData));
    public static readonly IntegrationEventType InvoiceApproved = new("invoice.approved", "Invoice", typeof(InvoiceEventData));
    public static readonly IntegrationEventType InvoiceSent = new("invoice.sent", "Invoice", typeof(InvoiceEventData));
    public static readonly IntegrationEventType InvoicePaid = new("invoice.paid", "Invoice", typeof(InvoiceEventData));
    public static readonly IntegrationEventType BillApproved = new("bill.approved", "Bill", typeof(BillEventData));
    public static readonly IntegrationEventType BillPaid = new("bill.paid", "Bill", typeof(BillEventData));
    public static readonly IntegrationEventType PurchaseOrderApproved = new("purchase_order.approved", "PurchaseOrder", typeof(PurchaseOrderEventData));
    public static readonly IntegrationEventType ProjectCreated = new("project.created", "Project", typeof(ProjectEventData));
    public static readonly IntegrationEventType EmployeeHired = new("employee.hired", "Employee", typeof(EmployeeEventData));
    public static readonly IntegrationEventType EmployeeTerminated = new("employee.terminated", "Employee", typeof(EmployeeEventData));
    public static readonly IntegrationEventType OpportunityStageChanged = new("opportunity.stage_changed", "Opportunity", typeof(OpportunityStageEventData));
    public static readonly IntegrationEventType PeriodClosed = new("period.closed", "FiscalPeriod", typeof(PeriodEventData));
    public static readonly IntegrationEventType ShipmentShipped = new("shipment.shipped", "Shipment", typeof(ShipmentEventData));
    public static readonly IntegrationEventType RfqAwarded = new("rfq.awarded", "Rfq", typeof(RfqAwardEventData));
    public static readonly IntegrationEventType NcrDispositioned = new("ncr.dispositioned", "Nonconformance", typeof(NcrEventData));

    public static IReadOnlyList<IntegrationEventType> All { get; } =
    [
        TimesheetSubmitted, TimesheetApproved, InvoiceApproved, InvoiceSent, InvoicePaid, BillApproved, BillPaid,
        PurchaseOrderApproved, ProjectCreated, EmployeeHired, EmployeeTerminated, OpportunityStageChanged, PeriodClosed,
        ShipmentShipped, RfqAwarded, NcrDispositioned,
    ];

    public static IntegrationEventType? Find(string key) => All.FirstOrDefault(t => t.Key == key);
}

/// <summary>
/// What an event says. Payloads are allow-lists: ids and summary fields only. Never add pay or cost rates, salaries,
/// bank, tax or EEO data, or sheet cell values (restricted columns): a webhook receiver is outside every permission check.
/// </summary>
public abstract record IntegrationEventData
{
    /// <summary>The id of the record the event is about (sent as the payload's <c>subjectId</c>; a method so it isn't serialized twice).</summary>
    public abstract Guid GetSubjectId();
}

public sealed record TimesheetEventData(
    Guid TimesheetId, Guid EmployeeId, string EmployeeNumber, string EmployeeName, DateOnly PeriodStart, DateOnly PeriodEnd,
    decimal TotalHours, string Status) : IntegrationEventData
{
    public override Guid GetSubjectId() => TimesheetId;
}

public sealed record InvoiceEventData(
    Guid InvoiceId, string Number, string Type, Guid CompanyId, Guid ContractId, Guid CustomerId, DateOnly InvoiceDate, DateOnly DueDate,
    decimal Total, decimal AmountPaid, decimal Balance, string? CurrencyCode, string Status) : IntegrationEventData
{
    public override Guid GetSubjectId() => InvoiceId;
}

public sealed record BillEventData(
    Guid BillId, string Number, Guid CompanyId, Guid VendorId, string VendorInvoiceNumber, DateOnly InvoiceDate, DateOnly DueDate,
    decimal Amount, decimal PaidAmount, decimal Balance, string? CurrencyCode, string Status) : IntegrationEventData
{
    public override Guid GetSubjectId() => BillId;
}

public sealed record PurchaseOrderEventData(
    Guid PurchaseOrderId, string Number, Guid VendorId, DateOnly OrderDate, int Revision, decimal Total, string Status) : IntegrationEventData
{
    public override Guid GetSubjectId() => PurchaseOrderId;
}

public sealed record ProjectEventData(
    Guid ProjectId, string Code, string Name, Guid CompanyId, Guid? ContractId, Guid? ProjectManagerId, string Status, DateOnly? Start, DateOnly? Finish)
    : IntegrationEventData
{
    public override Guid GetSubjectId() => ProjectId;
}

public sealed record EmployeeEventData(
    Guid EmployeeId, string EmployeeNumber, string DisplayName, string Email, Guid CompanyId, Guid? OrgUnitId, Guid? SupervisorId,
    DateOnly HireDate, DateOnly? TerminationDate) : IntegrationEventData
{
    public override Guid GetSubjectId() => EmployeeId;
}

public sealed record OpportunityStageEventData(
    Guid OpportunityId, string Number, string Name, string FromStage, string ToStage, decimal EstimatedValue, DateOnly? EstimatedAwardDate)
    : IntegrationEventData
{
    public override Guid GetSubjectId() => OpportunityId;
}

public sealed record PeriodEventData(Guid FiscalPeriodId, int FiscalYear, int Number, string Name, DateOnly Start, DateOnly End, string Status)
    : IntegrationEventData
{
    public override Guid GetSubjectId() => FiscalPeriodId;
}

public sealed record ShipmentEventData(
    Guid ShipmentId, string Number, string? ShipmentNumber, Guid CompanyId, Guid SalesOrderId, string SalesOrderNumber, Guid CustomerId,
    Guid? ContractId, DateOnly? ShipDate, string? Carrier, string? TrackingNumber, bool IsFinal, decimal Value) : IntegrationEventData
{
    public override Guid GetSubjectId() => ShipmentId;
}

public sealed record RfqAwardEventData(
    Guid RfqId, string Number, string Title, Guid AwardId, int VendorsInvited, int QuotesReceived, bool SoleSource, decimal AwardedTotal,
    string PurchaseOrderNumbers) : IntegrationEventData
{
    public override Guid GetSubjectId() => RfqId;
}

public sealed record NcrEventData(
    Guid NcrId, string Number, Guid PartId, string Lot, string Serial, decimal Quantity, string Disposition, Guid? VendorId, Guid? WorkOrderId)
    : IntegrationEventData
{
    public override Guid GetSubjectId() => NcrId;
}
