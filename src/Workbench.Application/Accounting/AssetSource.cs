namespace Workbench.Application.Accounting;

/// <summary>A fixed asset as other modules show it.</summary>
public sealed record AssetRef(Guid Id, string TagNumber, string Description);

/// <summary>What the CAS disclosure statement reports about the asset policy.</summary>
public sealed record AssetPolicySummary(decimal CapitalizationThreshold, IReadOnlyList<string> DepreciationMethods);

/// <summary>
/// <b>What other license modules ask of the fixed asset register.</b> Fixed assets belong to the accounting module, which registers the implementation; a host without it
/// registers nothing and the modules treat the answer as "no assets". Workforce (the assets a leaver holds), project controls (government property still in service at
/// closeout) and government contracting (the CAS disclosure's capitalization threshold and depreciation methods) read the register this way instead of through its tables.
/// </summary>
public interface IAssetSource
{
    /// <summary>The assets in service in an employee's custody (for the offboarding checklist).</summary>
    Task<IReadOnlyList<AssetRef>> InServiceWithCustodianAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken cancellationToken);

    /// <summary>Records that the asset came back from an employee at offboarding (a transfer to no custodian).</summary>
    Task ReturnAtOffboardingAsync(Guid assetId, DateOnly date, string employeeName, CancellationToken cancellationToken);

    /// <summary>The tags of government-furnished or contractor-acquired assets still in service on a contract or on any of its projects.</summary>
    Task<IReadOnlyList<string>> OpenGovernmentPropertyAsync(IWorkbenchDbContext db, Guid? contractId, IReadOnlyCollection<Guid> projectIds, CancellationToken cancellationToken);

    /// <summary>The in-service date of the asset with this description (the sample data's anchor), or null.</summary>
    Task<DateOnly?> InServiceDateAsync(IWorkbenchDbContext db, string description, CancellationToken cancellationToken);

    /// <summary>The capitalization threshold and the depreciation methods the active asset categories use.</summary>
    Task<AssetPolicySummary> PolicyAsync(IWorkbenchDbContext db, CancellationToken cancellationToken);
}
