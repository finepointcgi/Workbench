using Microsoft.EntityFrameworkCore;
using Workbench.Application.Crm;
using Workbench.Application.Imports.CaptureExec;
using Workbench.Application.Imports.Costpoint;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using D = Workbench.Domain.Crm;
using ImportKind = Workbench.Domain.Imports.ImportKind;
using ImportSource = Workbench.Domain.Imports.ImportSource;

namespace Workbench.Application.Imports.Commercial;

/// <summary>
/// Companies, contacts and deals from a HubSpot export, a Salesforce export, or any plain CSV/Excel list: the way a commercial
/// firm brings its pipeline across. Same wizard as the Capture Exec import (upload, map, validate, dry run, import, roll back);
/// the three sources differ only in the headers they auto-map and the deal stages they translate (<see cref="CommercialStages"/>).
/// <para>
/// Duplicates are never overwritten silently. A row that matches a record already in Workbench (companies on website domain,
/// then name; contacts on email, then company and name; deals on the source system's id, then name and company) is skipped and
/// listed as a duplicate in the dry run. Only when <c>ImportOptionsDto.UpdateExisting</c> is ticked, after seeing that list, does the
/// import update it, and then only the cells the file fills in.
/// </para>
/// <para>
/// Header sources. Salesforce API field names (Account: Name, Website, Phone, BillingStreet, BillingCity, BillingState,
/// BillingPostalCode, BillingCountry, Industry, Type, ParentId, Description; Contact: FirstName, LastName, Email, Title, Phone,
/// MobilePhone, AccountId, Department; Opportunity: Name, AccountId, Amount, StageName, Probability, CloseDate, Type, LeadSource,
/// Description, NextStep, IsClosed, IsWon) are the ones in the Object Reference for the Salesforce Platform, read 2026-11-16:
/// https://developer.salesforce.com/docs/platform/object-reference/references/standard-objects/object-opportunity.html (and
/// object-account.html, object-contact.html). HubSpot property labels, which become export headers (Company name, Company domain
/// name, Phone number, City, State/Region, Industry, Type, Website URL, Record ID; First name, Last name, Email, Job title, Company
/// name, Contact owner; Deal name, Amount, Deal stage, Pipeline, Close date, Create date, Deal owner, Deal type) are from
/// https://knowledge.hubspot.com/properties/hubspot-crm-default-company-properties,
/// https://knowledge.hubspot.com/properties/hubspots-default-contact-properties and
/// https://knowledge.hubspot.com/properties/hubspots-default-deal-properties; the default deal stages (Appointment scheduled,
/// Qualified to buy, Presentation scheduled, Decision maker bought-in, Contract sent, Closed won, Closed lost) from
/// https://knowledge.hubspot.com/object-settings/set-up-and-customize-pipelines. Those pages don't print literal export
/// headers, don't list association columns ("Associated Company") and don't give Salesforce's UI report labels or default stage
/// names: those spellings (and "Deal probability", "Closed lost reason") are aliases from experience, matched case- and
/// punctuation-insensitively, and the mapping screen covers any file that differs.
/// </para>
/// </summary>
public abstract class CommercialImporter(ImportSource source) : CostpointImporter
{
    public override ImportSource Source => source;

    protected ImportSource Flavor => source;

    protected string System => source switch { ImportSource.HubSpot => "HubSpot", ImportSource.Salesforce => "Salesforce", _ => "a CSV file" };

    protected static string Key(string? text) => FieldMatcher.Normalize(text);

    protected static bool Update(ImportContext context) => context.Options.UpdateExisting;

    /// <summary>"https://www.Example.com/about" → "example.com": how two spellings of one company's website are matched.</summary>
    internal static string? Domain(string? website)
    {
        if (string.IsNullOrWhiteSpace(website))
        {
            return null;
        }

        var text = website.Trim().ToLowerInvariant();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        var end = text.IndexOfAny(['/', '?', '#', ' ']);
        if (end >= 0)
        {
            text = text[..end];
        }

        if (text.StartsWith("www.", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        return text.Contains('.') ? text : null;
    }

    /// <summary>A matching record is left alone and reported as a duplicate, unless the user chose to update.</summary>
    protected static void Duplicate(ImportContext context, MappedRow row, string entityType, string label, string matchedOn)
    {
        row.Warning(null, $"{label} is already in Workbench (matched on {matchedOn}); left as it is. Tick \"Update records that are already in Workbench\" to change it.");
        context.Run.Skip(row.FileName, row.Number, entityType, label, $"Duplicate: matches an existing record on {matchedOn}");
    }

    protected string ImportReason(ImportContext context, string what) => $"Imported {what} from {System} (import job {context.Job.Id.ToString()[..8]})";

    protected string[] Aliases(string[] hubspot, string[] salesforce, string[] generic) => source switch
    {
        ImportSource.HubSpot => [.. hubspot, .. generic],
        ImportSource.Salesforce => [.. salesforce, .. generic],
        _ => [.. generic, .. hubspot, .. salesforce],
    };
}

/// <summary>Deal stages of HubSpot, Salesforce and spreadsheets → Workbench's pipeline stages.</summary>
public static class CommercialStages
{
    /// <summary>
    /// HubSpot's default Sales Pipeline: Appointment scheduled → Identified, Qualified to buy → Qualified, Presentation scheduled →
    /// Capture (pursuing), Decision maker bought-in → Proposal, Contract sent → Submitted, Closed won → Won, Closed lost → Lost.
    /// Salesforce's usual stages: Prospecting → Identified, Qualification and Needs Analysis → Qualified, Value Proposition, Id.
    /// Decision Makers and Perception Analysis → Capture, Proposal/Price Quote → Proposal, Negotiation/Review → Submitted,
    /// Closed Won → Won, Closed Lost → Lost. A custom stage reads by its words, else by the deal's probability.
    /// </summary>
    internal static OpportunityStage? Parse(string? text)
    {
        var t = FieldMatcher.Normalize(text);
        return t switch
        {
            "" => null,
            "appointmentscheduled" or "prospecting" or "prospect" or "lead" or "new" or "open" or "identified" or "targeting" or "discovery" => OpportunityStage.Identified,
            "qualifiedtobuy" or "qualification" or "qualified" or "needsanalysis" or "marketingqualified" or "salesqualified" or "qualify" => OpportunityStage.Qualified,
            "presentationscheduled" or "valueproposition" or "iddecisionmakers" or "identifydecisionmakers" or "perceptionanalysis" or "demo" or "presentation" or "solutionbuilding" or "evaluation" => OpportunityStage.Capture,
            "decisionmakerboughtin" or "proposalpricequote" or "proposal" or "pricequote" or "quote" or "proposalsent" or "quotesent" => OpportunityStage.Proposal,
            "contractsent" or "negotiationreview" or "negotiation" or "contractnegotiation" or "verbalagreement" or "commitment" or "closing" or "contract" => OpportunityStage.Submitted,
            "closedwon" or "won" or "win" or "closedandwon" or "customer" => OpportunityStage.Won,
            "closedlost" or "lost" or "loss" => OpportunityStage.Lost,
            "closednobid" or "nobid" or "disqualified" or "dropped" or "cancelled" or "canceled" or "abandoned" or "onhold" => OpportunityStage.NoBid,
            _ => CaptureExecOpportunityImporter.ParseStage(text),
        };
    }

    /// <summary>The stage a custom stage most likely is, from its win probability (0–100): used when its name isn't recognized.</summary>
    internal static OpportunityStage FromProbability(int probability) => probability switch
    {
        >= 100 => OpportunityStage.Won,
        >= 90 => OpportunityStage.Submitted,
        >= 70 => OpportunityStage.Proposal,
        >= 40 => OpportunityStage.Capture,
        >= 20 => OpportunityStage.Qualified,
        _ => OpportunityStage.Identified,
    };
}

/// <summary>Companies → accounts (kind Other: a commercial customer, or a partner or competitor when the type says so).</summary>
public sealed class CommercialAccountImporter(ImportSource source) : CommercialImporter(source)
{
    public override ImportKind Kind => ImportKind.CrmAccounts;
    public override string Title => Flavor == ImportSource.Salesforce ? "Accounts" : "Companies (accounts)";
    public override string Description => "Your customers, prospects and partners. Import these first: contacts and deals find their company by name. Companies already in Workbench (same website domain or name) are listed as duplicates and left alone.";
    public override string? SampleFile => Flavor switch { ImportSource.HubSpot => "samples/import/hubspot/companies.csv", ImportSource.Salesforce => "samples/import/salesforce/accounts.csv", _ => "samples/import/generic/companies.csv" };

    public override IReadOnlyList<ImportField> Fields =>
    [
        new("Name", "Company name", true, "Matched to existing accounts by website domain, then by name.",
            Aliases(["Company name", "Name"], ["Account Name", "Name"], ["Company", "Account", "Organization", "Organization Name", "Customer", "Client", "Client Name"])),
        new("Website", "Website", false, "Its domain (example.com) is the first thing duplicates are matched on.",
            Aliases(["Website URL", "Company domain name", "Domain Name"], ["Website"], ["Web Site", "URL", "Web Address", "Domain", "Website"])),
        new("Type", "Company type", false, "Partner, Reseller or Vendor read as a partner and Competitor as a competitor; anything else is a customer or prospect.",
            Aliases(["Type", "Lifecycle stage"], ["Type", "Account Type"], ["Company Type", "Relationship", "Category"])),
        new("Phone", "Phone", false, "", Aliases(["Phone number"], ["Phone"], ["Main Phone", "Telephone", "Phone Number"])),
        new("Address", "Street address", false, "City, state, ZIP and country are added when mapped.",
            Aliases(["Street Address", "Address"], ["BillingStreet", "Billing Street"], ["Street", "Address 1", "Address Line 1"])),
        new("City", "City", false, "", Aliases(["City"], ["BillingCity", "Billing City"], ["City"])),
        new("State", "State", false, "", Aliases(["State/Region"], ["BillingState", "Billing State/Province", "Billing State"], ["State", "State/Province", "Province"])),
        new("Zip", "ZIP code", false, "", Aliases(["Postal Code"], ["BillingPostalCode", "Billing Zip/Postal Code", "Billing Zip"], ["Zip", "Zip Code", "Postal Code", "Postcode"])),
        new("Country", "Country", false, "", Aliases(["Country/Region"], ["BillingCountry", "Billing Country"], ["Country"])),
        new("Industry", "Industry", false, "Kept in the notes.", Aliases(["Industry"], ["Industry"], ["Sector"])),
        new("Notes", "Notes", false, "", Aliases(["Description", "About Us"], ["Description"], ["Comments", "Notes", "Description"])),
    ];

    internal static CrmAccountKind ParseKind(string? text)
    {
        var t = Key(text);
        return t switch
        {
            _ when t.Contains("competitor") => CrmAccountKind.Competitor,
            _ when t.Contains("partner") || t.Contains("reseller") || t.Contains("vendor") || t.Contains("supplier") || t.Contains("subcontractor") => CrmAccountKind.Partner,
            _ => CrmAccountKind.Other,
        };
    }

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var accounts = Service<ICrmAccountService>(context);
        var existing = await accounts.ListAccountsAsync(cancellationToken: cancellationToken);
        var byDomain = new Dictionary<string, CrmAccountDto>();
        var byName = new Dictionary<string, CrmAccountDto>();
        foreach (var a in existing)
        {
            if (Domain(a.Website) is { } domain)
            {
                byDomain.TryAdd(domain, a);
            }

            byName.TryAdd(Key(a.Name), a);
        }

        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var name = row.Required("Name");
            var website = row.Get("Website");
            var domain = Domain(website);
            if (name is null)
            {
                continue;
            }

            var firstDomain = domain is null || seen.Add($"d|{domain}");
            var firstName = seen.Add($"n|{Key(name)}");
            if (!firstDomain || !firstName)
            {
                row.Error("Name", $"{name} appears more than once in the file (same website domain or name).");
                continue;
            }

            var address = string.Join(", ", new[] { row.Get("Address"), row.Get("City"), string.Join(" ", new[] { row.Get("State"), row.Get("Zip") }.OfType<string>()), row.Get("Country") }
                .Where(p => !string.IsNullOrWhiteSpace(p)));
            var notes = new[] { row.Get("Industry") is { } industry ? $"Industry: {industry}" : null, row.Get("Notes") }.OfType<string>().ToList();
            var request = new SaveCrmAccountRequest(name, ParseKind(row.Get("Type")), null, null, null, null, website, row.Get("Phone"),
                address.Length == 0 ? null : address, notes.Count == 0 ? null : string.Join("\n", notes));

            CrmAccountDto? match = null;
            string? matchedOn = null;
            if (domain is not null && byDomain.TryGetValue(domain, out var d))
            {
                (match, matchedOn) = (d, "website domain");
            }
            else if (byName.TryGetValue(Key(name), out var n))
            {
                (match, matchedOn) = (n, "company name");
            }

            if (match is null)
            {
                var created = await context.Run.CreateAsync(row.FileName, row.Number, typeof(D.CrmAccount), "Account", name, $"{name}{(domain is null ? "" : $" ({domain})")}",
                    async () => (await accounts.CreateAccountAsync(request, cancellationToken)).Id, cancellationToken);
                if (created is not null)
                {
                    // Later rows of this file see it (as a placeholder when this is only a check).
                    var placeholder = new CrmAccountDto(created.Value, name, null, request.Kind, null, null, null, null, website, null, null, null, null, null, true, 0, 0);
                    byName.TryAdd(Key(name), placeholder);
                    if (domain is not null)
                    {
                        byDomain.TryAdd(domain, placeholder);
                    }
                }

                continue;
            }

            if (!Update(context))
            {
                Duplicate(context, row, "Account", name, matchedOn!);
                continue;
            }

            var merged = request with
            {
                Kind = row.Get("Type") is null ? match.Kind : request.Kind, Website = request.Website ?? match.Website, Phone = request.Phone ?? match.Phone,
                Address = request.Address ?? match.Address, Notes = request.Notes ?? match.Notes, Acronym = match.Acronym, ParentId = match.ParentId, Uei = match.Uei,
                CageCode = match.CageCode, IsActive = match.IsActive,
            };
            var changed = Changed(("Name", match.Name, merged.Name), ("Type", match.Kind, merged.Kind), ("Website", match.Website, merged.Website), ("Phone", match.Phone, merged.Phone),
                ("Address", match.Address, merged.Address), ("Notes", match.Notes, merged.Notes));
            if (changed.Count == 0)
            {
                context.Run.Skip(row.FileName, row.Number, "Account", name, "Unchanged");
                continue;
            }

            await context.Run.UpdateAsync(row.FileName, row.Number, typeof(D.CrmAccount), match.Id, "Account", name, UpdateSummary(changed),
                () => accounts.UpdateAccountAsync(match.Id, merged, cancellationToken), cancellationToken);
        }
    }
}

/// <summary>Contacts → contacts on their company's account.</summary>
public sealed class CommercialContactImporter(ImportSource source) : CommercialImporter(source)
{
    public override ImportKind Kind => ImportKind.CrmContacts;
    public override string Title => "Contacts";
    public override string Description => "The people at your companies. Their company must already be an account (import companies first). Contacts already in Workbench (same email, or same name at the same company) are listed as duplicates and left alone.";
    public override string? SampleFile => Flavor switch { ImportSource.HubSpot => "samples/import/hubspot/contacts.csv", ImportSource.Salesforce => "samples/import/salesforce/contacts.csv", _ => "samples/import/generic/contacts.csv" };

    public override IReadOnlyList<ImportField> Fields =>
    [
        new("Company", "Company", true, "The account the contact belongs to, by name.",
            Aliases(["Associated Company", "Primary Associated Company", "Company name", "Company"], ["Account Name", "Account.Name"], ["Company", "Company Name", "Account", "Organization", "Client", "Customer"])),
        new("FirstName", "First name", false, "Or map Full name.", Aliases(["First name"], ["FirstName", "First Name"], ["First", "Given Name"])),
        new("LastName", "Last name", false, "", Aliases(["Last name"], ["LastName", "Last Name"], ["Last", "Surname", "Family Name"])),
        new("FullName", "Full name", false, "\"First Last\" or \"Last, First\" when there are no separate name columns.", Aliases(["Name"], ["Full Name", "Name"], ["Contact", "Contact Name", "Full Name", "Name"])),
        new("Title", "Job title", false, "Decides the contact's role: executives, technical leads, buyers and so on.", Aliases(["Job title"], ["Title"], ["Job Title", "Position", "Role"])),
        new("Email", "Email", false, "Matched to existing contacts by email, then by company and name.", Aliases(["Email"], ["Email"], ["E-mail", "Email Address", "Work Email"])),
        new("Phone", "Phone", false, "", Aliases(["Phone number", "Mobile phone number"], ["Phone", "MobilePhone", "Mobile"], ["Work Phone", "Office Phone", "Mobile", "Mobile Phone", "Telephone"])),
        new("Notes", "Notes", false, "", Aliases(["Notes"], ["Description", "Department"], ["Comments", "Notes", "Description"])),
    ];

    internal static ContactRole ParseRole(string? title)
    {
        var t = Key(title);
        return t switch
        {
            "" => ContactRole.Other,
            _ when t.Contains("chief") || t.Contains("founder") || t.Contains("president") || t.Contains("owner") || t.Contains("director") || t.Contains("head") || t.StartsWith("vp") || t.Contains("vicepresident") || t is "ceo" or "cfo" or "coo" => ContactRole.Executive,
            _ when t.Contains("procure") || t.Contains("purchas") || t.Contains("buyer") || t.Contains("vendor") || t.Contains("sourcing") => ContactRole.ContractingSpecialist,
            _ when t.Contains("cto") || t.Contains("engineer") || t.Contains("architect") || t.Contains("technical") || t.Contains("developer") || t.StartsWith("it") => ContactRole.TechnicalLead,
            _ when t.Contains("sales") || t.Contains("businessdevelopment") || t.Contains("accountexec") || t.Contains("partnership") => ContactRole.BusinessDevelopment,
            _ when t.Contains("manager") || t.Contains("project") || t.Contains("program") || t.Contains("lead") => ContactRole.ProgramManager,
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
            if (!seen.Add(Key(email ?? $"{companyName}|{first}|{last}")))
            {
                row.Error("Email", $"{first} {last} appears more than once in the file.");
            }

            if (row.HasErrors || account is null)
            {
                continue;
            }

            var label = $"{first} {last}".Trim();
            var request = new SaveCrmContactRequest(account.Id, first ?? "", last ?? "", ParseRole(row.Get("Title")), row.Get("Title"), email, row.Get("Phone"), row.Get("Notes"));
            CrmContactDto? match = null;
            string? matchedOn = null;
            if (email is not null && byEmail.TryGetValue(email, out var e))
            {
                (match, matchedOn) = (e, "email");
            }
            else if (byPerson.TryGetValue((account.Id, Key(first + last)), out var p))
            {
                (match, matchedOn) = (p, "name and company");
            }

            if (match is null)
            {
                await context.Run.CreateAsync(row.FileName, row.Number, typeof(D.CrmContact), "Contact", label, $"{label} at {account.Name}",
                    async () => (await accounts.CreateContactAsync(request, cancellationToken)).Id, cancellationToken);
                continue;
            }

            if (!Update(context))
            {
                Duplicate(context, row, "Contact", label, matchedOn!);
                continue;
            }

            var merged = request with
            {
                Title = request.Title ?? match.Title, Email = request.Email ?? match.Email, Phone = request.Phone ?? match.Phone, Notes = request.Notes ?? match.Notes,
                Role = row.Get("Title") is null ? match.Role : request.Role, IsActive = match.IsActive,
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
/// Deals → the pipeline: stage, estimated value (the deal amount), win probability, expected or actual close date, owner and, for closed
/// deals, the outcome. Business development or administrators, like the Capture Exec import: imported stages are history, so they don't
/// go through the go/no-go gates and don't notify anyone.
/// </summary>
public sealed class CommercialDealImporter(ImportSource source) : CommercialImporter(source)
{
    public override ImportKind Kind => ImportKind.Opportunities;
    public override string Title => Flavor == ImportSource.Salesforce ? "Opportunities" : "Deals (opportunities)";
    public override string Description => "Open and closed deals with their stage, amount, probability, close date and owner. Deals already in Workbench (same ID from the source system, or same name at the same company) are listed as duplicates and left alone.";
    public override string? SampleFile => Flavor switch { ImportSource.HubSpot => "samples/import/hubspot/deals.csv", ImportSource.Salesforce => "samples/import/salesforce/opportunities.csv", _ => "samples/import/generic/deals.csv" };

    public override IReadOnlyList<ImportField> Fields =>
    [
        new("Id", $"{(Flavor == ImportSource.Salesforce ? "Salesforce Id" : Flavor == ImportSource.HubSpot ? "HubSpot Record ID" : "Deal ID")}", false, "The source system's own id: importing again matches on it.",
            Aliases(["Record ID", "Deal ID"], ["Id", "Opportunity ID", "Opportunity Id", "18 Digit Opportunity ID"], ["ID", "Deal ID", "Opportunity ID", "Reference"])),
        new("Name", "Deal name", true, "",
            Aliases(["Deal name"], ["Name", "Opportunity Name"], ["Deal", "Deal Name", "Opportunity", "Opportunity Name", "Title", "Project"])),
        new("Company", "Company", false, "By name; a deal whose company isn't an account yet keeps the name as text.",
            Aliases(["Associated Company", "Primary Associated Company", "Company name", "Company"], ["Account Name", "Account.Name"], ["Company", "Company Name", "Account", "Client", "Customer", "Organization"])),
        new("Amount", "Amount (estimated value)", false, "Becomes the deal's estimated value.",
            Aliases(["Amount", "Deal amount"], ["Amount", "Amount (converted)"], ["Value", "Deal Value", "Estimated Value", "Est. Value", "Revenue", "Total Value", "Contract Value"])),
        new("Stage", "Stage", false, "Translated to Workbench's pipeline stages (see the guide). Defaults to Identified.",
            Aliases(["Deal stage"], ["StageName", "Stage"], ["Stage", "Status", "Pipeline Stage", "Sales Stage", "Phase"])),
        new("Probability", "Win probability", false, "0–100 (or 0–1). Also used to place a custom stage when its name isn't recognized.",
            Aliases(["Deal probability", "Win probability"], ["Probability", "Probability (%)"], ["Win Probability", "Probability of Win", "Pwin", "Chance"])),
        new("Closed", "Closed", false, "Salesforce's IsClosed: Y/N.", Aliases([], ["IsClosed", "Closed"], ["Is Closed"])),
        new("Won", "Won", false, "Salesforce's IsWon: Y/N. Decides won or lost for a closed deal whose stage isn't clear.", Aliases([], ["IsWon", "Won"], ["Is Won"])),
        new("CloseDate", "Close date", false, "Expected for open deals, actual for won and lost ones.",
            Aliases(["Close date", "Expected close date"], ["CloseDate", "Close Date"], ["Expected Close", "Expected Close Date", "Close", "Closing Date", "Close By"])),
        new("Owner", "Owner", false, "Email, or the name of a Workbench user; becomes the deal's lead.",
            Aliases(["Deal owner"], ["OwnerId", "Opportunity Owner", "Owner"], ["Owner", "Sales Rep", "Account Manager", "Lead"])),
        new("Type", "New or existing business", false, "Existing business, Upgrade, Renewal or Repeat read as repeat work; anything else is new.",
            Aliases(["Deal type"], ["Type", "Opportunity Type"], ["Business Type", "New/Existing", "Deal Type"])),
        new("Description", "Description", false, "", Aliases(["Description"], ["Description"], ["Notes", "Summary", "Comments"])),
        new("NextStep", "Next step", false, "Added to the description.", Aliases(["Next step"], ["NextStep", "Next Step"], ["Next Action"])),
        new("LeadSource", "Lead source", false, "Added to the description.", Aliases(["Original Source Type", "Lead source"], ["LeadSource", "Lead Source"], ["Source"])),
        new("LossReason", "Win/loss reason", false, "Price, Technical, Customer relationship, Incumbency, Timing and so on.",
            Aliases(["Closed lost reason", "Closed won reason"], ["Loss Reason", "Reason"], ["Win/Loss Reason", "Loss Reason", "Outcome Reason"])),
    ];

    protected override async Task ProcessAsync(ImportContext context, IReadOnlyList<MappedRow> rows, CancellationToken cancellationToken)
    {
        var access = Service<CrmAccess>(context);
        if (!(await access.RolesAsync(cancellationToken)).Has(Permission.CaptureManage))
        {
            context.Run.Error(rows[0].FileName, 0, null, "Only business development and administrators can import deals. Ask one of them to run this import.");
            return;
        }

        var factory = Service<IWorkbenchDbContextFactory>(context);
        var audit = Service<AuditRecorder>(context);
        var clock = Service<TimeProvider>(context);
        var dbSource = Flavor switch { ImportSource.HubSpot => D.OpportunitySource.HubSpot, ImportSource.Salesforce => D.OpportunitySource.Salesforce, _ => D.OpportunitySource.CsvImport };
        Dictionary<string, Guid> companies;
        Dictionary<string, string> people;
        Dictionary<string, D.Opportunity> bySourceId;
        Dictionary<string, D.Opportunity> byNameAndCompany;
        await using (var db = await factory.CreateAsync(cancellationToken))
        {
            companies = [];
            foreach (var a in await db.Set<D.CrmAccount>().AsNoTracking().Where(a => a.Kind != D.CrmAccountKind.Competitor && a.Kind != D.CrmAccountKind.Partner).ToListAsync(cancellationToken))
            {
                companies.TryAdd(Key(a.Name), a.Id);
            }

            people = [];
            foreach (var u in await db.Set<AppUser>().AsNoTracking().Select(u => new { u.Email, u.DisplayName }).ToListAsync(cancellationToken))
            {
                people.TryAdd(Key(u.DisplayName), u.Email);
                people.TryAdd(Key(u.Email), u.Email);
            }

            var all = await db.Set<D.Opportunity>().AsNoTracking().ToListAsync(cancellationToken);
            bySourceId = all.Where(o => o.Source == dbSource && o.SourceId != null).GroupBy(o => o.SourceId!).ToDictionary(g => g.Key, g => g.First());
            byNameAndCompany = all.GroupBy(o => NameKey(o.Name, o.AgencyAccountId, o.AgencyName)).ToDictionary(g => g.Key, g => g.First());
        }

        string? Person(MappedRow row)
        {
            var text = row.Get("Owner");
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

            row.Warning("Owner", $"{text} isn't a Workbench user; the deal's lead is left empty. Add them under People, then import again.");
            return null;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>();
        foreach (var row in rows)
        {
            var sourceId = row.Get("Id");
            var name = row.Required("Name");
            var companyText = row.Get("Company");
            var amount = row.Decimal("Amount") ?? 0m;
            if (amount < 0)
            {
                row.Error("Amount", "The amount can't be negative.");
            }

            var probability = CaptureExecOpportunityImporter.Percent(row, "Probability");
            var closeDate = row.Date("CloseDate");
            var closed = row.Bool("Closed");
            var won = row.Bool("Won");

            var stageText = row.Get("Stage");
            var stage = CommercialStages.Parse(stageText);
            if (stageText is not null && stage is null)
            {
                if (probability is { } p)
                {
                    stage = CommercialStages.FromProbability(p);
                    row.Warning("Stage", $"Stage '{stageText}' isn't one Workbench knows; placed at {OpportunityStages.Label(stage.Value)} from its {p}% probability.");
                }
                else
                {
                    row.Error("Stage", $"Stage '{stageText}' isn't one Workbench knows. Rename it in the file to a standard stage (for example Qualified, Proposal, Closed won) or map a probability column so it can be placed.");
                }
            }

            // Salesforce's flags settle a closed deal whose stage is a custom name.
            if (closed == true && stage is not (OpportunityStage.Won or OpportunityStage.Lost or OpportunityStage.NoBid))
            {
                stage = won == true ? OpportunityStage.Won : OpportunityStage.Lost;
            }

            if (name is not null)
            {
                var key = sourceId is not null ? $"id|{sourceId}" : NameKey(name, companyText is not null && companies.TryGetValue(Key(companyText), out var cid) ? cid : null, companyText);
                if (!seenIds.Add(key))
                {
                    row.Error(sourceId is not null ? "Id" : "Name", sourceId is not null ? $"{sourceId} appears more than once in the file." : $"{name} for {companyText ?? "no company"} appears more than once in the file.");
                }
            }

            if (row.HasErrors || name is null)
            {
                continue;
            }

            Guid? companyId = companyText is not null && companies.TryGetValue(Key(companyText), out var found) ? found : null;
            if (companyText is not null && companyId is null)
            {
                row.Warning("Company", $"{companyText} isn't an account yet; the deal keeps the name as text. Import companies first to link it.");
            }

            var existingType = Key(row.Get("Type"));
            var kind = existingType.Contains("existing") || existingType.Contains("renewal") || existingType.Contains("upgrade") || existingType.Contains("repeat") || existingType.Contains("recurring")
                ? OpportunityKind.Recompete : OpportunityKind.New;
            var description = string.Join("\n", new[] { row.Get("Description"), row.Get("NextStep") is { } next ? $"Next step: {next}" : null, row.Get("LeadSource") is { } lead ? $"Lead source: {lead}" : null }.OfType<string>());
            var effective = stage ?? OpportunityStage.Identified;
            var closedDeal = effective >= OpportunityStage.Won;
            var pwin = effective == OpportunityStage.Won ? 100 : closedDeal ? 0 : probability ?? DefaultPwin(effective);
            var request = new SaveOpportunityRequest(
                name, description.Length == 0 ? null : description, null, companyId, companyId is null ? companyText : null, null, SetAside.None, null, null, kind, amount,
                closeDate, null, null, null, null, null, null, null, Person(row), null, pwin, 100, null, null, null);
            var outcome = new CaptureExecOpportunityImporter.Outcome(effective, CaptureExecOpportunityImporter.ParseReason(row.Get("LossReason")), null,
                effective == OpportunityStage.Won ? amount : null, closeDate);

            D.Opportunity? existing = null;
            string? matchedOn = null;
            if (sourceId is not null && bySourceId.TryGetValue(sourceId, out var byId))
            {
                (existing, matchedOn) = (byId, $"{System} ID");
            }
            else if (byNameAndCompany.TryGetValue(NameKey(name, companyId, companyText), out var byName))
            {
                (existing, matchedOn) = (byName, "deal name and company");
            }

            if (existing is null)
            {
                var label = sourceId is null ? name : $"{sourceId} {name}";
                await context.Run.CreateAsync(row.FileName, row.Number, typeof(D.Opportunity), "Deal", label,
                    $"{name} · {OpportunityStages.Label(effective)}{(amount > 0 ? $" · {amount:C0}" : "")}",
                    () => CaptureExecOpportunityImporter.SaveAsync(context, factory, audit, clock, null, request, outcome, sourceId, cancellationToken, dbSource, System), cancellationToken);
                continue;
            }

            if (!Update(context))
            {
                Duplicate(context, row, "Deal", $"{existing.Number} {name}", matchedOn!);
                continue;
            }

            // Blank cells keep what Workbench has.
            var merged = request with
            {
                Pwin = probability is null && !closedDeal ? existing.Pwin : request.Pwin,
                CaptureManagerEmail = request.CaptureManagerEmail ?? existing.CaptureManagerEmail,
                EstimatedValue = row.IsMapped("Amount") && row.Get("Amount") is not null ? amount : existing.EstimatedValue,
                EstimatedAwardDate = closeDate ?? existing.EstimatedAwardDate,
                Description = request.Description ?? existing.Description,
                AgencyAccountId = request.AgencyAccountId ?? existing.AgencyAccountId,
                AgencyName = request.AgencyAccountId is null && request.AgencyName is null ? existing.AgencyName : request.AgencyName,
                Kind = row.Get("Type") is null ? existing.Kind.ToContract() : request.Kind,
            };
            var targetStage = stageText is null && closed is null ? existing.Stage.ToContract() : effective;
            var changed = Changed(("Name", existing.Name, merged.Name), ("Amount", existing.EstimatedValue, merged.EstimatedValue), ("Stage", existing.Stage.ToContract(), targetStage),
                ("Probability", existing.Pwin, merged.Pwin), ("Close date", existing.EstimatedAwardDate, merged.EstimatedAwardDate),
                ("Company", existing.AgencyAccountId?.ToString() ?? existing.AgencyName, merged.AgencyAccountId?.ToString() ?? merged.AgencyName),
                ("Owner", existing.CaptureManagerEmail, merged.CaptureManagerEmail), ("Description", existing.Description, merged.Description),
                ("Type", existing.Kind.ToContract(), merged.Kind), (System + " ID", existing.SourceId, sourceId ?? existing.SourceId));
            if (changed.Count == 0)
            {
                context.Run.Skip(row.FileName, row.Number, "Deal", $"{existing.Number} {name}", "Unchanged");
                continue;
            }

            await context.Run.UpdateAsync(row.FileName, row.Number, typeof(D.Opportunity), existing.Id, "Deal", $"{existing.Number} {name}", UpdateSummary(changed),
                () => CaptureExecOpportunityImporter.SaveAsync(context, factory, audit, clock, existing.Id, merged, outcome with { Stage = targetStage }, sourceId, cancellationToken, dbSource, System),
                cancellationToken);
        }
    }

    private static int DefaultPwin(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Qualified => 25,
        OpportunityStage.Capture => 50,
        OpportunityStage.Proposal => 70,
        OpportunityStage.Submitted => 85,
        _ => 10,
    };

    private static string NameKey(string name, Guid? companyId, string? companyText) => $"{Key(name)}|{(companyId is { } id ? id.ToString() : Key(companyText))}";
}
