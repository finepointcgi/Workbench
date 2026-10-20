using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Application.Integrations;

namespace Workbench.Application.ProjectAccounting;

public sealed class FiscalCalendarService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : IFiscalCalendarService
{
    public async Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var years = await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, y => y.Year, cancellationToken);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken);
        return periods
            .Where(p => fiscalYear is null || years[p.FiscalYearId] == fiscalYear)
            .OrderBy(p => p.Start)
            .Select(p => p.ToDto(years[p.FiscalYearId]))
            .ToList();
    }

    public async Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= date && p.End >= date, cancellationToken);
        if (period is null)
        {
            return null;
        }

        var year = await db.Set<FiscalYear>().AsNoTracking().FirstAsync(y => y.Id == period.FiscalYearId, cancellationToken);
        return period.ToDto(year.Year);
    }

    public async Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default)
    {
        if (start.Day != 1)
        {
            throw new ArgumentException("A fiscal year must start on the first day of a month.");
        }

        var end = start.AddYears(1).AddDays(-1);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<FiscalYear>().AnyAsync(y => y.Year == fiscalYear, cancellationToken))
        {
            throw new ArgumentException($"Fiscal year {fiscalYear} already exists.");
        }

        if (await db.Set<FiscalYear>().AnyAsync(y => y.Start <= end && y.End >= start, cancellationToken))
        {
            throw new ArgumentException("That fiscal year overlaps an existing one.");
        }

        var year = new FiscalYear { Year = fiscalYear, Start = start, End = end };
        db.Set<FiscalYear>().Add(year);
        var periods = Enumerable.Range(0, 12).Select(i =>
        {
            var periodStart = start.AddMonths(i);
            return new FiscalPeriod
            {
                FiscalYearId = year.Id,
                Number = i + 1,
                Name = $"{periodStart:yyyy-MM}",
                Start = periodStart,
                End = periodStart.AddMonths(1).AddDays(-1),
            };
        }).ToList();
        db.Set<FiscalPeriod>().AddRange(periods);
        audit.Record(db, nameof(FiscalYear), year.Id, "Created", after: new { year.Year, year.Start, year.End });
        await db.SaveChangesAsync(cancellationToken);
        return periods.Select(p => p.ToDto(fiscalYear)).ToList();
    }

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default)
    {
        var why = reason.Required("Reason");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var period = await db.Set<FiscalPeriod>().FirstOrDefaultAsync(p => p.Id == periodId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Fiscal period", periodId);
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstAsync(y => y.Id == period.FiscalYearId, cancellationToken);
        var before = period.ToDto(year.Year);
        period.Status = (PeriodStatus)(int)status;
        audit.Record(db, nameof(FiscalPeriod), period.Id, status == FiscalPeriodState.Closed ? "Closed" : "Reopened", before, period.ToDto(year.Year), why);
        if (period.Status == PeriodStatus.Closed && before.Status != FiscalPeriodState.Closed)
        {
            IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.PeriodClosed, IntegrationEventPayloads.Period(period, year.Year), audit.Actor);
        }
        await db.SaveChangesAsync(cancellationToken);
        return period.ToDto(year.Year);
    }
}
