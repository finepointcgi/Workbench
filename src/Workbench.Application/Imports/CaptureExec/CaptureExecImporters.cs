using Microsoft.EntityFrameworkCore;
using Workbench.Application.Crm;
using Workbench.Application.Imports.Costpoint;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using D = Workbench.Domain.Crm;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports.CaptureExec;

/// <summary>
/// Capture Exec exports (Excel or CSV, from any list view or report): companies → accounts, contacts, and opportunities.
/// Column names vary with each customer's Capture Exec setup, so every field has the common spellings as aliases and the
/// mapping screen covers the rest. Re-importing the same export updates what changed (opportunities match on Capture Exec's
/// id, then the solicitation number; companies on UEI, then name; contacts on email, then company and name), so a
/// recurring export keeps Workbench in step during a cut-over.
/// </summary>
public abstract class CaptureExecImporter : CostpointImporter
{
    public override ImportSource Source => ImportSource.CaptureExec;

    protected static string Reason(ImportContext context, string what) => $"Imported {what} from Capture Exec (import job {context.Job.Id.ToString()[..8]})";

    /// <summary>Lower-cased, letters and digits only: how names are matched.</summary>
    internal static string Key(string? text) => FieldMatcher.Normalize(text);
}

/// <summary>Capture Exec companies (customers, teaming partners, competitors) → accounts.</summary>
public sealed class CaptureExecAccountImporter : CaptureExecImporter
{
    public override ImportKind Kind => ImportKind.CrmAccounts;
    public override string Title => "Companies (accounts)";
    public override string Description => "Government customers, primes, teaming partners and competitors. Import these first: contacts and opportunities refer to them by name.";
    public override string? SampleFile => "samples/import/captureexec/companies.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Name", "Company name", true, "Matched to existing accounts by UEI, then by name.", "Company", "Company Name", "Account", "Account Name", "Organization", "Organization Name", "Agency"),
        new("Type", "Company type", false, "Agency, Office, Prime, Partner, Competitor or Other. Customer and Government read as Agency; Teaming Partner and Subcontractor as Partner. Defaults to Other.",
            "Company Type", "Account Type", "Relationship", "Category", "Organization Type", "Kind"),
        new("Parent", "Parent company", false, "The department or agency an office sits under (by name or acronym).", "Parent", "Parent Company", "Parent Account", "Parent Organization", "Department"),
        new("Acronym", "Acronym", false, "", "Abbreviation", "Short Name", "Acronym"),
        new("Uei", "UEI", false, "SAM.gov Unique Entity ID.", "UEI", "SAM UEI", "Unique Entity ID", "UEI Number"),
        new("Cage", "CAGE code", false, "", "CAGE", "CAGE Code", "Cage Code"),
        new("Website", "Website", false, "", "Web Site", "URL", "Web Address"),
        new("Phone", "Phone", false, "", "Main Phone", "Phone Number", "Telephone"),
        new("Address", "Address", false, "Street address. City, state and ZIP are added when mapped.", "Street", "Address 1", "Street Address", "Address Line 1"),
        new("City", "City", false, "", "City"),
        new("State", "State", false, "", "State", "State/Province"),
        new("Zip", "ZIP code", false, "", "Zip", "Postal Code", "Zip Code"),
        new("Notes", "Notes", false, "", "Description", "Comments", "Notes"),
    ];

    internal static CrmAccountKind? ParseKind(string? text)
    {
        var t = Key(text);
        return t switch
        {
            "" => CrmAccountKind.Other,
            "agency" or "customer" or "government" or "govt" or "federal" or "federalagency" or "governmentagency" or "department" => CrmAccountKind.Agency,
            "office" or "governmentoffice" or "contractingoffice" or "suboffice" or "command" => CrmAccountKind.Office,
            "prime" or "primecontractor" or "primecontractors" => CrmAccountKind.Prime,
            "partner" or "teamingpartner" or "teammate" or "subcontractor" or "sub" or "vendor" or "jvpartner" => CrmAccountKind.Partner,
            "competitor" or "competition" or "rival" => CrmAccountKind.Competitor,
            "other" or "industry" or "commercial" => CrmAccountKind.Other,
            _ => null,
        };
    }

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var accounts = Service<ICrmAccountService>(context);
        var existing = await accounts.ListAccountsAsync(cancellationToken: cancellationToken);
        var byName = new Dictionary<string, Guid>();
        var byUei = new Dictionary<string, CrmAccountDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in existing)
        {
            byName.TryAdd(Key(a.Name), a.Id);
            if (a.Acronym is { } acronym)
            {
                byName.TryAdd(Key(acronym), a.Id);
            }

            if (a.Uei is { } uei)
            {
                byUei.TryAdd(uei, a);
            }
        }

        var existingById = existing.ToDictionary(a => a.Id);
        var parsed = new List<(MappedRow Row, string Name, string? Parent, SaveCrmAccountRequest Request)>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var name = row.Required("Name");
            var typeText = row.Get("Type");
            var kind = ParseKind(typeText);
            if (typeText is not null && kind is null)
            {
                row.Error("Type", $"Company type '{typeText}' isn't one of Agency, Office, Prime, Partner, Competitor or Other.");
            }

            if (name is not null && !seen.Add(Key(name)))
            {
                row.Error("Name", $"{name} appears more than once in the file.");
            }

            if (row.HasErrors || name is null)
            {
                continue;
            }

            var address = string.Join(", ", new[] { row.Get("Address"), row.Get("City"), string.Join(" ", new[] { row.Get("State"), row.Get("Zip") }.OfType<string>()) }
                .Where(p => !string.IsNullOrWhiteSpace(p)));
            parsed.Add((row, name, row.Get("Parent"), new SaveCrmAccountRequest(name, kind ?? CrmAccountKind.Other, row.Get("Acronym"), null,
                row.Get("Uei")?.ToUpperInvariant(), row.Get("Cage")?.ToUpperInvariant(), row.Get("Website"), row.Get("Phone"), address.Length == 0 ? null : address, row.Get("Notes"))));
        }

        // Parents first: a row waits until the company it sits under is known (already in Workbench or earlier in this pass).
        var pending = parsed;
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => p.Parent is null || byName.ContainsKey(Key(p.Parent)) || !pending.Any(q => Key(q.Name) == Key(p.Parent))).ToList();
            if (ready.Count == 0)
            {
                foreach (var p in pending)
                {
                    p.Row.Error("Parent", $"{p.Name} and its parent company refer to each other.");
                }

                break;
            }

            foreach (var (row, name, parent, request) in ready)
            {
                Guid? parentId = null;
                if (parent is not null)
                {
                    if (!byName.TryGetValue(Key(parent), out var found))
                    {
                        row.Warning("Parent", $"Parent company {parent} isn't in Workbench or the file; {name} is imported without one.");
                    }
                    else
                    {
                        parentId = found;
                    }
                }

                var save = request with { ParentId = parentId };
                var match = save.Uei is { } uei && byUei.TryGetValue(uei, out var u) ? u
                    : byName.TryGetValue(Key(name), out var id) && existingById.TryGetValue(id, out var n) ? n
                    : null;
                if (match is not null)
                {
                    var changed = Changed(("Name", match.Name, save.Name), ("Type", match.Kind, save.Kind), ("Acronym", match.Acronym, save.Acronym ?? match.Acronym),
                        ("Parent", match.ParentId, save.ParentId ?? match.ParentId), ("UEI", match.Uei, save.Uei ?? match.Uei), ("CAGE", match.CageCode, save.CageCode ?? match.CageCode),
                        ("Website", match.Website, save.Website ?? match.Website), ("Phone", match.Phone, save.Phone ?? match.Phone),
                        ("Address", match.Address, save.Address ?? match.Address), ("Notes", match.Notes, save.Notes ?? match.Notes));
                    byName[Key(name)] = match.Id;
                    if (changed.Count == 0)
                    {
                        context.Run.Skip(row.FileName, row.Number, "Account", name, "Unchanged");
                        continue;
                    }

                    // Blank cells keep what Workbench has.
                    var merged = save with
                    {
                        Acronym = save.Acronym ?? match.Acronym, ParentId = save.ParentId ?? match.ParentId, Uei = save.Uei ?? match.Uei, CageCode = save.CageCode ?? match.CageCode,
                        Website = save.Website ?? match.Website, Phone = save.Phone ?? match.Phone, Address = save.Address ?? match.Address, Notes = save.Notes ?? match.Notes,
                        IsActive = match.IsActive,
                    };
                    await context.Run.UpdateAsync(row.FileName, row.Number, typeof(D.CrmAccount), match.Id, "Account", name, UpdateSummary(changed),
                        () => accounts.UpdateAccountAsync(match.Id, merged, cancellationToken), cancellationToken);
                    continue;
                }

                var created = await context.Run.CreateAsync(row.FileName, row.Number, typeof(D.CrmAccount), "Account", name, $"{name} ({CrmLabelsText(save.Kind)})",
                    async () => (await accounts.CreateAccountAsync(save, cancellationToken)).Id, cancellationToken);
                if (created is { } newId)
                {
                    byName[Key(name)] = newId;
                    if (save.Acronym is { } acronym)
                    {
                        byName.TryAdd(Key(acronym), newId);
                    }
                }
            }

            pending = pending.Except(ready).ToList();
        }
    }

    private static string CrmLabelsText(CrmAccountKind kind) => kind.IsGovernment() ? "government" : kind.ToString().ToLowerInvariant();
}

/// <summary>Capture Exec contacts → contacts on their company's account.</summary>
public sealed class CaptureExecContactImporter : CaptureExecImporter
{
    public override ImportKind Kind => ImportKind.CrmContacts;
    public override string Title => "Contacts";
    public override string Description => "People at your customers, partners and competitors. Their company must already be an account (import companies first).";
    public override string? SampleFile => "samples/import/captureexec/contacts.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Company", "Company", true, "The account the contact belongs to, by name or acronym.", "Company", "Company Name", "Account", "Account Name", "Organization", "Agency"),
        new("FirstName", "First name", false, "Or map Full name.", "First Name", "First", "Given Name"),
        new("LastName", "Last name", false, "", "Last Name", "Last", "Surname", "Family Name"),
        new("FullName", "Full name", false, "\"First Last\" or \"Last, First\" when there are no separate name columns.", "Name", "Contact", "Contact Name", "Full Name"),
        new("Title", "Job title", false, "", "Title", "Job Title", "Position"),
        new("Role", "Role", false, "Contracting officer, Contracting specialist, Program manager, Technical lead, Small business specialist, Executive, Business development or Other.",
            "Role", "Contact Role", "Contact Type", "Buying Role", "Influence"),
        new("Email", "Email", false, "Matched to existing contacts by email, then by company and name.", "E-mail", "Email Address", "Work Email"),
        new("Phone", "Phone", false, "", "Phone", "Work Phone", "Office Phone", "Mobile", "Mobile Phone"),
        new("Notes", "Notes", false, "", "Notes", "Comments", "Description"),
    ];

    internal static ContactRole ParseRole(string? text)
    {
        var t = Key(text);
        return t switch
        {
            _ when t.Contains("contractingofficer") || t is "co" or "pco" or "aco" or "ko" => ContactRole.ContractingOfficer,
            _ when t.Contains("contractingspecialist") || t.Contains("contractspecialist") || t is "cs" or "buyer" => ContactRole.ContractingSpecialist,
            _ when t.Contains("programmanager") || t.Contains("projectmanager") || t is "pm" or "cor" or "cotr" => ContactRole.ProgramManager,
            _ when t.Contains("technical") || t.Contains("engineer") || t is "tpoc" => ContactRole.TechnicalLead,
            _ when t.Contains("smallbusiness") || t is "osdbu" or "sbs" => ContactRole.SmallBusinessSpecialist,
            _ when t.Contains("executive") || t.Contains("director") || t.Contains("chief") || t is "ceo" or "cio" or "cto" or "ses" or "decisionmaker" => ContactRole.Executive,
            _ when t.Contains("businessdevelopment") || t.Contains("capture") || t is "bd" => ContactRole.BusinessDevelopment,
            _ => ContactRole.Other,
        };
    }

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var accounts = Service<ICrmAccountService>(context);
        var byName = new Dictionary<string, CrmAccountDto>();
        foreach (var a in await accounts.ListAccountsAsync(cancellationToken: cancellationToken))
        {
            byName.TryAdd(Key(a.Name), a);
            if (a.Acronym is { } acronym)
            {
                byName.TryAdd(Key(acronym), a);
            }
        }

        var contacts = await accounts.ListContactsAsync(cancellationToken: cancellationToken);
        var byEmail = contacts.Where(c => c.Email is not null).GroupBy(c => c.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var byPerson = contacts.GroupBy(c => (c.AccountId, Key(c.FirstName + c.LastName))).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var companyName = row.Required("Company");
            var (first, last) = Names(row);
            if (first is null && last is null)
            {
                row.Error("FirstName", "Map the first and last name (or the full name) and fill them in.");
            }

            CrmAccountDto? account = null;
            if (companyName is not null && !byName.TryGetValue(Key(companyName), out account))
            {
                row.Error("Company", $"Company {companyName} isn't an account yet: import companies first, or add it.");
            }

            var email = row.Get("Email")?.ToLowerInvariant();
            var key = email ?? $"{companyName}|{first}|{last}";
            if (!seen.Add(Key(key)))
            {
                row.Error("Email", $"{first} {last} appears more than once in the file.");
            }

            if (row.HasErrors || account is null)
            {
                continue;
            }

            var label = $"{first} {last}".Trim();
            var request = new SaveCrmContactRequest(account.Id, first ?? "", last ?? "", ParseRole(row.Get("Role")), row.Get("Title"), email, row.Get("Phone"), row.Get("Notes"));
            var match = email is not null && byEmail.TryGetValue(email, out var e) ? e
                : byPerson.TryGetValue((account.Id, Key(first + last)), out var p) ? p
                : null;
            if (match is not null)
            {
                var merged = request with
                {
                    Title = request.Title ?? match.Title, Email = request.Email ?? match.Email, Phone = request.Phone ?? match.Phone, Notes = request.Notes ?? match.Notes,
                    Role = row.Get("Role") is null ? match.Role : request.Role, IsActive = match.IsActive,
                };
                var changed = Changed(("Company", match.AccountId, merged.AccountId), ("First name", match.FirstName, merged.FirstName), ("Last name", match.LastName, merged.LastName),
                    ("Title", match.Title, merged.Title), ("Role", match.Role, merged.Role), ("Email", match.Email, merged.Email), ("Phone", match.Phone, merged.Phone), ("Notes", match.Notes, merged.Notes));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Contact", label, "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(D.CrmContact), match.Id, "Contact", label, UpdateSummary(changed),
                    () => accounts.UpdateContactAsync(match.Id, merged, cancellationToken), cancellationToken);
                continue;
            }

            await context.Run.CreateAsync(row.FileName, row.Number, typeof(D.CrmContact), "Contact", label, $"{label} at {account.Name}",
                async () => (await accounts.CreateContactAsync(request, cancellationToken)).Id, cancellationToken);
        }
    }

    private static (string? First, string? Last) Names(MappedRow row)
    {
        var first = row.Get("FirstName");
        var last = row.Get("LastName");
        if ((first is null || last is null) && row.Get("FullName") is { } full)
        {
            var name = PersonName(full);
            var space = name.LastIndexOf(' ');
            return space > 0 ? (first ?? name[..space].Trim(), last ?? name[(space + 1)..].Trim()) : (first ?? name, last ?? "");
        }

        return (first, last);
    }
}

/// <summary>
/// Capture Exec opportunities → the pipeline, with their stage, Pwin/Pgo, dates, managers and (for decided pursuits) outcome.
/// Business development or administrators: imported stages are history, so they don't go through the go/no-go gates and
/// don't notify anyone.
/// </summary>
public sealed class CaptureExecOpportunityImporter : CaptureExecImporter
{
    public override ImportKind Kind => ImportKind.Opportunities;
    public override string Title => "Opportunities";
    public override string Description => "Pursuits with stage, value, Pwin/Pgo, key dates, capture and proposal managers, competitors and outcome. Re-import the same export to update them.";
    public override string? SampleFile => "samples/import/captureexec/opportunities.csv";

    public override IReadOnlyList<ImportField> Fields { get; } =
    [
        new("Id", "Capture Exec ID", false, "Capture Exec's own id: re-imports update the same opportunity.", "ID", "Opportunity ID", "Opp ID", "Record ID", "CE ID", "Pursuit ID", "Opportunity Number"),
        new("Name", "Opportunity name", true, "", "Opportunity", "Opportunity Name", "Opportunity Title", "Title", "Pursuit", "Pursuit Name", "Program", "Program Name"),
        new("Description", "Description", false, "", "Description", "Summary", "Scope", "Synopsis"),
        new("Solicitation", "Solicitation number", false, "Also used to match opportunities already in the pipeline.", "Solicitation", "Solicitation #", "Solicitation Number", "RFP Number", "RFP #", "Sol #", "Notice ID"),
        new("Agency", "Agency", false, "Matched to a government account by name or acronym; otherwise kept as text.", "Agency", "Customer", "Client", "Department", "Buying Office", "Contracting Office", "Sub Agency", "Office"),
        new("Naics", "NAICS", false, "", "NAICS", "NAICS Code", "Primary NAICS"),
        new("SetAside", "Set-aside", false, "Full and open, Small business, 8(a), HUBZone, SDVOSB, WOSB, EDWOSB or VOSB.", "Set Aside", "Set-Aside", "Set Aside Type", "Competition Type", "Socioeconomic"),
        new("Vehicle", "Contract vehicle", false, "", "Contract Vehicle", "Vehicle", "IDIQ", "GWAC", "Acquisition Vehicle"),
        new("ContractType", "Contract type", false, "FFP, T&M, LH, CPFF, CPAF or CPIF.", "Contract Type", "Pricing Type", "Award Type"),
        new("Type", "New or recompete", false, "Recompete, Follow-on or Recompetition read as recompete; anything else is new work.", "Type", "Opportunity Type", "Business Type", "New/Recompete", "Pursuit Type"),
        new("Value", "Total value", false, "Total estimated value, options included.", "Value", "Total Value", "Estimated Value", "Contract Value", "TCV", "Total Contract Value", "Amount", "Est. Value"),
        new("Stage", "Stage", false, "Capture Exec's stage or status: Identified, Qualified, Capture, Proposal, Submitted, Won, Lost or No bid (common synonyms read too). Defaults to Identified.",
            "Stage", "Status", "Phase", "Pipeline Stage", "Capture Stage", "Sales Stage"),
        new("Pwin", "Pwin", false, "Probability of win, 0–100 (or 0–1).", "PWin", "P-Win", "Probability of Win", "Win Probability", "PWin %"),
        new("Pgo", "Pgo", false, "Probability the government awards, 0–100 (or 0–1). Defaults to 50.", "PGo", "P-Go", "Probability of Go", "Go Probability", "PGo %"),
        new("AwardDate", "Expected award date", false, "", "Award Date", "Est. Award Date", "Estimated Award Date", "Projected Award Date", "Close Date", "Expected Award"),
        new("PopMonths", "Period of performance", false, "Months, or years when the cell says years (\"5 years\").", "Period of Performance", "PoP", "Duration", "POP (Months)", "Contract Length"),
        new("RfiDue", "RFI due", false, "", "RFI Due", "RFI Due Date", "Sources Sought Due", "RFI Date"),
        new("RfpRelease", "RFP release", false, "", "RFP Release", "RFP Release Date", "Est. RFP Release", "Solicitation Release", "Final RFP Date"),
        new("ProposalDue", "Proposal due", false, "", "Proposal Due", "Proposal Due Date", "Response Due", "Due Date", "Submission Date", "RFP Due"),
        new("Place", "Place of performance", false, "", "Place of Performance", "Location", "Performance Location"),
        new("Incumbent", "Incumbent", false, "", "Incumbent", "Current Contractor", "Incumbent Contractor"),
        new("Competitors", "Competitors", false, "Several names separated by semicolons or commas.", "Competitors", "Competition", "Known Competitors"),
        new("CaptureManager", "Capture manager", false, "Email, or the name of a Workbench user.", "Capture Manager", "Capture Lead", "Owner", "Opportunity Owner", "BD Lead", "Capture Manager Email"),
        new("ProposalManager", "Proposal manager", false, "Email, or the name of a Workbench user.", "Proposal Manager", "Proposal Lead", "Proposal Manager Email"),
        new("OutcomeReason", "Win/loss reason", false, "Price, Technical, Past performance, Customer relationship, Incumbency, Staffing, Teaming, Not aligned, No capacity, Cancelled or Other.",
            "Win/Loss Reason", "Loss Reason", "Win Reason", "Reason", "Outcome Reason", "No Bid Reason"),
        new("AwardedTo", "Awarded to", false, "Who won, when we lost.", "Awardee", "Awarded To", "Winner", "Winning Company"),
        new("AwardedValue", "Awarded value", false, "", "Award Value", "Awarded Value", "Award Amount"),
        new("DecidedOn", "Decision date", false, "When it was won, lost or not bid.", "Decision Date", "Award Decision Date", "Closed Date", "Date Closed", "Outcome Date"),
        new("Url", "Link", false, "SAM.gov or GovWin link.", "URL", "Link", "SAM.gov Link", "GovWin Link", "Notice URL"),
    ];

    internal static OpportunityStage? ParseStage(string? text)
    {
        var t = Key(text);
        return t switch
        {
            "" => OpportunityStage.Identified,
            "identified" or "identification" or "lead" or "prospect" or "tracking" or "forecast" or "watch" or "longterm" or "new" or "0" or "1" => OpportunityStage.Identified,
            "qualified" or "qualification" or "qualify" or "pursue" or "interest" or "2" => OpportunityStage.Qualified,
            "capture" or "prerfp" or "pursuit" or "positioning" or "shaping" or "bid" or "bidding" or "3" => OpportunityStage.Capture,
            "proposal" or "proposaldevelopment" or "writing" or "rfp" or "rfpreleased" or "inproposal" or "4" => OpportunityStage.Proposal,
            "submitted" or "proposalsubmitted" or "awaitingaward" or "evaluation" or "underevaluation" or "pendingaward" or "5" => OpportunityStage.Submitted,
            "won" or "win" or "awarded" or "closedwon" => OpportunityStage.Won,
            "lost" or "loss" or "closedlost" or "notawarded" => OpportunityStage.Lost,
            "nobid" or "nogo" or "dropped" or "declined" or "passed" or "cancelled" or "canceled" or "closednobid" or "withdrawn" => OpportunityStage.NoBid,
            _ => null,
        };
    }

    internal static OutcomeReason? ParseReason(string? text)
    {
        var t = Key(text);
        return t switch
        {
            "" => null,
            _ when t.Contains("price") || t.Contains("cost") => OutcomeReason.Price,
            _ when t.Contains("technical") || t.Contains("solution") => OutcomeReason.Technical,
            _ when t.Contains("pastperformance") || t.Contains("experience") => OutcomeReason.PastPerformance,
            _ when t.Contains("relationship") || t.Contains("customer") => OutcomeReason.CustomerRelationship,
            _ when t.Contains("incumb") => OutcomeReason.Incumbency,
            _ when t.Contains("staff") || t.Contains("personnel") || t.Contains("keypersonnel") => OutcomeReason.Staffing,
            _ when t.Contains("team") || t.Contains("partner") => OutcomeReason.Teaming,
            _ when t.Contains("align") || t.Contains("fit") || t.Contains("scope") => OutcomeReason.NotAligned,
            _ when t.Contains("capacity") || t.Contains("bandwidth") || t.Contains("resources") => OutcomeReason.NoCapacity,
            _ when t.Contains("cancel") => OutcomeReason.Cancelled,
            _ => OutcomeReason.Other,
        };
    }

    /// <summary>A percentage written 40, 40%, or 0.4.</summary>
    internal static int? Percent(MappedRow row, string field)
    {
        if (row.Decimal(field) is not { } value)
        {
            return null;
        }

        var text = row.Get(field) ?? "";
        if (value is > 0m and <= 1m && !text.Contains('%'))
        {
            value *= 100m;
        }

        if (value is < 0m or > 100m)
        {
            row.Error(field, $"{field} '{text}' isn't a percentage from 0 to 100.");
            return null;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    internal static int? Months(MappedRow row)
    {
        var text = row.Get("PopMonths");
        if (text is null)
        {
            return null;
        }

        var lower = text.ToLowerInvariant();
        var digits = new string(lower.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!decimal.TryParse(digits, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            row.Error("PopMonths", $"Period of performance '{text}' isn't a number of months or years.");
            return null;
        }

        return (int)Math.Round(lower.Contains("year") || lower.Contains("yr") ? number * 12 : number);
    }

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var access = Service<CrmAccess>(context);
        if (!(await access.RolesAsync(cancellationToken)).Has(Permission.CaptureManage))
        {
            context.Run.Error(rows[0].FileName, 0, null, "Only business development and administrators can import opportunities. Ask one of them to run this import.");
            return;
        }

        var factory = Service<IWorkbenchDbContextFactory>(context);
        var audit = Service<AuditRecorder>(context);
        var clock = Service<TimeProvider>(context);
        Dictionary<string, Guid> agencies;
        Dictionary<string, string> people;
        Dictionary<string, D.Opportunity> bySourceId;
        Dictionary<string, D.Opportunity> bySolicitation;
        await using (var db = await factory.CreateAsync(cancellationToken))
        {
            agencies = [];
            foreach (var a in await db.Set<D.CrmAccount>().AsNoTracking().Where(a => a.Kind == D.CrmAccountKind.Agency || a.Kind == D.CrmAccountKind.Office).ToListAsync(cancellationToken))
            {
                agencies.TryAdd(Key(a.Name), a.Id);
                if (a.Acronym is { } acronym)
                {
                    agencies.TryAdd(Key(acronym), a.Id);
                }
            }

            people = [];
            foreach (var u in await db.Set<AppUser>().AsNoTracking().Select(u => new { u.Email, u.DisplayName }).ToListAsync(cancellationToken))
            {
                people.TryAdd(Key(u.DisplayName), u.Email);
                people.TryAdd(Key(u.Email), u.Email);
            }

            var all = await db.Set<D.Opportunity>().AsNoTracking().ToListAsync(cancellationToken);
            bySourceId = all.Where(o => o.Source == D.OpportunitySource.CaptureExec && o.SourceId != null).GroupBy(o => o.SourceId!).ToDictionary(g => g.Key, g => g.First());
            bySolicitation = all.Where(o => o.SolicitationKey != null).GroupBy(o => o.SolicitationKey!).ToDictionary(g => g.Key, g => g.First());
        }

        string? Person(MappedRow row, string field)
        {
            var text = row.Get(field);
            if (text is null)
            {
                return null;
            }

            if (people.TryGetValue(Key(text), out var email) || people.TryGetValue(Key(PersonName(text)), out email))
            {
                return email;
            }

            if (text.Contains('@'))
            {
                return text.Trim().ToLowerInvariant();
            }

            row.Warning(field, $"{text} isn't a Workbench user; {field} is left empty. Add them under People, then import again.");
            return null;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSolicitations = new HashSet<string>();
        foreach (var row in rows)
        {
            var sourceId = row.Get("Id");
            var name = row.Required("Name");
            var stageText = row.Get("Stage");
            var stage = ParseStage(stageText);
            if (stageText is not null && stage is null)
            {
                row.Error("Stage", $"Stage '{stageText}' isn't one Workbench knows. Use Identified, Qualified, Capture, Proposal, Submitted, Won, Lost or No bid.");
            }

            var typeText = row.Get("ContractType");
            var contractType = typeText is null ? null : ContractImporter.ParseKind(typeText);
            if (typeText is not null && contractType is null)
            {
                row.Error("ContractType", $"Contract type '{typeText}' isn't one of FFP, T&M, LH, CPFF, CPAF, CPIF.");
            }

            var naics = row.Get("Naics");
            if (naics is not null && (naics.Length is < 2 or > 6 || !naics.All(char.IsDigit)))
            {
                row.Error("Naics", $"NAICS '{naics}' isn't 2 to 6 digits.");
            }

            var value = row.Decimal("Value") ?? 0m;
            if (value < 0)
            {
                row.Error("Value", "The total value can't be negative.");
            }

            var pwin = Percent(row, "Pwin");
            var pgo = Percent(row, "Pgo");
            var months = Months(row);
            var dates = (Award: row.Date("AwardDate"), Rfi: row.Date("RfiDue"), Rfp: row.Date("RfpRelease"), Due: row.Date("ProposalDue"), Decided: row.Date("DecidedOn"));
            var awardedValue = row.Decimal("AwardedValue");
            var solicitation = row.Get("Solicitation");
            var solicitationKey = PipelineMath.SolicitationKey(solicitation);
            if (sourceId is not null && !seenIds.Add(sourceId))
            {
                row.Error("Id", $"Capture Exec ID {sourceId} appears more than once in the file.");
            }

            if (solicitationKey is not null && !seenSolicitations.Add(solicitationKey))
            {
                row.Error("Solicitation", $"Solicitation {solicitation} appears more than once in the file.");
            }

            if (row.HasErrors || name is null)
            {
                continue;
            }

            var agencyText = row.Get("Agency");
            Guid? agencyId = agencyText is not null && agencies.TryGetValue(Key(agencyText), out var found) ? found : null;
            var competitors = (row.Get("Competitors") ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var incumbent = row.Get("Incumbent");
            if (incumbent is not null && !competitors.Contains(incumbent, StringComparer.OrdinalIgnoreCase))
            {
                competitors.Insert(0, incumbent);
            }

            var kindText = Key(row.Get("Type"));
            var request = new SaveOpportunityRequest(
                name, row.Get("Description"), solicitation, agencyId, agencyId is null ? agencyText : null, naics, SamSetAsides.Parse(row.Get("SetAside")),
                row.Get("Vehicle"), contractType, kindText.Contains("recomp") || kindText.Contains("followon") || (incumbent is not null && kindText.Length == 0) ? OpportunityKind.Recompete : OpportunityKind.New,
                value, dates.Award, months, dates.Rfi, dates.Rfp, dates.Due, row.Get("Place"), null, incumbent,
                Person(row, "CaptureManager"), Person(row, "ProposalManager"), pwin ?? 10, pgo ?? 50,
                null, row.IsMapped("Competitors") || incumbent is not null ? competitors.Select(c => new SaveCompetitor(null, c, string.Equals(c, incumbent, StringComparison.OrdinalIgnoreCase))).ToList() : null,
                row.Get("Url"));
            var outcome = new Outcome(stage ?? OpportunityStage.Identified, ParseReason(row.Get("OutcomeReason")), row.Get("AwardedTo"), awardedValue, dates.Decided);

            var existing = sourceId is not null && bySourceId.TryGetValue(sourceId, out var byId) ? byId
                : solicitationKey is not null && bySolicitation.TryGetValue(solicitationKey, out var bySol) ? bySol
                : null;
            if (existing is not null)
            {
                var keepPwin = pwin is null ? request with { Pwin = existing.Pwin } : request;
                var merged = (pgo is null ? keepPwin with { Pgo = existing.Pgo } : keepPwin) with
                {
                    CaptureManagerEmail = keepPwin.CaptureManagerEmail ?? existing.CaptureManagerEmail,
                    ProposalManagerEmail = keepPwin.ProposalManagerEmail ?? existing.ProposalManagerEmail,
                };
                var targetStage = stageText is null ? existing.Stage.ToContract() : outcome.Stage;
                var changed = Changed(("Name", existing.Name, merged.Name), ("Value", existing.EstimatedValue, merged.EstimatedValue), ("Stage", existing.Stage.ToContract(), targetStage),
                    ("Pwin", existing.Pwin, merged.Pwin), ("Pgo", existing.Pgo, merged.Pgo), ("Proposal due", existing.ProposalDueOn, merged.ProposalDueOn),
                    ("Award date", existing.EstimatedAwardDate, merged.EstimatedAwardDate), ("RFP release", existing.RfpReleaseOn, merged.RfpReleaseOn),
                    ("Agency", existing.AgencyAccountId?.ToString() ?? existing.AgencyName, merged.AgencyAccountId?.ToString() ?? merged.AgencyName),
                    ("Capture manager", existing.CaptureManagerEmail, merged.CaptureManagerEmail), ("Proposal manager", existing.ProposalManagerEmail, merged.ProposalManagerEmail),
                    ("Solicitation", existing.SolicitationNumber, merged.SolicitationNumber), ("Description", existing.Description, merged.Description),
                    ("Capture Exec ID", existing.SourceId, sourceId ?? existing.SourceId));
                if (changed.Count == 0)
                {
                    context.Run.Skip(row.FileName, row.Number, "Opportunity", $"{existing.Number} {name}", "Unchanged");
                    continue;
                }

                await context.Run.UpdateAsync(row.FileName, row.Number, typeof(D.Opportunity), existing.Id, "Opportunity", $"{existing.Number} {name}", UpdateSummary(changed),
                    () => SaveAsync(context, factory, audit, clock, existing.Id, merged, outcome with { Stage = targetStage }, sourceId, cancellationToken), cancellationToken);
                continue;
            }

            var label = sourceId is null ? name : $"{sourceId} {name}";
            await context.Run.CreateAsync(row.FileName, row.Number, typeof(D.Opportunity), "Opportunity", label,
                $"{name} · {OpportunityStages.Label(outcome.Stage)}{(value > 0 ? $" · {value:C0}" : "")}",
                () => SaveAsync(context, factory, audit, clock, null, request, outcome, sourceId, cancellationToken), cancellationToken);
        }
    }

    internal sealed record Outcome(OpportunityStage Stage, OutcomeReason? Reason, string? AwardedTo, decimal? AwardedValue, DateOnly? DecidedOn);

    /// <summary>
    /// Creates (<paramref name="id"/> null) or updates an opportunity straight in the database: the same validation as the
    /// service, but imported stages and outcomes are history (no gates, no B&amp;P code, no notifications).
    /// </summary>
    private static async Task<Guid> SaveAsync(ImportContext context, IWorkbenchDbContextFactory factory, AuditRecorder audit, TimeProvider clock, Guid? id,
        SaveOpportunityRequest request, Outcome outcome, string? sourceId, CancellationToken ct)
    {
        await using var db = await factory.CreateAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var actor = context.Job.CreatedBy;
        D.Opportunity opportunity;
        if (id is { } existingId)
        {
            opportunity = await db.Set<D.Opportunity>().FirstAsync(o => o.Id == existingId, ct);
        }
        else
        {
            opportunity = await OpportunityService.NewAsync(db, actor, now, D.OpportunitySource.CaptureExec, ct);
            db.Set<D.Opportunity>().Add(opportunity);
        }

        var before = id is null ? null : OpportunityService.Snapshot(opportunity);

        // Competitors are replaced when the export has them: keep what was there so a rollback can put it back.
        var oldCompetitors = new List<(Guid Id, string Snapshot)>();
        if (id is not null && request.Competitors is not null)
        {
            await using var read = await factory.CreateAsync(ct);
            foreach (var competitorId in await read.Set<D.OpportunityCompetitor>().AsNoTracking().Where(c => c.OpportunityId == opportunity.Id).Select(c => c.Id).ToListAsync(ct))
            {
                if (await EntitySnapshots.CaptureAsync(read, typeof(D.OpportunityCompetitor), competitorId, ct) is { } snapshot)
                {
                    oldCompetitors.Add((competitorId, snapshot));
                }
            }
        }

        await OpportunityService.ApplyAsync(db, opportunity, request, ct);
        if (sourceId is not null)
        {
            opportunity.SourceId = sourceId.Length <= 100 ? sourceId : throw new ArgumentException("The Capture Exec ID can be at most 100 characters.");
        }

        opportunity.UpdatedAtUtc = now;
        var to = outcome.Stage.ToDomain();
        D.OpportunityStageChange? change = null;
        if (id is null)
        {
            // New: it starts in its imported stage (the audit entry records the import).
            opportunity.Stage = to;
        }
        else if (opportunity.Stage != to)
        {
            change = new D.OpportunityStageChange { OpportunityId = opportunity.Id, From = opportunity.Stage, To = to, ChangedBy = actor, ChangedAtUtc = now, Note = "Imported from Capture Exec" };
            db.Set<D.OpportunityStageChange>().Add(change);
            opportunity.Stage = to;
            opportunity.StageChangedAtUtc = now;
        }

        if (to >= D.OpportunityStage.Won)
        {
            opportunity.OutcomeReason = (outcome.Reason ?? OutcomeReason.Other).ToDomain();
            opportunity.DecidedOn = outcome.DecidedOn ?? opportunity.DecidedOn ?? DateOnly.FromDateTime(now);
            opportunity.AwardedValue = outcome.AwardedValue ?? opportunity.AwardedValue;
            opportunity.AwardedTo = CrmMapping.Clean(outcome.AwardedTo, 200, "Awarded to") ?? opportunity.AwardedTo;
        }

        audit.Record(db, OpportunityService.AuditEntityType, opportunity.Id, id is null ? "Created" : "Updated", before, OpportunityService.Snapshot(opportunity),
            Reason(context, "opportunity"));
        await db.SaveChangesAsync(ct);
        if (change is not null)
        {
            await context.Run.RecordCreatedAsync(typeof(D.OpportunityStageChange), change.Id, $"Stage change of {opportunity.Number}", ct);
        }

        if (request.Competitors is not null)
        {
            var kept = oldCompetitors.Select(c => c.Id).ToHashSet();
            foreach (var competitorId in await db.Set<D.OpportunityCompetitor>().AsNoTracking().Where(c => c.OpportunityId == opportunity.Id).Select(c => c.Id).ToListAsync(ct))
            {
                if (!kept.Contains(competitorId))
                {
                    await context.Run.RecordCreatedAsync(typeof(D.OpportunityCompetitor), competitorId, $"Competitor on {opportunity.Number}", ct);
                }
            }

            foreach (var (competitorId, snapshot) in oldCompetitors)
            {
                await context.Run.RecordDeletedAsync(typeof(D.OpportunityCompetitor), competitorId, $"Competitor on {opportunity.Number}", snapshot, ct);
            }
        }

        return opportunity.Id;
    }
}
