using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations;

/// <summary>
/// Raises an integration event: adds an <see cref="IntegrationOutboxEvent"/> to the caller's unit of work, so it's saved with
/// the caller's <c>SaveChangesAsync</c> (one transaction) and sent only if the change was. Call it next to the audit row,
/// before saving. The background dispatcher fans it out to webhooks and connectors.
/// </summary>
public static class IntegrationOutbox
{
    public const int PayloadVersion = 1;

    /// <summary>The event type of the "Send test" delivery; not in the catalog and never fanned out.</summary>
    public const string PingEventType = "ping";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Adds the event to <paramref name="db"/>. <paramref name="data"/> must be the type's data record.</summary>
    public static IntegrationOutboxEvent Raise(IWorkbenchDbContext db, IntegrationEventType type, IntegrationEventData data, string? actor, DateTime? occurredAtUtc = null)
    {
        if (data.GetType() != type.DataType)
        {
            throw new ArgumentException($"Event {type.Key} carries {type.DataType.Name}, not {data.GetType().Name}.", nameof(data));
        }

        var row = Build(type.Key, type.Subject, data.GetSubjectId(), data, actor, occurredAtUtc ?? DateTime.UtcNow);
        db.Set<IntegrationOutboxEvent>().Add(row);
        return row;
    }

    internal static IntegrationOutboxEvent Build(string eventType, string subject, Guid subjectId, object data, string? actor, DateTime occurredAtUtc)
    {
        var row = new IntegrationOutboxEvent
        {
            EventType = eventType,
            Subject = subject,
            SubjectId = subjectId,
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim().ToLowerInvariant(),
            OccurredAtUtc = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
            PayloadJson = "",
        };
        var envelope = new Envelope(row.Id, eventType, PayloadVersion, row.OccurredAtUtc, subject, subjectId, row.Actor, data);
        row.PayloadJson = JsonSerializer.Serialize(envelope, Json);
        return row;
    }

    /// <summary>The JSON body every receiver gets. <c>data</c> is the event type's record (docs/integrations/webhooks.md).</summary>
    private sealed record Envelope(Guid Id, string Type, int Version, DateTime OccurredAt, string Subject, Guid SubjectId, string? Actor, object Data);
}

/// <summary>Builds each event's data record from the entity, so every caller sends the same fields.</summary>
public static class IntegrationEventPayloads
{
    public static TimesheetEventData Timesheet(Domain.ProjectAccounting.Timesheet sheet, Domain.ProjectAccounting.Employee employee) =>
        new(sheet.Id, employee.Id, employee.EmployeeNumber, employee.DisplayName, sheet.PeriodStart, sheet.PeriodEnd,
            sheet.Entries.Sum(e => e.Hours), sheet.Status.ToString());

    public static InvoiceEventData Invoice(Domain.Billing.Invoice invoice) =>
        new(invoice.Id, invoice.Number, invoice.Type.ToString(), invoice.CompanyId, invoice.ContractId, invoice.CustomerId, invoice.InvoiceDate, invoice.DueDate,
            invoice.Total, invoice.AmountPaid, invoice.Balance, invoice.CurrencyCode, invoice.Status.ToString());

    public static BillEventData Bill(Domain.Purchasing.ApBill bill) =>
        new(bill.Id, bill.Number, bill.CompanyId, bill.VendorId, bill.InvoiceNumber, bill.InvoiceDate, bill.DueDate,
            bill.Amount, bill.PaidAmount, bill.Balance, bill.CurrencyCode, bill.Status.ToString());

    public static PurchaseOrderEventData PurchaseOrder(Domain.Purchasing.PurchaseOrder po) =>
        new(po.Id, po.Number, po.VendorId, po.OrderDate, po.Revision, po.Total, po.Status.ToString());

    public static ProjectEventData Project(Domain.ProjectAccounting.Project project) =>
        new(project.Id, project.Code, project.Name, project.CompanyId, project.ContractId, project.ProjectManagerId, project.Status.ToString(), project.Start, project.Finish);

    public static EmployeeEventData Employee(Domain.ProjectAccounting.Employee employee) =>
        new(employee.Id, employee.EmployeeNumber, employee.DisplayName, employee.Email, employee.CompanyId, employee.OrgUnitId, employee.SupervisorId,
            employee.HireDate, employee.TerminationDate);

    public static OpportunityStageEventData OpportunityStage(Domain.Crm.Opportunity opportunity, Domain.Crm.OpportunityStage from) =>
        new(opportunity.Id, opportunity.Number, opportunity.Name, from.ToString(), opportunity.Stage.ToString(), opportunity.EstimatedValue, opportunity.EstimatedAwardDate);

    public static PeriodEventData Period(Domain.ProjectAccounting.FiscalPeriod period, int fiscalYear) =>
        new(period.Id, fiscalYear, period.Number, period.Name, period.Start, period.End, period.Status.ToString());
}
