namespace Workbench.Domain.Companies;

/// <summary>
/// A record that belongs to one legal company: its transactions post to that company's ledger, and people limited to
/// other companies can't see it.
/// </summary>
public interface ICompanyOwned
{
    Guid CompanyId { get; set; }
}

/// <summary>
/// A legal company (entity) within the tenant, like a Costpoint company. Every tenant has the default company, which owns
/// every record created before a second company existed. Companies share the tenant's chart of accounts, fiscal calendar,
/// vendors, customers and labor categories; each keeps its own ledger, period close, projects, contracts, invoices, bills,
/// bank accounts, assets, indirect pools and employees.
/// </summary>
public sealed class Company : Entity
{
    /// <summary>
    /// The default company's fixed id. Migrations add every new <c>CompanyId</c> column with this id as its default, so
    /// existing rows land in the default company and a single-company tenant sees no change.
    /// </summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-00000000c001");

    /// <summary>Short, upper-case and unique, for example <c>MAIN</c> or <c>FED</c>.</summary>
    public required string Code { get; set; }

    public required string LegalName { get; set; }

    /// <summary>Employer identification number, <c>12-3456789</c>.</summary>
    public string? Ein { get; set; }

    /// <summary>
    /// The month the company's fiscal year starts (10 = October). Companies share the tenant's fiscal calendar so their
    /// periods line up for consolidation; this records the company's own year for reports and must match it.
    /// </summary>
    public int FiscalYearStartMonth { get; set; } = 10;

    /// <summary>ISO currency code of the company's books. Always <c>USD</c> for now.</summary>
    public string HomeCurrencyCode { get; set; } = "USD";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public bool IsDefault => Id == DefaultId;
}

/// <summary>
/// The accounts one company uses with one other company: <see cref="DueFromAccountId"/> (an asset, what the counterparty
/// owes this company) and <see cref="DueToAccountId"/> (a liability, what this company owes the counterparty). A pair of
/// companies has a row each way.
/// </summary>
public sealed class IntercompanyAccount : Entity
{
    public Guid CompanyId { get; set; }
    public Guid CounterpartyCompanyId { get; set; }
    public Guid DueFromAccountId { get; set; }
    public Guid DueToAccountId { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One company's close of a shared fiscal period. A period closed for the whole tenant is closed for every company; a
/// company can also close its own periods earlier. Reopening keeps the row with <see cref="IsClosed"/> false.
/// </summary>
public sealed class CompanyPeriodClose : Entity
{
    public Guid CompanyId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public bool IsClosed { get; set; }
    public required string ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Limits a user to some companies. A user with no rows can use every company.</summary>
public sealed class UserCompanyAccess : Entity
{
    public Guid UserId { get; set; }
    public Guid CompanyId { get; set; }
}

/// <summary>The company a user is working in (the app bar switcher). No row, or a null company, means all their companies.</summary>
public sealed class UserCompanySelection : Entity
{
    public Guid UserId { get; set; }
    public Guid? CompanyId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
