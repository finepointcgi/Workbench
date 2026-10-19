using Workbench.Application.Security;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Security;

namespace Workbench.Application.Assets;

/// <summary>
/// Records an <see cref="SecurityEventTypes.ExportAssets"/> event for every asset export (register, schedules, roll-forward,
/// government property). Registered right after the report service, so the permission decorator wraps it and only
/// allowed, successful exports are recorded.
/// </summary>
public sealed class SecurityLoggingAssetReportService(IAssetReportService inner, SecurityEventLog log) : IAssetReportService
{
    public Task<IReadOnlyList<AssetSummaryDto>> GetRegisterAsync(AssetQuery query, CancellationToken cancellationToken = default) =>
        inner.GetRegisterAsync(query, cancellationToken);

    public Task<DepreciationScheduleDto> GetScheduleAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default) =>
        inner.GetScheduleAsync(assetId, book, cancellationToken);

    public Task<RollForwardDto> GetRollForwardAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        inner.GetRollForwardAsync(from, to, cancellationToken);

    public Task<IReadOnlyList<GovernmentPropertyRowDto>> GetGovernmentPropertyAsync(CancellationToken cancellationToken = default) =>
        inner.GetGovernmentPropertyAsync(cancellationToken);

    public async Task<AssetExportFile> ExportRegisterCsvAsync(CancellationToken cancellationToken = default) =>
        await RecordAsync(await inner.ExportRegisterCsvAsync(cancellationToken), "Asset register", cancellationToken);

    public async Task<AssetExportFile> ExportScheduleCsvAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default) =>
        await RecordAsync(await inner.ExportScheduleCsvAsync(assetId, book, cancellationToken), $"Depreciation schedule ({book}), asset {assetId}", cancellationToken);

    public async Task<AssetExportFile> ExportRollForwardCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await RecordAsync(await inner.ExportRollForwardCsvAsync(from, to, cancellationToken), $"Roll-forward {from:yyyy-MM-dd}..{to:yyyy-MM-dd}", cancellationToken);

    public async Task<AssetExportFile> ExportGovernmentPropertyCsvAsync(CancellationToken cancellationToken = default) =>
        await RecordAsync(await inner.ExportGovernmentPropertyCsvAsync(cancellationToken), "Government property listing", cancellationToken);

    private async Task<AssetExportFile> RecordAsync(AssetExportFile file, string what, CancellationToken cancellationToken)
    {
        var rows = Math.Max(0, file.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);
        await log.RecordAsync(SecurityEventTypes.ExportAssets, file.FileName, detail: $"{what}: {rows} rows", cancellationToken: cancellationToken);
        return file;
    }
}
