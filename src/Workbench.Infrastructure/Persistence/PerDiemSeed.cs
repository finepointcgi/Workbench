using Microsoft.EntityFrameworkCore;
using Workbench.Application.PerDiem;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for per diem: the shipped sample CONUS rates (Washington DC, Huntsville AL, San Diego CA and the
/// standard rate) for the current and the previous fiscal year, and analyst@'s draft expense report for a three-day DC trip
/// last week with its per diem computed and applied (75% travel days, a provided lunch, one night above the lodging maximum
/// with a justification). Runs once, after the project-accounting seed.
/// </summary>
internal static class PerDiemSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<PerDiemRateBatch>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var year = PerDiemTables.FiscalYearOf(today);
        foreach (var fy in new[] { year - 1, year })
        {
            var parsed = PerDiemFileParser.Parse("gsa-conus-sample.csv", PerDiemRateService.SampleFile(), fy, PerDiemRateKind.Conus);
            PerDiemRateService.AddBatch(db, fy, PerDiemRateKind.Conus, "Sample", "gsa-conus-sample.csv", parsed.Rates, DefaultMieTiers.All, Actor, now.AddMinutes(fy - year));
        }

        await db.SaveChangesAsync(cancellationToken);

        var analyst = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "analyst@example.com", cancellationToken);
        var wbs = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Code == "1001.01.02", cancellationToken);
        if (analyst is null || wbs is null)
        {
            return;
        }

        // Monday to Wednesday of last week.
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 7);
        var report = new ExpenseReport
        {
            EmployeeId = analyst.Id,
            Title = "DC program review",
            Purpose = "Quarterly program review with the customer in Washington, DC",
            TripStart = monday,
            TripEnd = monday.AddDays(2),
            CreatedBy = analyst.Email,
            CreatedAtUtc = now,
        };
        var perDiem = new TravelPerDiem { ExpenseReportId = report.Id, WbsElementId = wbs.Id, UpdatedBy = analyst.Email, CreatedAtUtc = now, UpdatedAtUtc = now, AppliedAtUtc = now };
        perDiem.Destinations.Add(new TravelDestination
        {
            TravelPerDiemId = perDiem.Id, Kind = PerDiemRateKind.Conus, State = "DC", Destination = "District of Columbia", ArriveOn = monday, DepartOn = monday.AddDays(2),
        });
        perDiem.Days.Add(new TravelDay
        {
            TravelPerDiemId = perDiem.Id, Date = monday, LodgingClaimed = 289m, LodgingTaxClaimed = 43.35m,
            Justification = "Conference hotel; no rooms at or below the per diem rate within walking distance of the customer site.",
        });
        perDiem.Days.Add(new TravelDay { TravelPerDiemId = perDiem.Id, Date = monday.AddDays(1), LodgingClaimed = 240m, LodgingTaxClaimed = 36m, LunchProvided = true });
        perDiem.Days.Add(new TravelDay { TravelPerDiemId = perDiem.Id, Date = monday.AddDays(2) });
        db.AddRange(report, perDiem);

        var tables = await PerDiemTables.LoadAsync(db, cancellationToken);
        var stops = perDiem.Destinations.Select(d => new TravelDestinationDto(PerDiemTable.Conus, d.Country, d.State, d.Destination, d.ArriveOn, d.DepartOn)).ToList();
        var claims = perDiem.Days.ToDictionary(d => d.Date, d => new TravelDayInput(d.Date, d.LodgingClaimed, d.LodgingTaxClaimed, d.MieClaimed,
            d.BreakfastProvided, d.LunchProvided, d.DinnerProvided, d.Justification));
        var days = PerDiemCalculator.Compute(stops, claims, tables.Resolve, true, LodgingOveragePolicy.RequiresJustification);
        var location = days[0].Location;
        void Line(PerDiemLineKind kind, decimal amount, string description)
        {
            if (amount <= 0)
            {
                return;
            }

            var entry = new OdcEntry
            {
                ProjectId = wbs.ProjectId, WbsElementId = wbs.Id, Type = OdcType.Travel, IncurredOn = monday, Amount = amount, Description = description,
                EmployeeId = analyst.Id, ExpenseReportId = report.Id, SubmittedBy = analyst.Email, CreatedAtUtc = now,
            };
            db.Add(entry);
            db.Add(new PerDiemLine
            {
                TravelPerDiemId = perDiem.Id, ExpenseReportId = report.Id, OdcEntryId = entry.Id, Kind = kind, FirstDate = monday, LastDate = monday.AddDays(2), Amount = amount,
            });
        }

        Line(PerDiemLineKind.Lodging, days.Sum(d => d.LodgingAllowable), $"Lodging · {location} · 2 nights (max {days[0].LodgingMax:C}/night)");
        Line(PerDiemLineKind.LodgingTax, days.Sum(d => d.LodgingTaxAllowable), $"Lodging taxes · {location}");
        Line(PerDiemLineKind.LodgingJustified, days.Sum(d => d.LodgingJustified), $"Lodging above the per diem maximum (justified) · {location}");
        Line(PerDiemLineKind.Mie, days.Sum(d => d.MieAllowable), $"M&IE per diem · {location} · 3 days ({days[0].MieRate:C}/day; 75% on travel days; provided meals deducted)");
        db.Add(new OdcEntry
        {
            ProjectId = wbs.ProjectId, WbsElementId = wbs.Id, Type = OdcType.Travel, IncurredOn = monday, Amount = 486.20m, Description = "Airfare HSV–DCA round trip (coach)",
            Vendor = "Delta", EmployeeId = analyst.Id, ExpenseReportId = report.Id, SubmittedBy = analyst.Email, CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
