using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.PerDiem;
using Workbench.Application.Travel;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Travel;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for travel and cards: analyst@'s approved trip TA-000001 for the DC program review (the per
/// diem seed's draft expense report is its expense report, so the page shows estimate vs actual), and a company-paid
/// "Citi corporate card" program with analyst@'s card ••4417 and one imported statement: the DCA taxi is on the trip report,
/// a rental car, a coffee and a 40-day-old purchase are still unassigned (the last one overdue), plus a refund.
/// Runs once, after the per diem seed.
/// </summary>
internal static class TravelSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<TravelAuthorization>().AnyAsync(cancellationToken) || await db.Set<CardProgram>().AnyAsync(cancellationToken))
        {
            return;
        }

        var analyst = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "analyst@example.com", cancellationToken);
        var wbs = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Code == "1001.01.02", cancellationToken);
        var report = analyst is null ? null : await db.Set<ExpenseReport>().FirstOrDefaultAsync(r => r.EmployeeId == analyst.Id && r.Title == "DC program review", cancellationToken);
        if (analyst is null || wbs is null || report?.TripStart is not { } start || report.TripEnd is not { } end)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var pm = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == analyst.SupervisorId, cancellationToken);

        // The approved trip, priced from the per diem tables, with the per diem seed's report as its expense report.
        var stop = new TravelDestinationDto(PerDiemTable.Conus, "US", "DC", "District of Columbia", start, end);
        var estimate = TravelEstimator.Estimate(await PerDiemTables.LoadAsync(db, cancellationToken), [stop], 450m, 0m, 60m);
        var trip = new TravelAuthorization
        {
            Number = "TA-000001",
            EmployeeId = analyst.Id,
            Title = "DC program review",
            Purpose = report.Purpose ?? "Quarterly program review with the customer in Washington, DC",
            ProjectId = wbs.ProjectId,
            WbsElementId = wbs.Id,
            TripStart = start,
            TripEnd = end,
            EstimatedAirfare = 450m,
            EstimatedLodging = estimate.Lodging,
            EstimatedMie = estimate.Mie,
            EstimatedOther = 60m,
            OtherDescription = "Taxis to and from the airport",
            ExpenseReportId = report.Id,
            Status = TravelAuthorizationStatus.Approved,
            CreatedBy = analyst.Email,
            CreatedAtUtc = now.AddDays(-21),
            UpdatedAtUtc = now.AddDays(-19),
            SubmittedBy = analyst.Email,
            SubmittedAtUtc = now.AddDays(-21),
            ApprovedBy = pm?.Email ?? Actor,
            ApprovedAtUtc = now.AddDays(-19),
        };
        trip.Destinations.Add(new TravelAuthorizationDestination
        {
            TravelAuthorizationId = trip.Id, Order = 0, Kind = PerDiemRateKind.Conus, State = "DC", Destination = "District of Columbia", ArriveOn = start, DepartOn = end,
        });
        db.Add(trip);

        // A company-paid card program, analyst@'s card and one statement.
        var liability = await CardSupport.EnsureLiabilityAccountAsync(db, now, cancellationToken);
        var program = new CardProgram
        {
            Name = "Citi corporate card", Issuer = "Citibank", PaymentResponsibility = CardPaymentResponsibility.CompanyPaid, LiabilityAccountId = liability.Id,
            UnsubmittedAlertDays = 30, CreatedBy = Actor, CreatedAtUtc = now,
        };
        var card = new CorporateCard { ProgramId = program.Id, EmployeeId = analyst.Id, Last4 = "4417", NameOnCard = "ALEX RIVERA", CreatedAtUtc = now };
        db.AddRange(program, card);

        string D(DateOnly d) => d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
        var csv = $"""
            Transaction Date,Posted Date,Card Number,Description,Category,Amount
            {D(today.AddDays(-40))},{D(today.AddDays(-39))},XXXX-XXXX-XXXX-4417,AMAZON MKTPLACE PMTS,5942,64.99
            {D(start)},{D(start.AddDays(1))},XXXX-XXXX-XXXX-4417,UBER *TRIP DCA,4121,38.40
            {D(start.AddDays(1))},{D(start.AddDays(2))},XXXX-XXXX-XXXX-4417,STARBUCKS #11823 WASHINGTON DC,5814,6.75
            {D(end)},{D(end.AddDays(1))},XXXX-XXXX-XXXX-4417,HERTZ RENT-A-CAR DCA,3357,142.18
            {D(end)},{D(end.AddDays(1))},XXXX-XXXX-XXXX-4417,UBER *TRIP REFUND,4121,-5.00
            """;
        var rows = new CsvCardFeed().Read(csv).Transactions;
        var keys = CardFeeds.DedupeKeys(rows);
        var import = new CardStatementImport
        {
            ProgramId = program.Id, FileName = "citi-statement-sample.csv", Format = "CSV", RowCount = rows.Count, ImportedCount = rows.Count,
            ImportedTotal = rows.Sum(r => r.Amount), ImportedBy = Actor, ImportedAtUtc = now,
        };
        db.Add(import);
        CardTransaction? taxi = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var charge = new CardTransaction
            {
                ProgramId = program.Id, ImportId = import.Id, CardId = card.Id, EmployeeId = analyst.Id, CardLast4 = row.CardLast4, DedupeKey = keys[i],
                TransactionDate = row.TransactionDate, PostedDate = row.PostedDate, Amount = row.Amount, Merchant = row.Merchant, MerchantCategory = row.MerchantCategory,
                CreatedAtUtc = now,
            };
            db.Add(charge);
            taxi ??= row.Merchant == "UBER *TRIP DCA" ? charge : null;
        }

        // The taxi is on the trip's report: an ordinary expense line, its amount locked to the charge.
        if (taxi is not null)
        {
            var line = new OdcEntry
            {
                ProjectId = wbs.ProjectId, WbsElementId = wbs.Id, Type = OdcType.Travel, IncurredOn = taxi.TransactionDate, Amount = taxi.Amount,
                Description = "Taxi DCA to hotel", Vendor = taxi.Merchant, EmployeeId = analyst.Id, ExpenseReportId = report.Id, SubmittedBy = analyst.Email, CreatedAtUtc = now,
            };
            db.Add(line);
            (taxi.OdcEntryId, taxi.AttachedAtUtc, taxi.AttachedBy) = (line.Id, now, analyst.Email);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
