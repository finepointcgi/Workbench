using Microsoft.Extensions.Options;
using Workbench.Application.Compliance;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Compliance;
using Workbench.Domain.Compliance;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Compliance;

/// <summary>CPSR procurement files: scoring against the thresholds, the readiness sample and RFQ evidence, over <see cref="PurchasingKit"/> as finance@.</summary>
public sealed class CpsrTests
{
    private static readonly CpsrSettingsDto Settings = new CpsrOptions().ToSettings();
    private static readonly DateOnly Day = PurchasingKit.Day;

    private static CpsrDocument Doc(decimal value, bool government = true, string? size = "Small") =>
        new(ProcurementDocumentKind.PurchaseOrder, Guid.NewGuid(), "PO-1", "Acme", size, Day, value, government, government ? "W91-0001" : null);

    private static CpsrElementStatus Status(IReadOnlyList<CpsrElementResultDto> r, CpsrElement e) => r.Single(x => x.Element == e).Status;

    private static ProcurementFile CompleteFile(params int[] levels)
    {
        var file = new ProcurementFile
        {
            Competition = CompetitionMethod.Competitive,
            QuotesSolicited = 4,
            QuotesReceived = 3,
            PriceAnalysis = PriceAnalysisKind.Both,
            PriceAnalysisNotes = "Compared three quotes and the vendor's cost breakdown.",
            FlowdownClauses = string.Join(",", CpsrOptions.DefaultClauses.Concat(CpsrOptions.DefaultClausesAboveSat)),
            DebarmentCheckedOn = Day.AddDays(-5),
            DebarmentResult = "No exclusions",
            SmallBusinessReportPeriod = "FY2026 ISR (Mar)",
            UpdatedBy = "finance@example.com",
        };
        foreach (var level in levels)
        {
            file.Approvals.Add(new ProcurementFileApproval { Level = level, LevelName = Settings.ApprovalLevels[level - 1].Name, ApprovedBy = "x@example.com" });
        }

        return file;
    }

    [Fact]
    public void A_micro_purchase_only_needs_the_buyers_approval()
    {
        var r = CpsrScoring.Evaluate(Doc(9_000m), null, null, Settings);
        Assert.All(new[] { CpsrElement.Competition, CpsrElement.PriceAnalysis, CpsrElement.Debarment, CpsrElement.SmallBusiness },
            e => Assert.Equal(CpsrElementStatus.NotApplicable, Status(r, e)));
        Assert.Equal(CpsrElementStatus.Missing, Status(r, CpsrElement.Flowdowns)); // Government-funded orders flow clauses down at any value.
        Assert.Equal(CpsrElementStatus.Missing, Status(r, CpsrElement.Approvals));
        Assert.Contains("Buyer", r.Single(x => x.Element == CpsrElement.Approvals).Detail);
        Assert.Equal(0, CpsrScoring.Score(r));

        var commercial = CpsrScoring.Evaluate(Doc(9_000m, government: false), CompleteFile(1), null, Settings);
        Assert.Equal(100, CpsrScoring.Score(commercial));
        Assert.Equal(CpsrElementStatus.NotApplicable, Status(commercial, CpsrElement.Flowdowns));
    }

    [Fact]
    public void A_large_order_needs_every_element_and_the_matching_approval_level()
    {
        // 400,000 is above the simplified acquisition threshold: the SAT clauses and the contracts director's approval apply.
        var doc = Doc(400_000m);
        Assert.Equal(new[] { 1, 2, 3 }, CpsrScoring.RequiredApprovals(doc.Value, Settings).Select(l => l.Level).ToArray());
        Assert.Contains("52.219-8", CpsrScoring.RequiredClauses(doc, Settings));

        Assert.Equal(100, CpsrScoring.Score(CpsrScoring.Evaluate(doc, CompleteFile(1, 2, 3), null, Settings)));

        var partial = CompleteFile(1, 2);
        partial.QuotesReceived = 1;
        partial.FlowdownClauses = string.Join(",", CpsrOptions.DefaultClauses);
        partial.DebarmentCheckedOn = Day.AddDays(3); // After award.
        var r = CpsrScoring.Evaluate(doc, partial, null, Settings);
        Assert.Equal(CpsrElementStatus.Missing, Status(r, CpsrElement.Competition));
        Assert.Contains("52.219-8", r.Single(x => x.Element == CpsrElement.Flowdowns).Detail);
        Assert.Contains("after the award", r.Single(x => x.Element == CpsrElement.Debarment).Detail);
        Assert.Contains("Contracts director", r.Single(x => x.Element == CpsrElement.Approvals).Detail);
        Assert.Equal(33, CpsrScoring.Score(r)); // Price analysis and small business met: 2 of 6.

        // Sole source with a justification satisfies competition; a stale exclusions check doesn't count.
        partial.Competition = CompetitionMethod.SoleSource;
        partial.SoleSourceJustification = "Only the OEM can service the instrument.";
        partial.DebarmentCheckedOn = Day.AddDays(-200);
        r = CpsrScoring.Evaluate(doc, partial, null, Settings);
        Assert.Equal(CpsrElementStatus.Met, Status(r, CpsrElement.Competition));
        Assert.Contains("200 days", r.Single(x => x.Element == CpsrElement.Debarment).Detail);

        // Above the certified cost or pricing data threshold a price analysis alone isn't enough.
        var huge = CompleteFile(1, 2, 3, 4);
        huge.PriceAnalysis = PriceAnalysisKind.PriceAnalysis;
        Assert.Equal(CpsrElementStatus.Missing, Status(CpsrScoring.Evaluate(Doc(3_000_000m), huge, null, Settings), CpsrElement.PriceAnalysis));
    }

    [Fact]
    public void RFQ_evidence_counts_for_competition_when_the_file_has_no_quote_count()
    {
        var rfq = new CompetitionEvidenceDto("RFQ-0007", 5, 3, "Lowest price, technically acceptable");
        var r = CpsrScoring.Evaluate(Doc(50_000m), null, rfq, Settings);
        var competition = r.Single(x => x.Element == CpsrElement.Competition);
        Assert.Equal(CpsrElementStatus.Met, competition.Status);
        Assert.Contains("RFQ-0007", competition.Detail);
    }

    private static async Task<PurchaseOrder> OrderAsync(PurchasingKit kit, string number, decimal amount, bool government, DateOnly? date = null, PurchaseOrderStatus status = PurchaseOrderStatus.Issued)
    {
        await using var db = await kit.Factory.CreateAsync();
        var po = new PurchaseOrder { Number = number, VendorId = kit.Acme.Id, OrderDate = date ?? Day, Status = status, Total = amount, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow };
        po.Lines.Add(new PurchaseOrderLine
        {
            PurchaseOrderId = po.Id,
            LineNumber = 1,
            Description = "Test equipment",
            Quantity = 1,
            UnitPrice = amount,
            Amount = amount,
            ProjectId = government ? kit.Fx.Project1 : null,
            WbsElementId = government ? kit.Fx.Wbs1 : null,
            AccountId = government ? null : kit.Account("1510"),
            OrgUnitId = government ? null : kit.OrgId,
        });
        db.Set<PurchaseOrder>().Add(po);
        await db.SaveChangesAsync();
        return po;
    }

    [Fact]
    public async Task Files_are_saved_and_approved_and_the_readiness_dashboard_scores_a_sample()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var cpsr = kit.Get<ICpsrService>();
        var big = await OrderAsync(kit, "PO-900001", 400_000m, government: true);
        var small = await OrderAsync(kit, "PO-900002", 8_000m, government: false);
        await OrderAsync(kit, "PO-900003", 20_000m, government: true, status: PurchaseOrderStatus.Draft); // Drafts aren't reviewed.

        // An empty file is scored without being saved.
        var empty = await cpsr.GetFileAsync(ProcurementDocumentKind.PurchaseOrder, big.Id);
        Assert.Null(empty.FileId);
        Assert.True(empty.GovernmentFunded);
        Assert.Equal(0, empty.Score);

        var saved = await cpsr.SaveFileAsync(ProcurementDocumentKind.PurchaseOrder, big.Id, new SaveProcurementFileRequest(CompetitionType.Competitive, 4, 3,
            PriceAnalysis: PriceAnalysisType.PriceAnalysis, PriceAnalysisNotes: "Lowest of three quotes, within 2% of the GSA schedule price.",
            FlowdownClauses: ["52.204-21, 52.204-25", "52.203-19", "52.222-50", "52.219-8"], DebarmentCheckedOn: Day.AddDays(-3), DebarmentResult: "No exclusions"));
        Assert.NotNull(saved.FileId);
        Assert.Contains("52.204-25", saved.FlowdownClauses);
        Assert.Contains(saved.Elements, e => e.Element == CpsrElement.Flowdowns && e.Detail.Contains("52.222-35"));

        await cpsr.ApproveAsync(ProcurementDocumentKind.PurchaseOrder, big.Id, new ApproveProcurementFileRequest(1, "Buyer review"));
        var approved = await cpsr.ApproveAsync(ProcurementDocumentKind.PurchaseOrder, big.Id, new ApproveProcurementFileRequest(3));
        Assert.Equal(CpsrElementStatus.Met, approved.Elements.Single(e => e.Element == CpsrElement.Approvals).Status);
        Assert.Equal(new[] { "Buyer", "Contracts director" }, approved.Approvals.Select(a => a.LevelName).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => cpsr.ApproveAsync(ProcurementDocumentKind.PurchaseOrder, big.Id, new ApproveProcurementFileRequest(3)));
        await Assert.ThrowsAsync<ArgumentException>(() => cpsr.ApproveAsync(ProcurementDocumentKind.PurchaseOrder, big.Id, new ApproveProcurementFileRequest(9)));

        // Readiness over the kit's subcontract (SUB-1, 20,000 ceiling, starts 2026-01-01) and the two issued orders.
        var readiness = await cpsr.GetReadinessAsync(new CpsrReviewRequest(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), SampleSize: 2));
        Assert.Equal(3, readiness.Population);
        Assert.Contains(readiness.Sample, s => s.DocumentId == big.Id); // Everything above the SAT is always sampled.
        Assert.Equal(2, readiness.SampleSize);
        Assert.Equal(readiness.Missing.Count, readiness.Sample.Sum(s => s.MissingCount));
        Assert.Contains(readiness.Missing, m => m.DocumentId == big.Id && m.Element == CpsrElement.SmallBusiness);
        var all = await cpsr.GetReadinessAsync(new CpsrReviewRequest(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(3, all.SampleSize);
        Assert.Contains(all.Sample, s => s.Kind == ProcurementDocumentKind.Subcontract && s.Number == "SUB-1");
        var overall = all.Elements.Sum(e => e.Met) * 100m / all.Elements.Sum(e => e.Applicable);
        Assert.Equal((int)Math.Round(overall, MidpointRounding.AwayFromZero), all.Score);

        // The same seed gives the same sample.
        var again = await cpsr.GetReadinessAsync(new CpsrReviewRequest(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), SampleSize: 2));
        Assert.Equal(readiness.Sample.Select(s => s.DocumentId), again.Sample.Select(s => s.DocumentId));

        var export = await cpsr.ExportReviewAsync(new CpsrReviewRequest(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal("text/html", export.ContentType);
        Assert.Contains("PO-900001", export.Content);
        Assert.Contains("Contracts director", export.Content);
        Assert.Contains(small.Number, export.Content);
    }

    private sealed class FakeRfqSource(Guid poId) : IProcurementCompetitionSource
    {
        public Task<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>> GetForPurchaseOrdersAsync(IReadOnlyCollection<Guid> purchaseOrderIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>>(purchaseOrderIds.Contains(poId)
                ? new Dictionary<Guid, CompetitionEvidenceDto> { [poId] = new("RFQ-0042", 4, 2) }
                : new Dictionary<Guid, CompetitionEvidenceDto>());
    }

    [Fact]
    public async Task The_RFQ_module_feeds_competition_through_the_competition_source()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        Assert.IsType<NoCompetitionSource>(kit.Get<IProcurementCompetitionSource>());
        var po = await OrderAsync(kit, "PO-900010", 60_000m, government: true);
        var service = new CpsrService(kit.Factory, kit.Get<AuditRecorder>(), new FakeRfqSource(po.Id), Options.Create(new CpsrOptions()), TimeProvider.System);
        var file = await service.GetFileAsync(ProcurementDocumentKind.PurchaseOrder, po.Id);
        Assert.Equal("RFQ-0042", file.RfqEvidence?.Reference);
        Assert.Equal(CpsrElementStatus.Met, file.Elements.Single(e => e.Element == CpsrElement.Competition).Status);
    }

    [Fact]
    public async Task Bad_input_and_other_roles_are_refused()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var cpsr = kit.Get<ICpsrService>();
        var po = await OrderAsync(kit, "PO-900020", 60_000m, government: true);
        await Assert.ThrowsAsync<ArgumentException>(() => cpsr.SaveFileAsync(ProcurementDocumentKind.PurchaseOrder, po.Id, new SaveProcurementFileRequest(CompetitionType.Competitive, 2, 3)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => cpsr.GetFileAsync(ProcurementDocumentKind.Subcontract, po.Id));

        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => cpsr.GetReadinessAsync(new CpsrReviewRequest()));
        kit.As(Admin);
        Assert.NotNull(await cpsr.GetSettingsAsync());
    }
}
