using Workbench.Contracts.Companies;

namespace Workbench.Application.Companies;

/// <summary>
/// A kind of record that can be moved between companies (Setup › Companies › Move records) and that a license module owns, so core can't read its
/// tables: the accounting module answers for <see cref="CompanyRecordKind.BankAccount"/> and <see cref="CompanyRecordKind.FixedAsset"/>. A kind
/// nobody answers for lists nothing. Registered in the module's <c>ConfigureServices</c>.
/// </summary>
public interface ICompanyRecordSource
{
    CompanyRecordKind Kind { get; }

    /// <summary>The records of the kind (or just <paramref name="only"/>) with the company each is in and, when it cannot move, why. The caller fills in the company code.</summary>
    Task<IReadOnlyList<CompanyRecordDto>> ListAsync(IWorkbenchDbContext db, Guid? only, CancellationToken cancellationToken);

    /// <summary>Sets the company of one record. The caller saves.</summary>
    Task AssignAsync(IWorkbenchDbContext db, Guid id, Guid companyId, CancellationToken cancellationToken);
}
