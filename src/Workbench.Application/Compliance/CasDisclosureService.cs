using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Assets;
using Workbench.Application.IndirectRates;
using Workbench.Application.Pricing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Compliance;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Companies;
using Workbench.Domain.Compliance;
using Workbench.Domain.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Compliance;

/// <summary>
/// CAS disclosure statements: versions of the DS-1 (or DS-2) with structured answers, items linked to the live
/// configuration, effective dating, the printable export, and the CAS 401/402 consistency check.
/// </summary>
public sealed class CasDisclosureService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    IOptions<AssetOptions> assetOptions,
    TimeProvider clock) : ICasDisclosureService
{
    public const string StatementEntity = nameof(DisclosureStatement);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<IReadOnlyList<DisclosureVersionDto>> ListVersionsAsync(DisclosureFormType? form = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<DisclosureStatement>().AsNoTracking();
        if (form is { } f)
        {
            var domain = (DisclosureForm)(int)f;
            q = q.Where(s => s.Form == domain);
        }

        var statements = await q.ToListAsync(cancellationToken);
        var ids = statements.Select(s => s.Id).ToList();
        var answers = await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => ids.Contains(a.StatementId)).ToListAsync(cancellationToken);
        return statements.OrderBy(s => s.Form).ThenByDescending(s => s.Version)
            .Select(s => Version(s, answers.Where(a => a.StatementId == s.Id).ToList())).ToList();
    }

    public async Task<DisclosureStatementDto> GetAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DtoAsync(db, statementId, cancellationToken);
    }

    public async Task<DisclosureStatementDto> CreateVersionAsync(CreateDisclosureVersionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Form))
        {
            throw new ArgumentException("Unknown disclosure statement form.");
        }

        var form = (DisclosureForm)(int)request.Form;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var existing = await db.Set<DisclosureStatement>().AsNoTracking().Where(s => s.Form == form).ToListAsync(cancellationToken);
        if (existing.FirstOrDefault(s => s.Status == DisclosureStatus.Draft) is { } draft)
        {
            throw new InvalidOperationException($"Version {draft.Version} is still a draft: finish it or delete it before starting another.");
        }

        var latest = existing.OrderByDescending(s => s.Version).FirstOrDefault();
        var version = (latest?.Version ?? 0) + 1;
        var statement = new DisclosureStatement
        {
            Form = form,
            Version = version,
            Title = Purchasing.PurchasingSupport.Optional(request.Title, "Title", 200) ?? $"{FormName(form)} disclosure statement, revision {version}",
            Status = DisclosureStatus.Draft,
            PreviousVersionId = latest?.Id,
            ChangeSummary = Purchasing.PurchasingSupport.Optional(request.ChangeSummary, "Change summary", 4000),
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<DisclosureStatement>().Add(statement);
        if (latest is not null)
        {
            var answers = await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => a.StatementId == latest.Id).ToListAsync(cancellationToken);
            db.Set<DisclosureAnswer>().AddRange(answers.Select(a => new DisclosureAnswer
            {
                StatementId = statement.Id,
                ItemKey = a.ItemKey,
                Choice = a.Choice,
                Text = a.Text,
                UpdatedBy = a.UpdatedBy,
                UpdatedAtUtc = a.UpdatedAtUtc,
            }));
        }

        audit.Record(db, StatementEntity, statement.Id, "Created", after: new { Form = form.ToString(), statement.Version, CopiedFrom = latest?.Version });
        await db.SaveChangesAsync(cancellationToken);
        return await DtoAsync(db, statement.Id, cancellationToken);
    }

    public async Task<DisclosureStatementDto> SaveAnswersAsync(Guid statementId, IReadOnlyList<SaveDisclosureAnswerRequest> answers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var statement = await DraftAsync(db, statementId, cancellationToken);
        var items = DisclosureItems.For((DisclosureFormType)(int)statement.Form).ToDictionary(i => i.Key, StringComparer.Ordinal);
        var existing = await db.Set<DisclosureAnswer>().Where(a => a.StatementId == statementId).ToDictionaryAsync(a => a.ItemKey, StringComparer.Ordinal, cancellationToken);
        var changed = new List<string>();
        foreach (var request in answers)
        {
            var item = items.GetValueOrDefault(request.ItemKey ?? "") ?? throw new ArgumentException($"{FormName(statement.Form)} has no item {request.ItemKey}.");
            var choice = string.IsNullOrWhiteSpace(request.Choice) ? null : request.Choice.Trim();
            var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();
            if (choice is not null)
            {
                if (item.IsAmount)
                {
                    choice = decimal.TryParse(choice, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount >= 0
                        ? amount.ToString("0.##", CultureInfo.InvariantCulture)
                        : throw new ArgumentException($"Item {item.Key} takes an amount.");
                }
                else if (!item.Choices.Any(c => c.Code == choice))
                {
                    throw new ArgumentException($"Item {item.Key} has no choice {choice}.");
                }
            }

            if (text?.Length > 8000)
            {
                throw new ArgumentException($"The answer to item {item.Key} is longer than 8,000 characters.");
            }

            existing.TryGetValue(item.Key, out var answer);
            if (choice is null && text is null)
            {
                if (answer is not null)
                {
                    db.Set<DisclosureAnswer>().Remove(answer);
                    existing.Remove(item.Key);
                    changed.Add(item.Key);
                }

                continue;
            }

            if (answer is null)
            {
                answer = new DisclosureAnswer { StatementId = statementId, ItemKey = item.Key, UpdatedBy = audit.Actor };
                db.Set<DisclosureAnswer>().Add(answer);
                existing[item.Key] = answer;
            }
            else if (answer.Choice == choice && answer.Text == text)
            {
                continue;
            }

            answer.Choice = choice;
            answer.Text = text;
            answer.UpdatedBy = audit.Actor;
            answer.UpdatedAtUtc = UtcNow;
            changed.Add(item.Key);
        }

        if (changed.Count > 0)
        {
            audit.Record(db, StatementEntity, statementId, "Answered", after: new { statement.Version, Items = changed });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await DtoAsync(db, statementId, cancellationToken);
    }

    public async Task<DisclosureStatementDto> FillFromConfigurationAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var statement = await DraftAsync(db, statementId, cancellationToken);
        var config = await ConfigurationAsync(db, cancellationToken);
        var answered = (await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => a.StatementId == statementId && a.Choice != null).Select(a => a.ItemKey).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var fills = DisclosureItems.For((DisclosureFormType)(int)statement.Form)
            .Where(i => i.Link != DisclosureLink.None && !answered.Contains(i.Key) && config.Choice(i.Link) is not null)
            .Select(i => new SaveDisclosureAnswerRequest(i.Key, config.Choice(i.Link), null)).ToList();
        if (fills.Count == 0)
        {
            return await DtoAsync(db, statementId, cancellationToken);
        }

        // Keep any narrative already written for the item.
        var texts = await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => a.StatementId == statementId).ToDictionaryAsync(a => a.ItemKey, a => a.Text, StringComparer.Ordinal, cancellationToken);
        await db.DisposeAsync();
        return await SaveAnswersAsync(statementId, fills.Select(f => f with { Text = texts.GetValueOrDefault(f.ItemKey) }).ToList(), cancellationToken);
    }

    public async Task<DisclosureStatementDto> MakeEffectiveAsync(Guid statementId, MakeDisclosureEffectiveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var statement = await DraftAsync(db, statementId, cancellationToken);
        var items = DisclosureItems.For((DisclosureFormType)(int)statement.Form);
        var answers = await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => a.StatementId == statementId).ToListAsync(cancellationToken);
        var open = items.Where(i => !IsAnswered(i, answers.FirstOrDefault(a => a.ItemKey == i.Key))).Select(i => i.Key).ToList();
        if (open.Count > 0)
        {
            throw new ArgumentException($"{open.Count} item{(open.Count == 1 ? " is" : "s are")} still unanswered ({string.Join(", ", open.Take(6))}{(open.Count > 6 ? ", …" : "")}).");
        }

        var current = await db.Set<DisclosureStatement>().FirstOrDefaultAsync(s => s.Form == statement.Form && s.Status == DisclosureStatus.Effective, cancellationToken);
        if (current is not null)
        {
            if (request.EffectiveFrom <= current.EffectiveFrom)
            {
                throw new ArgumentException($"Version {current.Version} is in force from {current.EffectiveFrom:yyyy-MM-dd}; the new version must start after that.");
            }

            current.Status = DisclosureStatus.Superseded;
            current.EffectiveTo = request.EffectiveFrom.AddDays(-1);
            audit.Record(db, StatementEntity, current.Id, "Superseded", after: new { current.Version, current.EffectiveTo, By = statement.Version });
        }

        statement.Status = DisclosureStatus.Effective;
        statement.EffectiveFrom = request.EffectiveFrom;
        statement.ChangeSummary = Purchasing.PurchasingSupport.Optional(request.ChangeSummary, "Change summary", 4000) ?? statement.ChangeSummary;
        statement.MadeEffectiveBy = audit.Actor;
        statement.MadeEffectiveAtUtc = UtcNow;
        audit.Record(db, StatementEntity, statement.Id, "MadeEffective", after: new { statement.Version, statement.EffectiveFrom, statement.ChangeSummary });
        await db.SaveChangesAsync(cancellationToken);
        return await DtoAsync(db, statementId, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var statement = await DraftAsync(db, statementId, cancellationToken);
        db.Set<DisclosureAnswer>().RemoveRange(await db.Set<DisclosureAnswer>().Where(a => a.StatementId == statementId).ToListAsync(cancellationToken));
        db.Set<DisclosureStatement>().Remove(statement);
        audit.Record(db, StatementEntity, statement.Id, "Deleted", before: new { statement.Version, statement.Title });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ComplianceExportFile> ExportAsync(Guid statementId, CancellationToken cancellationToken = default)
    {
        var s = await GetAsync(statementId, cancellationToken);
        var v = s.Version;
        var form = v.Form == DisclosureFormType.Ds2 ? "DS-2" : "DS-1";
        var html = new StringBuilder();
        ComplianceHtml.Begin(html, v.Title);
        html.Append("<p class=\"muted\">").Append(Enc($"{form}, revision {v.Version}. Status: {v.Status}"
            + (v.EffectiveFrom is { } from ? $", in force from {from:yyyy-MM-dd}" : "") + (v.EffectiveTo is { } to ? $" to {to:yyyy-MM-dd}" : "")
            + $". {v.Answered} of {v.Total} items answered. Exported {UtcNow:yyyy-MM-dd} by {audit.Actor}.")).Append("</p>");
        if (v.ChangeSummary is { } change)
        {
            html.Append("<p><strong>Changes in this revision:</strong> ").Append(Enc(change)).Append("</p>");
        }

        foreach (var part in s.Parts)
        {
            html.Append($"<h2>Part {Enc(part.Roman)}: {Enc(part.Title)}</h2><table><thead><tr><th style=\"width:6rem\">Item</th><th>Question</th><th>Answer</th></tr></thead><tbody>");
            foreach (var item in part.Items)
            {
                var answer = new StringBuilder();
                if (item.Choice is { } choice)
                {
                    answer.Append("<strong>").Append(Enc(item.Choices.FirstOrDefault(c => c.Code == choice)?.Label ?? choice)).Append("</strong>");
                }

                if (item.Text is { } text)
                {
                    answer.Append(answer.Length > 0 ? "<br>" : "").Append(Enc(text).Replace("\n", "<br>", StringComparison.Ordinal));
                }

                if (!item.Answered)
                {
                    answer.Append("<em>Not answered</em>");
                }

                html.Append($"<tr><td>{Enc(item.Key)}</td><td>{Enc(item.Question)}</td><td>{answer}</td></tr>");
            }

            html.Append("</tbody></table>");
        }

        ComplianceHtml.End(html);
        return new ComplianceExportFile($"{form.ToLowerInvariant()}-revision-{v.Version}.html", "text/html", html.ToString());
    }

    // ---- CAS 401/402 ----

    public async Task<CasConsistencyDto> CheckConsistencyAsync(Guid? statementId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var config = await ConfigurationAsync(db, cancellationToken);
        var findings = new List<CasConsistencyFindingDto>();

        // The disclosed practice: the given statement, else the DS-1 in force, else the latest DS-1 draft.
        var statement = statementId is { } id
            ? await db.Set<DisclosureStatement>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Disclosure statement", id)
            : await db.Set<DisclosureStatement>().AsNoTracking().Where(s => s.Form == DisclosureForm.Ds1 && s.Status != DisclosureStatus.Superseded)
                .OrderBy(s => s.Status == DisclosureStatus.Effective ? 0 : 1).ThenByDescending(s => s.Version).FirstOrDefaultAsync(cancellationToken);
        if (statement is null)
        {
            findings.Add(new("CAS 401", ConsistencySeverity.Warning, "Disclosure statement", "There's no DS-1 yet, so estimating and accounting practices aren't disclosed.",
                "Not disclosed", "Configured in Workbench", "admin/cas-disclosure"));
        }
        else
        {
            var answers = await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => a.StatementId == statement.Id).ToDictionaryAsync(a => a.ItemKey, StringComparer.Ordinal, cancellationToken);
            foreach (var item in DisclosureItems.For((DisclosureFormType)(int)statement.Form).Where(i => i.Link != DisclosureLink.None))
            {
                var configured = config.Choice(item.Link);
                var answer = answers.GetValueOrDefault(item.Key)?.Choice;
                var standard = item.Link switch
                {
                    DisclosureLink.UnallowableCosts => "CAS 405",
                    DisclosureLink.DepreciationMethods => "CAS 409",
                    DisclosureLink.CapitalizationThreshold => "CAS 404",
                    DisclosureLink.GaBase => "CAS 410",
                    _ => "CAS 401",
                };
                if (answer is null)
                {
                    findings.Add(new(standard, ConsistencySeverity.Info, DisclosureItems.LinkLabel(item.Link), $"Item {item.Key} isn't answered in revision {statement.Version}.",
                        "Not disclosed", config.Text(item.Link) ?? "Not set up", "admin/cas-disclosure"));
                }
                else if (configured is not null && !Matches(item, answer, configured))
                {
                    findings.Add(new(standard, ConsistencySeverity.Problem, DisclosureItems.LinkLabel(item.Link),
                        $"Item {item.Key} discloses a practice the books don't follow: disclosed and actual practice must agree.",
                        $"Disclosed: {Label(item, answer)}", $"Actual: {config.Text(item.Link)}", "admin/cas-disclosure"));
                }
            }
        }

        await PricingFindingsAsync(db, config, findings, cancellationToken);
        if (findings.Count == 0)
        {
            findings.Add(new("CAS 401/402", ConsistencySeverity.Info, "Overall", "No differences found between disclosed, estimating and accounting practices.", "", ""));
        }

        return new CasConsistencyDto(UtcNow, statement?.Id, statement?.Version,
            findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Standard, StringComparer.Ordinal).ThenBy(f => f.Area, StringComparer.Ordinal).ToList());
    }

    /// <summary>Estimating (proposal pricing) against accounting: pools, bases, rate sources, labor and unallowables.</summary>
    private async Task PricingFindingsAsync(IWorkbenchDbContext db, Configuration config, List<CasConsistencyFindingDto> findings, CancellationToken ct)
    {
        var fiscalYear = await db.Set<FiscalYear>().AsNoTracking().Where(y => y.Start <= Today && y.End >= Today).Select(y => (int?)y.Year).FirstOrDefaultAsync(ct);
        var forward = await db.Set<IndirectRate>().AsNoTracking()
            .Where(r => r.Basis == IndirectRateBasis.ForwardPricing && r.SupersededAtUtc == null && (fiscalYear == null || r.FiscalYear >= fiscalYear))
            .Select(r => r.Pool).Distinct().ToListAsync(ct);
        foreach (var pool in forward.Where(p => !config.Bases.ContainsKey(p)))
        {
            findings.Add(new("CAS 401", ConsistencySeverity.Problem, $"{RateBook.Label(pool)} pool",
                $"Proposals can be burdened with a {RateBook.Label(pool)} forward pricing rate, but accounting has no active {RateBook.Label(pool)} pool to accumulate actual costs in.",
                $"Forward pricing rate for {RateBook.Label(pool)}", "No active pool", "accounting/indirect-rates"));
        }

        foreach (var pool in config.Bases.Keys.Where(p => !forward.Contains(p)))
        {
            findings.Add(new("CAS 401", ConsistencySeverity.Warning, $"{RateBook.Label(pool)} pool",
                $"Accounting allocates {RateBook.Label(pool)}, but there's no current forward pricing rate for it, so proposals can't estimate it the same way.",
                "No forward pricing rate", $"{RateBook.Label(pool)} on {PriceCalculator.BaseLabel(config.Bases[pool])}", "accounting/indirect-rates"));
        }

        var open = await db.Set<Proposal>().AsNoTracking().Where(p => p.Status == ProposalStatus.Draft || p.Status == ProposalStatus.Submitted).ToListAsync(ct);
        var proposalIds = open.Select(p => p.Id).ToList();
        var models = await db.Set<PriceModel>().AsNoTracking().Where(m => proposalIds.Contains(m.ProposalId) && (m.IsBase || m.IsLocked)).ToListAsync(ct);
        foreach (var proposal in open.OrderBy(p => p.Number, StringComparer.Ordinal))
        {
            var link = $"pricing/{proposal.Id}";
            if (models.FirstOrDefault(m => m.ProposalId == proposal.Id && m.IsBase) is { } working)
            {
                var overrides = new[] { (IndirectPool.Fringe, working.FringeOverride), (IndirectPool.Overhead, working.OverheadOverride), (IndirectPool.GeneralAndAdministrative, working.GaOverride) }
                    .Where(o => o.Item2 is not null).Select(o => $"{RateBook.Label(o.Item1)} {o.Item2!.Value * 100:0.##}%").ToList();
                if (overrides.Count > 0)
                {
                    findings.Add(new("CAS 401", ConsistencySeverity.Warning, $"Proposal {proposal.Number}",
                        "The working price overrides the indirect rates on the books; document why the estimate departs from the accounting rates.",
                        $"Override: {string.Join(", ", overrides)}", "Rates on the books", link));
                }

                if (working.RateBasis != IndirectRateBasis.ForwardPricing)
                {
                    findings.Add(new("CAS 401", ConsistencySeverity.Warning, $"Proposal {proposal.Number}",
                        "The working price uses rates other than the forward pricing rates.", $"{working.RateBasis} rates", "Forward pricing rates", link));
                }
            }

            // Submitted versions keep the cascade they were priced with; compare it with the pools' current bases.
            foreach (var locked in models.Where(m => m.ProposalId == proposal.Id && m.IsLocked && m.LockedSnapshot is not null))
            {
                var cascade = Cascade(locked.LockedSnapshot!);
                foreach (var step in cascade)
                {
                    var pool = config.Bases.Keys.FirstOrDefault(p => RateBook.Label(p) == step.Pool);
                    if (config.Bases.TryGetValue(pool, out var actual) && RateBook.Label(pool) == step.Pool && PriceCalculator.BaseLabel(actual) != step.Base)
                    {
                        findings.Add(new("CAS 401", ConsistencySeverity.Problem, $"Proposal {proposal.Number} v{locked.Version}",
                            $"Priced {step.Pool} on a different base than accounting allocates it on.", $"{step.Pool} on {step.Base}", $"{step.Pool} on {PriceCalculator.BaseLabel(actual)}", link));
                    }
                }
            }
        }

        if (open.Count > 0 && config.LaborMethod != LaborCostMethod.Standard)
        {
            findings.Add(new("CAS 401", ConsistencySeverity.Warning, "Labor cost method",
                "Proposals price labor at each employee's hourly cost rate, while accounting costs exempt staff with uncompensated overtime by the effective rate. Disclose how estimates treat uncompensated overtime, or price at the effective rate.",
                "Hourly cost rates (no uncompensated overtime)", Configuration.LaborText(config.LaborMethod), "accounting/total-time"));
        }

        if (!config.UnallowableSegregated)
        {
            findings.Add(new("CAS 405", ConsistencySeverity.Problem, "Unallowable costs",
                "There's no active unallowable cost account, so unallowable costs can't be kept out of the pools that proposals and claims are built from.",
                "Proposals exclude unallowable costs", "No unallowable account", "accounting/accounts"));
        }
    }

    private static IReadOnlyList<CascadeStepDto> Cascade(string snapshot)
    {
        try
        {
            return JsonSerializer.Deserialize<PriceResultDto>(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Cascade ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ---- Configuration ----

    /// <summary>The live configuration the linked items describe.</summary>
    private sealed record Configuration(
        IReadOnlyDictionary<IndirectPool, AllocationBase> Bases,
        LaborCostMethod LaborMethod,
        bool UnallowableSegregated,
        decimal CapitalizationThreshold,
        IReadOnlyList<DepreciationMethod> DepreciationMethods)
    {
        public string? Choice(DisclosureLink link) => link switch
        {
            DisclosureLink.LaborChargingMethod => "IndividualActual",
            DisclosureLink.UncompensatedOvertime => LaborMethod.ToString(),
            DisclosureLink.FringeBase => Base(IndirectPool.Fringe),
            DisclosureLink.OverheadBase => Base(IndirectPool.Overhead),
            DisclosureLink.GaBase => Base(IndirectPool.GeneralAndAdministrative),
            DisclosureLink.DepreciationMethods => DepreciationMethods.Count switch { 0 => null, 1 => DepreciationMethods[0].ToString(), _ => "Mixed" },
            DisclosureLink.CapitalizationThreshold => CapitalizationThreshold.ToString("0.##", CultureInfo.InvariantCulture),
            DisclosureLink.UnallowableCosts => UnallowableSegregated ? "SeparateAccounts" : "NotSegregated",
            _ => null,
        };

        public string? Text(DisclosureLink link) => link switch
        {
            DisclosureLink.LaborChargingMethod => "Each employee's own cost rate on the work date",
            DisclosureLink.UncompensatedOvertime => LaborText(LaborMethod),
            DisclosureLink.FringeBase or DisclosureLink.OverheadBase or DisclosureLink.GaBase => Choice(link) is { } b && Enum.TryParse<AllocationBase>(b, out var basis)
                ? PriceCalculator.BaseLabel(basis) : "No active pool",
            DisclosureLink.DepreciationMethods => DepreciationMethods.Count == 0 ? null : string.Join(", ", DepreciationMethods),
            DisclosureLink.CapitalizationThreshold => CapitalizationThreshold.ToString("N2", CultureInfo.InvariantCulture),
            DisclosureLink.UnallowableCosts => UnallowableSegregated ? "Separate unallowable accounts in the chart" : "No unallowable accounts",
            _ => null,
        };

        private string Base(IndirectPool pool) => Bases.TryGetValue(pool, out var b) ? b.ToString() : "NotApplicable";

        public static string LaborText(LaborCostMethod method) => method switch
        {
            LaborCostMethod.TotalTimeEffectiveRate => "Total time accounting (effective rate)",
            LaborCostMethod.Hybrid => "Effective rate in periods with uncompensated hours",
            _ => "Standard (uncompensated hours cost nothing)",
        };
    }

    private async Task<Configuration> ConfigurationAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var setups = await db.Set<IndirectPoolSetup>().AsNoTracking().Where(s => s.IsActive && s.CompanyId == Company.DefaultId).ToListAsync(ct);
        var bases = setups.GroupBy(s => s.Pool).ToDictionary(g => g.Key, g => g.OrderBy(s => s.CascadeOrder).First().Base);
        var policies = await db.Set<LaborCostPolicy>().AsNoTracking().Where(p => p.EffectiveFrom <= Today).ToListAsync(ct);
        var method = policies.OrderByDescending(p => p.EffectiveFrom).ThenByDescending(p => p.CreatedAtUtc).FirstOrDefault()?.Method ?? LaborCostMethod.Standard;
        var unallowable = await db.Set<Account>().AsNoTracking().AnyAsync(a => a.IsActive && a.CostClass == CostClassification.Unallowable, ct);
        var methods = (await db.Set<AssetCategory>().AsNoTracking().Where(c => c.IsActive).Select(c => c.DefaultMethod).ToListAsync(ct)).Distinct().Order().ToList();
        return new Configuration(bases, method, unallowable, assetOptions.Value.CapitalizationThreshold, methods);
    }

    // ---- Helpers ----

    private static bool Matches(DisclosureItem item, string answer, string configured) =>
        item.IsAmount
            ? decimal.TryParse(answer, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(configured, NumberStyles.Number, CultureInfo.InvariantCulture, out var c) && a == c
            : string.Equals(answer, configured, StringComparison.Ordinal);

    private static string Label(DisclosureItem item, string choice) =>
        item.IsAmount ? choice : item.Choices.FirstOrDefault(c => c.Code == choice)?.Label ?? choice;

    /// <summary>Items with choices need a choice; narrative items need text.</summary>
    private static bool IsAnswered(DisclosureItem item, DisclosureAnswer? answer) =>
        answer is not null && (item.HasChoices ? answer.Choice is not null : answer.Text is not null);

    private static string FormName(DisclosureForm form) => form == DisclosureForm.Ds2 ? "DS-2" : "DS-1";

    private static string Enc(string text) => WebUtility.HtmlEncode(text);

    private static async Task<DisclosureStatement> DraftAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var statement = await db.Set<DisclosureStatement>().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Disclosure statement", id);
        if (statement.Status != DisclosureStatus.Draft)
        {
            throw new InvalidOperationException($"Revision {statement.Version} is {statement.Status.ToString().ToLowerInvariant()} and can't change; start a new revision.");
        }

        return statement;
    }

    private static DisclosureVersionDto Version(DisclosureStatement s, IReadOnlyList<DisclosureAnswer> answers)
    {
        var items = DisclosureItems.For((DisclosureFormType)(int)s.Form);
        var answered = items.Count(i => IsAnswered(i, answers.FirstOrDefault(a => a.ItemKey == i.Key)));
        return new DisclosureVersionDto(s.Id, (DisclosureFormType)(int)s.Form, s.Version, s.Title, (DisclosureState)(int)s.Status, s.EffectiveFrom, s.EffectiveTo, s.ChangeSummary,
            answered, items.Count, s.CreatedBy, s.CreatedAtUtc, s.MadeEffectiveBy, s.MadeEffectiveAtUtc);
    }

    private async Task<DisclosureStatementDto> DtoAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var statement = await db.Set<DisclosureStatement>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Disclosure statement", id);
        var answers = await db.Set<DisclosureAnswer>().AsNoTracking().Where(a => a.StatementId == id).ToListAsync(ct);
        var config = await ConfigurationAsync(db, ct);
        var form = (DisclosureFormType)(int)statement.Form;
        var byKey = answers.ToDictionary(a => a.ItemKey, StringComparer.Ordinal);
        var parts = DisclosureItems.PartsFor(form).Select(p => new DisclosurePartDto(p.Number, p.Roman, p.Title,
            DisclosureItems.For(form).Where(i => i.Part == p.Number).Select(i =>
            {
                var answer = byKey.GetValueOrDefault(i.Key);
                var linked = i.Link != DisclosureLink.None;
                var configured = linked ? config.Choice(i.Link) : null;
                bool? matches = linked && answer?.Choice is { } choice && configured is not null ? Matches(i, choice, configured) : null;
                return new DisclosureItemDto(i.Key, i.Part, i.Question, i.Choices, linked ? DisclosureItems.LinkLabel(i.Link) : null, configured, linked ? config.Text(i.Link) : null,
                    answer?.Choice, answer?.Text, IsAnswered(i, answer), matches, answer?.UpdatedBy, answer?.UpdatedAtUtc);
            }).ToList())).ToList();
        return new DisclosureStatementDto(Version(statement, answers), parts);
    }
}
