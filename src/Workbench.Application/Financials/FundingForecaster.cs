using System.Globalization;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.Financials;

public sealed record ContractTerms(Guid Id, string ContractNumber, string Name, ContractKind Type, DateOnly PeriodEnd, decimal CeilingValue, decimal FundedValue);

public sealed record SpendEntry(DateOnly Date, decimal Amount);

/// <summary>Funded and ceiling value remaining, burn rate, funding-exhaustion date and the 75% / 90% alerts.</summary>
public static class FundingForecaster
{
    public const int BurnPeriods = 3;
    public const decimal WarningThreshold = 0.75m;
    public const decimal CriticalThreshold = 0.90m;

    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static ContractFundingDto Forecast(ContractTerms contract, int projectCount, IReadOnlyList<SpendEntry> spend, IReadOnlyList<PeriodInfo> calendar, DateOnly asOf)
    {
        var spent = spend.Where(s => s.Date <= asOf).Sum(s => s.Amount);
        var fundedRemaining = contract.FundedValue - spent;
        var ceilingRemaining = contract.CeilingValue - spent;
        decimal? ratio = contract.FundedValue > 0 ? spent / contract.FundedValue : null;
        decimal? percent = ratio is { } r ? Math.Round(r, 4, MidpointRounding.AwayFromZero) : null;

        // Burn window: the last 3 fiscal periods that have started, the current one counted up to the as-of date.
        var window = calendar.Where(p => p.Start <= asOf).OrderByDescending(p => p.Start).Take(BurnPeriods).ToList();
        var windowStart = window.Count > 0 ? window.Min(p => p.Start) : asOf.AddDays(-89);
        var windowSpend = spend.Where(s => s.Date >= windowStart && s.Date <= asOf).Sum(s => s.Amount);
        var averagePerPeriod = Money.Round(windowSpend / (window.Count > 0 ? window.Count : BurnPeriods));
        var elapsedDays = asOf.DayNumber - windowStart.DayNumber + 1;
        var burnPerDay = elapsedDays > 0 ? windowSpend / elapsedDays : 0m;

        DateOnly? exhaustion = null;
        if (fundedRemaining > 0 && burnPerDay > 0)
        {
            var days = (int)Math.Min(Math.Ceiling(fundedRemaining / burnPerDay), 365 * 100);
            exhaustion = asOf.AddDays(days);
        }

        var alerts = new List<FundingAlertDto>();
        // Thresholds compare the unrounded ratio, so 74.999% doesn't trip the 75% alert.
        if (ratio is { } p)
        {
            if (p >= CriticalThreshold)
            {
                alerts.Add(new FundingAlertDto(FundingAlertLevel.Critical, 90, $"{percent!.Value.ToString("P1", Us)} of funded value spent (90% threshold)."));
            }
            else if (p >= WarningThreshold)
            {
                alerts.Add(new FundingAlertDto(FundingAlertLevel.Warning, 75, $"{percent!.Value.ToString("P1", Us)} of funded value spent (75% threshold)."));
            }
        }

        if (contract.FundedValue > 0 && fundedRemaining <= 0)
        {
            alerts.Add(new FundingAlertDto(FundingAlertLevel.Critical, null, "Funded value is exhausted; further work is unfunded and at risk."));
        }

        if (contract.CeilingValue > 0 && ceilingRemaining <= 0)
        {
            alerts.Add(new FundingAlertDto(FundingAlertLevel.Critical, null, "Contract ceiling is reached."));
        }

        if (exhaustion is { } date && date < contract.PeriodEnd)
        {
            alerts.Add(new FundingAlertDto(FundingAlertLevel.Info, null,
                $"At the current burn, funding runs out on {date:yyyy-MM-dd}, before the period of performance ends on {contract.PeriodEnd:yyyy-MM-dd}."));
        }

        return new ContractFundingDto(contract.Id, contract.ContractNumber, contract.Name, contract.Type, contract.PeriodEnd, projectCount,
            contract.CeilingValue, contract.FundedValue, spent, fundedRemaining, ceilingRemaining, percent, averagePerPeriod, exhaustion, asOf, alerts);
    }
}
