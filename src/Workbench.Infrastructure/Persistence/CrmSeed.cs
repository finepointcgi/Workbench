using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Approvals;
using Workbench.Application.Crm;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Time;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Crm;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for business development, entered through the real services: Blake Diaz (bd@example.com,
/// Business development only) captures a pipeline of 10 opportunities across every stage (moved through the real gates,
/// approved by dev@), with agencies, offices, partners, competitors and contacts. One pursuit decision waits for dev@, Sam
/// (sam.pm@) manages two proposals, a won opportunity waits for Finance's handoff, Blake has draft B&amp;P hours, and a saved
/// SAM.gov search (sample data) has found leads. Runs once, after the project manager seed.
/// </summary>
internal static class CrmSeed
{
    public const string BdEmail = "bd@example.com";
    private const string Admin = "dev@workbench.local";
    private const string Sam = ProjectManagerSeed.SamEmail;

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<Domain.Crm.Opportunity>().AnyAsync(cancellationToken)
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken) is not { } pat)
        {
            return;
        }

        // Blake: the Business development role only.
        var category = await db.Set<LaborCategory>().FirstOrDefaultAsync(c => c.Code == "PM3", cancellationToken);
        var blake = new Employee
        {
            EmployeeNumber = "E1006", Email = BdEmail, DisplayName = "Blake Diaz", OrgUnitId = pat.OrgUnitId, SupervisorId = pat.Id,
            DefaultLaborCategoryId = category?.Id, HireDate = pat.HireDate,
        };
        db.Add(blake);
        db.Add(new EmployeeCostRate { EmployeeId = blake.Id, EffectiveFrom = blake.HireDate, HourlyCost = 84m });
        if (!await db.Set<AppUser>().AnyAsync(u => u.Email == BdEmail, cancellationToken))
        {
            db.Add(new AppUser { Email = BdEmail, DisplayName = "Blake Diaz", IsBusinessDevelopment = true, CreatedAtUtc = DateTime.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);

        var user = new SeedUser { Email = BdEmail };
        var kit = new Kit(services, user);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // ---- Accounts and contacts ----
        async Task<Guid> Account(string name, CrmAccountKind kind, string? acronym = null, Guid? parent = null, string? notes = null) =>
            (await kit.Accounts.CreateAccountAsync(new SaveCrmAccountRequest(name, kind, acronym, parent, Notes: notes), cancellationToken)).Id;

        var dod = await Account("Department of Defense", CrmAccountKind.Agency, "DoD");
        var army = await Account("Department of the Army", CrmAccountKind.Agency, "Army", dod);
        var peoEis = await Account("PEO Enterprise Information Systems", CrmAccountKind.Office, "PEO EIS", army, "Fort Belvoir, VA. Our largest customer (contract W56KGU-26-C-0001).");
        var airForce = await Account("Department of the Air Force", CrmAccountKind.Agency, "USAF", dod);
        var aflcmc = await Account("Air Force Life Cycle Management Center", CrmAccountKind.Office, "AFLCMC", airForce);
        var dhs = await Account("Department of Homeland Security", CrmAccountKind.Agency, "DHS");
        var cisa = await Account("Cybersecurity and Infrastructure Security Agency", CrmAccountKind.Office, "CISA", dhs);
        var hhs = await Account("Department of Health and Human Services", CrmAccountKind.Agency, "HHS");
        var nih = await Account("National Institutes of Health", CrmAccountKind.Office, "NIH", hhs);
        var va = await Account("Department of Veterans Affairs", CrmAccountKind.Agency, "VA");
        var gsa = await Account("General Services Administration", CrmAccountKind.Agency, "GSA");
        var booz = await Account("Booz Allen Hamilton", CrmAccountKind.Competitor, "BAH");
        var caci = await Account("CACI International", CrmAccountKind.Competitor, "CACI");
        var leidos = await Account("Leidos", CrmAccountKind.Prime);
        var acme = await Account("Acme Federal Solutions", CrmAccountKind.Partner, notes: "SDVOSB. Strong Army past performance; teaming agreement on file.");
        var northstar = await Account("Northstar Analytics", CrmAccountKind.Partner, notes: "Data science small business (8(a)).");

        if (await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == "ARMY", cancellationToken) is { } armyCustomer)
        {
            user.Email = Admin;
            await kit.Accounts.LinkCustomerAsync(peoEis, armyCustomer.Id, cancellationToken);
            user.Email = BdEmail;
        }

        async Task<Guid> Contact(Guid account, string first, string last, ContactRole role, string title, string email) =>
            (await kit.Accounts.CreateContactAsync(new SaveCrmContactRequest(account, first, last, role, title, email), cancellationToken)).Id;

        var co = await Contact(peoEis, "Monica", "Reyes", ContactRole.ContractingOfficer, "Contracting Officer, ACC-APG", "monica.reyes@army.example.mil");
        var pmEis = await Contact(peoEis, "Lt. Col. David", "Okafor", ContactRole.ProgramManager, "Product Manager, Enterprise Cloud", "david.okafor@army.example.mil");
        await Contact(cisa, "Priya", "Nair", ContactRole.ProgramManager, "Deputy Director, CSOC", "priya.nair@cisa.example.gov");
        await Contact(nih, "Greg", "Holloway", ContactRole.TechnicalLead, "Chief Data Officer, NCI", "greg.holloway@nih.example.gov");
        await Contact(aflcmc, "Teresa", "Lin", ContactRole.SmallBusinessSpecialist, "Small Business Specialist", "teresa.lin@us.af.example.mil");
        await Contact(acme, "Jordan", "Price", ContactRole.BusinessDevelopment, "VP Business Development", "jordan.price@acmefederal.example.com");

        // ---- Opportunities ----
        async Task<OpportunityDto> Opp(SaveOpportunityRequest r) => await kit.Opportunities.CreateAsync(r, cancellationToken);

        var finance = await Opp(new SaveOpportunityRequest("Army Financial Management Systems Support", "Sustainment of GFEBS interfaces and financial reporting.",
            "W56KGU-26-R-0042", peoEis, NaicsCode: "541611", SetAside: SetAside.SmallBusiness, ContractVehicle: "Standalone", ContractType: ContractKind.TimeAndMaterials,
            Kind: OpportunityKind.Recompete, EstimatedValue: 18_000_000m, EstimatedAwardDate: today.AddMonths(9), PeriodOfPerformanceMonths: 60,
            RfiDueOn: today.AddDays(18), PlaceOfPerformance: "Fort Belvoir, VA", IncumbentAccountId: caci, Pwin: 30, Pgo: 70,
            Competitors: [new SaveCompetitor(caci, "", true, "Incumbent since 2021")]));
        var helpDesk = await Opp(new SaveOpportunityRequest("VA Help Desk Modernization", "Tier 1–2 service desk with ITSM migration.", null, va,
            NaicsCode: "541513", SetAside: SetAside.ServiceDisabledVeteranOwned, ContractVehicle: "T4NG2", ContractType: ContractKind.FirmFixedPrice,
            EstimatedValue: 6_200_000m, EstimatedAwardDate: today.AddMonths(11), PeriodOfPerformanceMonths: 36, Pwin: 20, Pgo: 60,
            Teammates: [new SaveTeammate(acme, "", TeamingRole.Prime, 60m, "Acme primes (SDVOSB); we sub")]));
        var nihData = await Opp(new SaveOpportunityRequest("NIH Data Platform Modernization", "Cloud data lake and analytics for cancer research data.", "75N98027R00011",
            nih, NaicsCode: "541511", ContractVehicle: "CIO-SP4", ContractType: ContractKind.TimeAndMaterials, EstimatedValue: 12_500_000m,
            EstimatedAwardDate: today.AddMonths(7), PeriodOfPerformanceMonths: 60, ProposalDueOn: today.AddDays(60), Pwin: 35, Pgo: 80,
            Teammates: [new SaveTeammate(northstar, "", TeamingRole.Subcontractor, 25m)]));
        var cyber = await Opp(new SaveOpportunityRequest("CISA Cyber Operations Center Support", "24×7 CSOC analysts, threat hunting and incident response.",
            "70RCSA27R00000005", cisa, NaicsCode: "541519", ContractVehicle: "Standalone", ContractType: ContractKind.CostPlusFixedFee, Kind: OpportunityKind.Recompete,
            EstimatedValue: 92_000_000m, EstimatedAwardDate: today.AddMonths(8), PeriodOfPerformanceMonths: 60, RfpReleaseOn: today.AddDays(20),
            ProposalDueOn: today.AddDays(50), PlaceOfPerformance: "Arlington, VA", IncumbentAccountId: booz, Pwin: 25, Pgo: 75,
            Teammates: [new SaveTeammate(acme, "", TeamingRole.Subcontractor, 30m)],
            Competitors: [new SaveCompetitor(booz, "", true), new SaveCompetitor(leidos, "")]));
        var cloud = await Opp(new SaveOpportunityRequest("Army Enterprise Cloud Migration", "Migrate 40 PEO EIS applications to cARMY.", "W91QUZ-27-R-0012", peoEis,
            NaicsCode: "541512", SetAside: SetAside.SmallBusiness, ContractVehicle: "Standalone", ContractType: ContractKind.CostPlusFixedFee, EstimatedValue: 48_000_000m,
            EstimatedAwardDate: today.AddMonths(5), PeriodOfPerformanceMonths: 48, ProposalDueOn: today.AddDays(12), PlaceOfPerformance: "Fort Belvoir, VA",
            ProposalManagerEmail: Sam, Pwin: 40, Pgo: 80, Teammates: [new SaveTeammate(acme, "", TeamingRole.Subcontractor, 20m)],
            Competitors: [new SaveCompetitor(leidos, ""), new SaveCompetitor(booz, "")]));
        var logistics = await Opp(new SaveOpportunityRequest("Air Force Logistics Agile Development", "Agile teams for logistics systems modernization.", "FA8771-27-R-1004",
            aflcmc, NaicsCode: "541511", SetAside: SetAside.SmallBusiness, ContractVehicle: "Standalone", ContractType: ContractKind.TimeAndMaterials, EstimatedValue: 27_000_000m,
            EstimatedAwardDate: today.AddMonths(4), PeriodOfPerformanceMonths: 60, ProposalDueOn: today.AddDays(9), PlaceOfPerformance: "Wright-Patterson AFB, OH",
            ProposalManagerEmail: Sam, Pwin: 45, Pgo: 85, Competitors: [new SaveCompetitor(caci, "")]));
        var analytics = await Opp(new SaveOpportunityRequest("DHS Data Analytics BPA", "Enterprise data analytics blanket purchase agreement.", "70RDAD27Q00000017", dhs,
            NaicsCode: "541511", ContractVehicle: "GSA MAS", ContractType: ContractKind.TimeAndMaterials, EstimatedValue: 15_000_000m,
            EstimatedAwardDate: today.AddMonths(2), PeriodOfPerformanceMonths: 60, ProposalDueOn: today.AddDays(-14), Pwin: 50, Pgo: 90,
            Teammates: [new SaveTeammate(northstar, "", TeamingRole.Subcontractor, 30m)]));
        var training = await Opp(new SaveOpportunityRequest("Army Training Systems Sustainment", "Sustainment of training management systems.", "W56KGU-26-R-0007", peoEis,
            NaicsCode: "541512", SetAside: SetAside.SmallBusiness, ContractType: ContractKind.FirmFixedPrice, EstimatedValue: 9_600_000m,
            EstimatedAwardDate: today.AddDays(-30), PeriodOfPerformanceMonths: 36, ProposalDueOn: today.AddDays(-90), Pwin: 60, Pgo: 90));
        var claims = await Opp(new SaveOpportunityRequest("VA Claims Processing Analytics", "Analytics for benefits claims backlog.", "36C10B26Q0311", va,
            NaicsCode: "541511", ContractType: ContractKind.FirmFixedPrice, EstimatedValue: 7_400_000m, EstimatedAwardDate: today.AddDays(-45),
            PeriodOfPerformanceMonths: 24, ProposalDueOn: today.AddDays(-110), Pwin: 40, Pgo: 90));
        var facilities = await Opp(new SaveOpportunityRequest("GSA Facilities Maintenance, Building 12", null, "47PB0027R0003", gsa, NaicsCode: "561210",
            SetAside: SetAside.HubZone, EstimatedValue: 3_100_000m, EstimatedAwardDate: today.AddMonths(3), Pwin: 5, Pgo: 70));

        // ---- Moves through the gates ----
        var gates = await kit.Gates.ListGatesAsync(cancellationToken: cancellationToken);
        async Task PassAsync(OpportunityDto o, OpportunityStage stage, int score, bool approve = true)
        {
            var gate = gates.First(g => g.ToStage == stage);
            user.Email = BdEmail;
            var review = await kit.Gates.SubmitAsync(o.Id, new SubmitGateReviewRequest(gate.Id, GateRecommendation.Go,
                gate.Criteria.Select(c => new GateScoreInput(c.Id, score)).ToList(), $"{gate.Name} for {o.Number}"), cancellationToken);
            if (review.Status == GateReviewStatus.Pending && approve)
            {
                user.Email = Admin;
                await kit.Gates.ApproveAsync(review.Id, new DecideGateRequest("Go."), cancellationToken);
                user.Email = BdEmail;
            }
        }

        async Task ToAsync(OpportunityDto o, OpportunityStage target)
        {
            foreach (var stage in OpportunityStages.Open.Where(s => s > OpportunityStage.Identified && s <= target))
            {
                if (gates.Any(g => g.ToStage == stage))
                {
                    await PassAsync(o, stage, 4);
                }
                else
                {
                    await kit.Opportunities.ChangeStageAsync(o.Id, new ChangeStageRequest(stage), cancellationToken);
                }
            }
        }

        await ToAsync(nihData, OpportunityStage.Qualified);
        await PassAsync(nihData, OpportunityStage.Capture, 3, approve: false); // Waits for dev@ on the home page and in Approvals.
        await ToAsync(cyber, OpportunityStage.Capture);
        await ToAsync(cloud, OpportunityStage.Capture);
        await ToAsync(logistics, OpportunityStage.Proposal);
        await ToAsync(analytics, OpportunityStage.Submitted);
        await ToAsync(training, OpportunityStage.Submitted);
        await kit.Opportunities.RecordOutcomeAsync(training.Id, new RecordOutcomeRequest(OpportunityStage.Won, OutcomeReason.Technical,
            "Strongest technical score; incumbent transition plan praised.", today.AddDays(-30), 9_450_000m), cancellationToken);
        await ToAsync(claims, OpportunityStage.Submitted);
        await kit.Opportunities.RecordOutcomeAsync(claims.Id, new RecordOutcomeRequest(OpportunityStage.Lost, OutcomeReason.Price,
            "Debrief: technically acceptable, 12% above the awardee.", today.AddDays(-45), 6_900_000m, "Booz Allen Hamilton"), cancellationToken);
        await kit.Opportunities.RecordOutcomeAsync(facilities.Id, new RecordOutcomeRequest(OpportunityStage.NoBid, OutcomeReason.NotAligned,
            "Facilities work is outside our core capabilities."), cancellationToken);

        // ---- Activity and tasks ----
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Meeting, "Industry day: enterprise cloud migration",
            "Customer wants a phased migration with a pilot of 5 apps.", peoEis, pmEis, cloud.Id, today.AddDays(-21)), cancellationToken);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Call, "Questions on the draft RFP schedule", null, peoEis, co, cloud.Id,
            today.AddDays(-6)), cancellationToken);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Task, "Finalize the Acme teaming agreement workshare", null, null, null, cloud.Id,
            DueOn: today.AddDays(3), AssignedTo: BdEmail), cancellationToken);
        await kit.Accounts.LogActivityAsync(new SaveCrmActivityRequest(CrmActivityKind.Task, "Send capability statement to the NIH CDO", null, nih, null, nihData.Id,
            DueOn: today.AddDays(-1), AssignedTo: BdEmail), cancellationToken);

        // ---- B&P time: Blake's current timesheet has draft hours on the cloud migration's B&P code ----
        var bpCode = (await kit.Opportunities.GetAsync(cloud.Id, cancellationToken)).BpWbsElementId;
        if (bpCode is { } wbs && category is not null)
        {
            var period = await new TimekeepingClock(TimeProvider.System, Options.Create(new TimekeepingOptions())).PeriodForAsync(db, today, cancellationToken);
            var now = DateTime.UtcNow;
            var sheet = new Timesheet { EmployeeId = blake.Id, PeriodStart = period.Start, PeriodEnd = period.End, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Add(sheet);
            for (var day = period.Start; day <= today; day = day.AddDays(1))
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    continue;
                }

                db.Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs, LaborCategoryId = category.Id, WorkDate = day, Hours = 6m, Comment = "Capture: solution and pricing", EnteredAtUtc = now, UpdatedAtUtc = now });
                db.Add(new TimeDayRecord { EmployeeId = blake.Id, TimesheetId = sheet.Id, WorkDate = day, FirstEnteredAtUtc = now });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // ---- Leads: a saved SAM.gov search (sample data without an API key), run once ----
        var search = await kit.Leads.CreateSearchAsync(new SaveLeadSearchRequest("Army and DHS IT services", ["5415", "541611"], [], [], null, 30), cancellationToken);
        await kit.Leads.RunSearchAsync(search.Id, cancellationToken);
    }

    private sealed class Kit
    {
        public Kit(IServiceProvider services, SeedUser user)
        {
            var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            var access = new CrmAccess(new AccessControl(factory, user), factory);
            var notifications = new NoNotifications();
            Accounts = new CrmAccountService(factory, access, notifications, clock);
            Opportunities = new OpportunityService(factory, access, audit, notifications, clock);
            Gates = new OpportunityGateService(factory, access, audit, notifications, clock, new ApprovalEngine(clock));
            Leads = new OpportunityLeadService(factory, access, new FakeSamGovClient(), audit, notifications, clock);
        }

        public CrmAccountService Accounts { get; }
        public OpportunityService Opportunities { get; }
        public OpportunityGateService Gates { get; }
        public OpportunityLeadService Leads { get; }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
