using Workbench.Application.Compliance;
using Workbench.Contracts;
using Workbench.Contracts.Compliance;
using Workbench.Domain.Accounting;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Compliance;

/// <summary>DS-1 versions, answers linked to the configuration, effective dating, the export and the CAS 401/402 check, as finance@.</summary>
public sealed class CasDisclosureTests
{
    private static ICasDisclosureService Cas(PurchasingKit kit) => kit.Get<ICasDisclosureService>();

    /// <summary>Fringe on direct labor and overhead on direct labor + fringe, the Hybrid labor method, and forward pricing rates for both.</summary>
    private static async Task ConfigureAsync(PurchasingKit kit)
    {
        await using var db = await kit.Factory.CreateAsync();
        db.Set<IndirectPoolSetup>().AddRange(
            new IndirectPoolSetup { Pool = IndirectPool.Fringe, Name = "Fringe", Base = AllocationBase.DirectLabor, CascadeOrder = 1, AppliedAccountId = kit.Account("6140"), AbsorbedAccountId = kit.Account("6140") },
            new IndirectPoolSetup { Pool = IndirectPool.Overhead, Name = "Overhead", Base = AllocationBase.DirectLaborPlusFringe, CascadeOrder = 2, AppliedAccountId = kit.Account("6140"), AbsorbedAccountId = kit.Account("6140") });
        db.Set<LaborCostPolicy>().Add(new LaborCostPolicy { EffectiveFrom = new DateOnly(2026, 1, 1), Method = LaborCostMethod.Hybrid, Reason = "TTA", CreatedBy = Finance });
        foreach (var pool in new[] { IndirectPool.Fringe, IndirectPool.Overhead })
        {
            db.Set<IndirectRate>().Add(new IndirectRate { Pool = pool, FiscalYear = 2030, Basis = IndirectRateBasis.ForwardPricing, Rate = 0.3m, EffectiveFrom = new DateOnly(2026, 1, 1), CreatedBy = Finance });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Answers every unanswered item: linked items from the configuration, choices with the first choice, the rest with text.</summary>
    private static async Task<DisclosureStatementDto> CompleteAsync(PurchasingKit kit, Guid id)
    {
        var s = await Cas(kit).FillFromConfigurationAsync(id);
        var rest = s.Parts.SelectMany(p => p.Items).Where(i => !i.Answered)
            .Select(i => new SaveDisclosureAnswerRequest(i.Key, i.Choices.Count > 0 ? i.Choices[0].Code : null, i.Choices.Count > 0 ? null : $"Practice for {i.Key}."))
            .ToList();
        return await Cas(kit).SaveAnswersAsync(id, rest);
    }

    [Fact]
    public async Task The_DS1_has_eight_parts_with_items_linked_to_the_configuration()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await ConfigureAsync(kit);
        var s = await Cas(kit).CreateVersionAsync(new CreateDisclosureVersionRequest());
        Assert.Equal((1, DisclosureState.Draft), (s.Version.Version, s.Version.Status));
        Assert.Equal(["I", "II", "III", "IV", "V", "VI", "VII", "VIII"], s.Parts.Select(p => p.Roman).ToArray());
        Assert.Equal(DisclosureItems.Ds1.Count, s.Version.Total);

        var overhead = s.Parts.SelectMany(p => p.Items).Single(i => i.Key == "4.1.0(b)");
        Assert.Equal(("DirectLaborPlusFringe", "Direct labor + fringe", null as bool?), (overhead.ConfiguredChoice, overhead.ConfiguredText, overhead.MatchesConfiguration));
        Assert.Equal("Hybrid", s.Parts.SelectMany(p => p.Items).Single(i => i.Key == "3.2.0(a)").ConfiguredChoice);
        Assert.Equal("5000", s.Parts.SelectMany(p => p.Items).Single(i => i.Key == "5.5.0(a)").ConfiguredChoice);

        // Answers are validated against the item; a wrong overhead base shows as not matching.
        await Assert.ThrowsAsync<ArgumentException>(() => Cas(kit).SaveAnswersAsync(s.Version.Id, [new("4.1.0(b)", "Bogus", null)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Cas(kit).SaveAnswersAsync(s.Version.Id, [new("9.9.9", null, "x")]));
        await Assert.ThrowsAsync<ArgumentException>(() => Cas(kit).SaveAnswersAsync(s.Version.Id, [new("5.5.0(a)", "five", null)]));
        s = await Cas(kit).SaveAnswersAsync(s.Version.Id, [new("4.1.0(b)", "DirectLabor", "Overhead on direct labor dollars."), new("5.5.0(a)", "5,000.00", null)]);
        var items = s.Parts.SelectMany(p => p.Items).ToDictionary(i => i.Key);
        Assert.False(items["4.1.0(b)"].MatchesConfiguration);
        Assert.True(items["5.5.0(a)"].MatchesConfiguration);
        Assert.Equal("5000", items["5.5.0(a)"].Choice);

        // Filling from the configuration leaves answered items alone and keeps narrative text.
        s = await Cas(kit).FillFromConfigurationAsync(s.Version.Id);
        items = s.Parts.SelectMany(p => p.Items).ToDictionary(i => i.Key);
        Assert.Equal("DirectLabor", items["4.1.0(b)"].Choice);
        Assert.Equal("DirectLabor", items["4.1.0(a)"].Choice);
        Assert.Equal("SeparateAccounts", items["6.3.0"].Choice); // The default chart has unallowable accounts.
        Assert.True(items["4.1.0(a)"].MatchesConfiguration);

        var export = await Cas(kit).ExportAsync(s.Version.Id);
        Assert.Equal("ds-1-revision-1.html", export.FileName);
        Assert.Contains("Part IV: Indirect costs", export.Content);
        Assert.Contains("Overhead on direct labor dollars.", export.Content);
    }

    [Fact]
    public async Task Versions_copy_answers_and_making_one_effective_supersedes_the_one_in_force()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var v1 = await Cas(kit).CreateVersionAsync(new CreateDisclosureVersionRequest());
        await Cas(kit).SaveAnswersAsync(v1.Version.Id, [new("1.1.0", "Corporation", null)]);

        // Incomplete statements can't take effect; only one draft at a time.
        await Assert.ThrowsAsync<ArgumentException>(() => Cas(kit).MakeEffectiveAsync(v1.Version.Id, new MakeDisclosureEffectiveRequest(new DateOnly(2026, 1, 1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cas(kit).CreateVersionAsync(new CreateDisclosureVersionRequest()));

        await CompleteAsync(kit, v1.Version.Id);
        var effective = await Cas(kit).MakeEffectiveAsync(v1.Version.Id, new MakeDisclosureEffectiveRequest(new DateOnly(2026, 1, 1), "Initial submission"));
        Assert.Equal((DisclosureState.Effective, new DateOnly(2026, 1, 1)), (effective.Version.Status, effective.Version.EffectiveFrom!.Value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cas(kit).SaveAnswersAsync(v1.Version.Id, [new("1.1.0", "Partnership", null)]));

        var v2 = await Cas(kit).CreateVersionAsync(new CreateDisclosureVersionRequest(ChangeSummary: "Overhead base change"));
        Assert.Equal((2, v1.Version.Total), (v2.Version.Version, v2.Version.Answered)); // Every answer carried over.
        await Cas(kit).SaveAnswersAsync(v2.Version.Id, [new("1.1.0", "Partnership", null)]);
        await Assert.ThrowsAsync<ArgumentException>(() => Cas(kit).MakeEffectiveAsync(v2.Version.Id, new MakeDisclosureEffectiveRequest(new DateOnly(2025, 12, 1))));
        await Cas(kit).MakeEffectiveAsync(v2.Version.Id, new MakeDisclosureEffectiveRequest(new DateOnly(2027, 1, 1)));

        var versions = await Cas(kit).ListVersionsAsync(DisclosureFormType.Ds1);
        Assert.Equal([2, 1], versions.Select(v => v.Version).ToArray());
        Assert.Equal((DisclosureState.Superseded, new DateOnly(2026, 12, 31)), (versions[1].Status, versions[1].EffectiveTo!.Value));
        Assert.Equal("Corporation", (await Cas(kit).GetAsync(v1.Version.Id)).Parts[0].Items[0].Choice); // History stays.
        Assert.Equal("Partnership", (await Cas(kit).GetAsync(v2.Version.Id)).Parts[0].Items[0].Choice);

        // A draft can be deleted; an effective version can't.
        var v3 = await Cas(kit).CreateVersionAsync(new CreateDisclosureVersionRequest());
        await Cas(kit).DeleteDraftAsync(v3.Version.Id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Cas(kit).GetAsync(v3.Version.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cas(kit).DeleteDraftAsync(v2.Version.Id));
    }

    [Fact]
    public async Task The_consistency_check_flags_estimating_and_disclosed_practices_that_differ_from_the_books()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await ConfigureAsync(kit);

        var none = await Cas(kit).CheckConsistencyAsync();
        Assert.Contains(none.Findings, f => f.Area == "Disclosure statement" && f.Severity == ConsistencySeverity.Warning);

        var s = await Cas(kit).CreateVersionAsync(new CreateDisclosureVersionRequest());
        await Cas(kit).SaveAnswersAsync(s.Version.Id, [new("4.1.0(b)", "DirectLabor", null), new("3.2.0(a)", "Standard", null)]);

        // A submitted proposal priced overhead on direct labor; the working price of another overrides the G&A-less books.
        await using (var db = await kit.Factory.CreateAsync())
        {
            var submitted = new Proposal { Number = "PRP-1", Title = "Submitted bid", Status = ProposalStatus.Submitted, CreatedBy = Finance };
            var draft = new Proposal { Number = "PRP-2", Title = "Working bid", CreatedBy = Finance };
            db.Set<Proposal>().AddRange(submitted, draft);
            db.Set<PriceModel>().AddRange(
                new PriceModel { ProposalId = submitted.Id, Name = "v1", IsLocked = true, Version = 1, CreatedBy = Finance,
                    LockedSnapshot = """{"cascade":[{"pool":"Fringe","base":"Direct labor"},{"pool":"Overhead","base":"Direct labor"}]}""" },
                new PriceModel { ProposalId = draft.Id, Name = "Working", IsBase = true, OverheadOverride = 0.25m, RateBasis = IndirectRateBasis.Provisional, CreatedBy = Finance });
            db.Set<IndirectRate>().Add(new IndirectRate { Pool = IndirectPool.GeneralAndAdministrative, FiscalYear = 2030, Basis = IndirectRateBasis.ForwardPricing, Rate = 0.1m, EffectiveFrom = new DateOnly(2026, 1, 1), CreatedBy = Finance });
            await db.SaveChangesAsync();
        }

        var result = await Cas(kit).CheckConsistencyAsync();
        Assert.Equal(s.Version.Id, result.StatementId);
        var f = result.Findings;

        // Disclosed vs actual.
        Assert.Contains(f, x => x.Severity == ConsistencySeverity.Problem && x.Area.StartsWith("Overhead pool base", StringComparison.Ordinal) && x.Accounting.Contains("Direct labor + fringe"));
        Assert.Contains(f, x => x.Severity == ConsistencySeverity.Problem && x.Area.StartsWith("Labor cost method", StringComparison.Ordinal));

        // Estimating vs accounting: the submitted price's overhead base, the override, provisional rates, a G&A rate with no G&A pool, TTA labor.
        Assert.Contains(f, x => x.Severity == ConsistencySeverity.Problem && x.Area == "Proposal PRP-1 v1" && x.Estimating == "Overhead on Direct labor" && x.Accounting == "Overhead on Direct labor + fringe");
        Assert.DoesNotContain(f, x => x.Area == "Proposal PRP-1 v1" && x.Estimating.StartsWith("Fringe", StringComparison.Ordinal));
        Assert.Contains(f, x => x.Area == "Proposal PRP-2" && x.Estimating.Contains("Overhead 25%"));
        Assert.Contains(f, x => x.Area == "Proposal PRP-2" && x.Estimating == "Provisional rates");
        Assert.Contains(f, x => x.Severity == ConsistencySeverity.Problem && x.Area == "G&A pool");
        Assert.Contains(f, x => x.Area == "Labor cost method" && x.Severity == ConsistencySeverity.Warning);
        Assert.True(result.Problems >= 4);
        Assert.Equal(ConsistencySeverity.Problem, f[0].Severity); // Problems first.
    }

    [Fact]
    public async Task Disclosure_statements_are_Finance_or_Admin_only()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        kit.As(Editor);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Cas(kit).ListVersionsAsync());
        kit.As(Admin);
        Assert.Empty(await Cas(kit).ListVersionsAsync());
    }
}
