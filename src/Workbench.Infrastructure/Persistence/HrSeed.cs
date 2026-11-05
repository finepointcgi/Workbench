using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Accounting;
using Workbench.Domain.Hr;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for HR: Harper Reed (hr@example.com, HR role) joins Operations; positions for every seeded job
/// (one Systems Engineer II seat open); job titles, a few EEO self-identifications (Val is a protected veteran); Alex's
/// Secret clearance reinvestigation and Dana's CISSP expire within 60 days; medical (PPO and HDHP), dental, vision, life,
/// 401(k), FSA and HSA plans mapped to 6030; open enrollment from today for four weeks (coverage next January 1);
/// current elections and dependents; Val's marriage waiting for HR to verify; standard onboarding and offboarding
/// templates, and Morgan Blake starting in a week with an onboarding checklist. Runs once, after the other seeds.
/// </summary>
internal static class HrSeed
{
    public const string HarperEmail = "hr@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Position>().AnyAsync(cancellationToken)
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken) is not { } pat)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        const string by = "seed@workbench.local";
        var units = await db.Set<OrgUnit>().ToDictionaryAsync(u => u.Code, cancellationToken);
        var categories = await db.Set<LaborCategory>().ToDictionaryAsync(c => c.Code, cancellationToken);
        Guid? Unit(string code) => units.TryGetValue(code, out var u) ? u.Id : null;
        Guid? Category(string code) => categories.TryGetValue(code, out var c) ? c.Id : null;

        var harper = new Employee
        {
            EmployeeNumber = "E1006", Email = HarperEmail, DisplayName = "Harper Reed", OrgUnitId = Unit("OPS"), SupervisorId = pat.Id,
            DefaultLaborCategoryId = Category("ADM1"), HireDate = pat.HireDate,
        };
        var morgan = new Employee
        {
            EmployeeNumber = "E1007", Email = "morgan.blake@example.com", DisplayName = "Morgan Blake", OrgUnitId = Unit("ENG"), SupervisorId = pat.Id,
            DefaultLaborCategoryId = Category("SE2"), HireDate = today.AddDays(7),
        };
        db.AddRange(harper, morgan);
        db.Add(new EmployeeCostRate { EmployeeId = harper.Id, EffectiveFrom = harper.HireDate, HourlyCost = 52m });
        db.Add(new EmployeeCostRate { EmployeeId = morgan.Id, EffectiveFrom = morgan.HireDate, HourlyCost = 61m });
        if (await db.Set<PayGroup>().FirstOrDefaultAsync(cancellationToken) is { } group)
        {
            db.Add(new EmployeePayProfile { EmployeeId = harper.Id, PayGroupId = group.Id, ProviderEmployeeId = "1006", PayType = PayType.Salaried, AnnualSalary = 88_000m, EffectiveFrom = harper.HireDate, UpdatedAtUtc = now, UpdatedBy = by });
            db.Add(new EmployeePayRate { EmployeeId = harper.Id, EffectiveFrom = harper.HireDate, PayType = PayType.Salaried, AnnualSalary = 88_000m, Reason = "Seeded pay rate", CreatedBy = by, CreatedAtUtc = now });
        }

        Position Position(string code, string title, string? category, string unit, EeoJobCategory eeo, decimal min, decimal max, int headcount = 1, bool exempt = true,
            EmploymentType type = EmploymentType.FullTime) => new()
        {
            Code = code, Title = title, LaborCategoryId = Category(category ?? ""), OrgUnitId = Unit(unit), EeoJobCategory = eeo, SalaryMin = min, SalaryMax = max,
            Headcount = headcount, IsExempt = exempt, EmploymentType = type, UpdatedAtUtc = now, UpdatedBy = by,
        };
        var pm = Position("OPS-PM3-01", "Program Manager III", "PM3", "OPS", EeoJobCategory.FirstMidOfficials, 150_000m, 195_000m);
        var se = Position("ENG-SE2-01", "Systems Engineer II", "SE2", "ENG", EeoJobCategory.Professionals, 110_000m, 145_000m, headcount: 2);
        var ba = Position("ENG-BA2-01", "Business Analyst II", "BA2", "ENG", EeoJobCategory.Professionals, 85_000m, 115_000m);
        var adm = Position("OPS-ADM1-01", "Administrative Assistant I", "ADM1", "OPS", EeoJobCategory.AdministrativeSupport, 45_000m, 62_000m, exempt: false, type: EmploymentType.PartTime);
        var hr = Position("OPS-HR-01", "HR Generalist", "ADM1", "OPS", EeoJobCategory.Professionals, 75_000m, 98_000m);
        se.ReportsToPositionId = pm.Id;
        ba.ReportsToPositionId = pm.Id;
        adm.ReportsToPositionId = pm.Id;
        hr.ReportsToPositionId = pm.Id;
        db.AddRange(pm, se, ba, adm, hr);

        var people = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        people[HarperEmail] = harper;
        people[morgan.Email] = morgan;
        void Profile(string email, string title, Position position, string location, EmploymentType type = EmploymentType.FullTime, EmploymentStatus status = EmploymentStatus.Active)
        {
            if (people.TryGetValue(email, out var e))
            {
                db.Add(new EmployeeHrProfile
                {
                    EmployeeId = e.Id, JobTitle = title, PositionId = position.Id, EmploymentType = type, Status = status, WorkLocation = location, WorkState = "VA",
                    UpdatedAtUtc = now, UpdatedBy = by,
                });
            }
        }

        Profile("pm@example.com", "Program Manager", pm, "Reston, VA");
        Profile("analyst@example.com", "Business Analyst", ba, "Reston, VA");
        Profile("dev@workbench.local", "Systems Engineer", se, "Remote");
        Profile("viewer@example.com", "Program Assistant", adm, "Reston, VA", EmploymentType.PartTime);
        Profile(HarperEmail, "HR Generalist", hr, "Reston, VA");
        Profile(morgan.Email, "Systems Engineer", se, "Reston, VA", status: EmploymentStatus.PendingStart);
        if (people.TryGetValue(ProjectManagerSeed.SamEmail, out var sam))
        {
            db.Add(new EmployeeHrProfile { EmployeeId = sam.Id, JobTitle = "Project Manager", WorkLocation = "Reston, VA", WorkState = "VA", UpdatedAtUtc = now, UpdatedBy = by });
        }

        void Eeo(string email, EeoGender gender, EeoRace race, bool veteran = false, bool protectedVeteran = false)
        {
            if (people.TryGetValue(email, out var e))
            {
                db.Add(new EmployeeEeoRecord
                {
                    EmployeeId = e.Id, Gender = gender, Race = race, Disability = DisabilityStatus.No, IsVeteran = veteran, ArmedForcesServiceMedalVeteran = protectedVeteran,
                    UpdatedAtUtc = now, UpdatedBy = by,
                });
            }
        }

        Eeo("pm@example.com", EeoGender.Female, EeoRace.White);
        Eeo("analyst@example.com", EeoGender.Male, EeoRace.HispanicOrLatino);
        Eeo("dev@workbench.local", EeoGender.Female, EeoRace.Asian);
        Eeo("viewer@example.com", EeoGender.Female, EeoRace.BlackOrAfricanAmerican, veteran: true, protectedVeteran: true);

        void Credential(string email, CredentialKind kind, string name, string? issuer, DateOnly? issued, DateOnly? expires, ClearanceLevel? level = null)
        {
            if (people.TryGetValue(email, out var e))
            {
                db.Add(new EmployeeCredential { EmployeeId = e.Id, Kind = kind, Name = name, Issuer = issuer, ClearanceLevel = level, IssuedOn = issued, ExpiresOn = expires, UpdatedAtUtc = now, UpdatedBy = by });
            }
        }

        Credential("analyst@example.com", CredentialKind.SecurityClearance, "Secret", "DCSA", today.AddYears(-10).AddDays(45), today.AddDays(45), ClearanceLevel.Secret);
        Credential("analyst@example.com", CredentialKind.Education, "B.S. Information Systems", "George Mason University", new DateOnly(2016, 5, 20), null);
        Credential("dev@workbench.local", CredentialKind.Certification, "CISSP", "ISC2", today.AddYears(-3).AddDays(20), today.AddDays(20));
        Credential("pm@example.com", CredentialKind.SecurityClearance, "Top Secret", "DCSA", today.AddYears(-3), today.AddYears(2), ClearanceLevel.TopSecret);
        Credential("pm@example.com", CredentialKind.Certification, "PMP", "PMI", today.AddYears(-1), today.AddYears(2));

        if (people.TryGetValue("analyst@example.com", out var alex))
        {
            db.Add(new EmergencyContact { EmployeeId = alex.Id, Name = "Jordan Rivera", Relationship = "Spouse", Phone = "703-555-0142", IsPrimary = true });
        }

        // Benefits: plans through the end of next year so current and open-enrollment elections both fit.
        var benefitsAccount = await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == "6030", cancellationToken);
        var planStart = new DateOnly(today.Year, 1, 1);
        var planEnd = new DateOnly(today.Year + 1, 12, 31);
        BenefitPlan Plan(string code, string name, BenefitPlanType type, string deduction, string? carrier, params (CoverageLevel Level, decimal Ee, decimal Er)[] tiers)
        {
            var plan = new BenefitPlan
            {
                Code = code, Name = name, Type = type, DeductionCode = deduction, Carrier = carrier, StartsOn = planStart, EndsOn = planEnd,
                ExpenseAccountId = benefitsAccount?.Id, UpdatedAtUtc = now, UpdatedBy = by,
            };
            plan.Tiers.AddRange(tiers.Select(t => new BenefitTier { PlanId = plan.Id, Coverage = t.Level, EmployeeMonthlyCost = t.Ee, EmployerMonthlyCost = t.Er }));
            db.Add(plan);
            return plan;
        }

        var ppo = Plan("MED-PPO", "Medical PPO", BenefitPlanType.Medical, "MEDPPO", "BlueCross",
            (CoverageLevel.EmployeeOnly, 145m, 610m), (CoverageLevel.EmployeeSpouse, 360m, 1_120m), (CoverageLevel.EmployeeChildren, 300m, 1_000m), (CoverageLevel.Family, 520m, 1_560m));
        var hdhp = Plan("MED-HDHP", "Medical HDHP with HSA", BenefitPlanType.Medical, "MEDHD", "BlueCross",
            (CoverageLevel.EmployeeOnly, 60m, 540m), (CoverageLevel.EmployeeSpouse, 180m, 980m), (CoverageLevel.EmployeeChildren, 150m, 880m), (CoverageLevel.Family, 260m, 1_380m));
        hdhp.IsHighDeductible = true;
        var dental = Plan("DEN", "Dental", BenefitPlanType.Dental, "DEN", "Delta Dental",
            (CoverageLevel.EmployeeOnly, 12m, 28m), (CoverageLevel.EmployeeSpouse, 26m, 52m), (CoverageLevel.EmployeeChildren, 24m, 48m), (CoverageLevel.Family, 38m, 74m));
        var vision = Plan("VIS", "Vision", BenefitPlanType.Vision, "VIS", "VSP",
            (CoverageLevel.EmployeeOnly, 4m, 8m), (CoverageLevel.EmployeeSpouse, 8m, 14m), (CoverageLevel.EmployeeChildren, 8m, 13m), (CoverageLevel.Family, 12m, 20m));
        var life = Plan("LIFE", "Basic life and AD&D (2× salary)", BenefitPlanType.Life, "LIFE", "MetLife", (CoverageLevel.EmployeeOnly, 0m, 22m));
        life.FullTimeOnly = true;
        var k401 = Plan("401K", "401(k) retirement plan", BenefitPlanType.Retirement401k, "401K", "Fidelity");
        k401.AnnualElectionLimit = 75m;
        k401.EmployerMatchRate = 1m;
        k401.EmployerMatchCapPercent = 0.04m;
        k401.FullTimeOnly = false;
        var fsa = Plan("FSA", "Health care FSA", BenefitPlanType.Fsa, "FSA", "WageWorks");
        fsa.AnnualElectionLimit = 3_300m;
        var hsa = Plan("HSA", "Health savings account", BenefitPlanType.Hsa, "HSA", "HealthEquity");
        hsa.AnnualElectionLimit = 4_300m;
        hsa.EmployerMonthlyContribution = 50m;

        db.Add(new OpenEnrollmentWindow
        {
            Name = $"Open enrollment {today.Year + 1}", OpensOn = today, ClosesOn = today.AddDays(27), CoverageStartsOn = new DateOnly(today.Year + 1, 1, 1),
            CreatedBy = by, CreatedAtUtc = now,
        });

        var current = new DateOnly(today.Year, 1, 1);
        BenefitElection Elect(Employee e, BenefitPlan plan, CoverageLevel? level, decimal? amount = null, bool waived = false, params BenefitDependent[] dependents)
        {
            var election = new BenefitElection
            {
                EmployeeId = e.Id, PlanId = plan.Id, PlanType = plan.Type, Coverage = level, TierId = plan.Tiers.FirstOrDefault(t => t.Coverage == level)?.Id,
                ContributionAmount = amount, IsWaived = waived, CoverageStart = current, Source = ElectionSource.OpenEnrollment, ElectedBy = e.Email, ElectedAtUtc = now,
            };
            election.Dependents.AddRange(dependents.Select(d => new BenefitElectionDependent { ElectionId = election.Id, DependentId = d.Id }));
            db.Add(election);
            return election;
        }

        if (people.TryGetValue("analyst@example.com", out var analyst))
        {
            var spouse = new BenefitDependent { EmployeeId = analyst.Id, FirstName = "Jordan", LastName = "Rivera", Relationship = DependentRelationship.Spouse, BirthDate = new DateOnly(1992, 4, 2), UpdatedAtUtc = now };
            var child = new BenefitDependent { EmployeeId = analyst.Id, FirstName = "Sam", LastName = "Rivera", Relationship = DependentRelationship.Child, BirthDate = new DateOnly(2021, 8, 14), UpdatedAtUtc = now };
            db.AddRange(spouse, child);
            Elect(analyst, ppo, CoverageLevel.EmployeeSpouse, dependents: [spouse]);
            Elect(analyst, dental, CoverageLevel.EmployeeSpouse, dependents: [spouse]);
            Elect(analyst, k401, null, amount: 6m);
        }

        Elect(pat, hdhp, CoverageLevel.EmployeeOnly);
        Elect(pat, hsa, null, amount: 3_000m);
        Elect(pat, k401, null, amount: 8m);
        Elect(pat, life, CoverageLevel.EmployeeOnly);
        if (people.TryGetValue("dev@workbench.local", out var dana))
        {
            Elect(dana, ppo, CoverageLevel.EmployeeOnly);
            Elect(dana, vision, CoverageLevel.EmployeeOnly);
            Elect(dana, life, CoverageLevel.EmployeeOnly);
        }

        if (people.TryGetValue("viewer@example.com", out var val))
        {
            Elect(val, ppo, null, waived: true);
            db.Add(new LifeEvent
            {
                EmployeeId = val.Id, Kind = LifeEventKind.Marriage, EventDate = today.AddDays(-10), WindowEndsOn = today.AddDays(20), Note = "Married on the 9th",
                ReportedBy = val.Email, ReportedAtUtc = now.AddDays(-2),
            });
        }

        var onboarding = new ChecklistTemplate { Name = "Standard onboarding", Kind = ChecklistKind.Onboarding, UpdatedAtUtc = now };
        (string Title, ChecklistOwner Owner, int Offset)[] onboardingTasks =
        [
            ("Send the offer letter and new-hire paperwork", ChecklistOwner.Hr, -7),
            ("Create the sign-in, email and laptop", ChecklistOwner.It, -2),
            ("Plan the first week and project assignments", ChecklistOwner.Manager, -1),
            ("Complete Form I-9 section 1 and direct deposit", ChecklistOwner.Employee, 0),
            ("Verify Form I-9 documents (section 2)", ChecklistOwner.Hr, 3),
            ("Submit the clearance transfer or investigation request", ChecklistOwner.Hr, 5),
            ("Timekeeping (DCAA) training", ChecklistOwner.Employee, 5),
            ("30-day check-in", ChecklistOwner.Manager, 30),
        ];
        onboarding.Tasks.AddRange(onboardingTasks.Select((t, i) => new ChecklistTemplateTask { TemplateId = onboarding.Id, Sequence = i + 1, Title = t.Title, Owner = t.Owner, DueOffsetDays = t.Offset }));
        var offboarding = new ChecklistTemplate { Name = "Standard offboarding", Kind = ChecklistKind.Offboarding, UpdatedAtUtc = now };
        (string Title, ChecklistOwner Owner, int Offset)[] offboardingTasks =
        [
            ("Exit interview", ChecklistOwner.Hr, -1),
            ("Hand over work and files", ChecklistOwner.Manager, -3),
            ("Debrief from security clearance (SF-312)", ChecklistOwner.Hr, 0),
        ];
        offboarding.Tasks.AddRange(offboardingTasks.Select((t, i) => new ChecklistTemplateTask { TemplateId = offboarding.Id, Sequence = i + 1, Title = t.Title, Owner = t.Owner, DueOffsetDays = t.Offset }));
        db.AddRange(onboarding, offboarding);

        var checklist = new EmployeeChecklist
        {
            EmployeeId = morgan.Id, TemplateId = onboarding.Id, Kind = ChecklistKind.Onboarding, AnchorDate = morgan.HireDate, CreatedBy = HarperEmail, CreatedAtUtc = now,
        };
        foreach (var t in onboarding.Tasks)
        {
            checklist.Tasks.Add(new ChecklistTask
            {
                ChecklistId = checklist.Id, Sequence = t.Sequence, Title = t.Title, Owner = t.Owner, DueDate = morgan.HireDate.AddDays(t.DueOffsetDays),
                AssigneeEmail = t.Owner switch { ChecklistOwner.Employee => morgan.Email, ChecklistOwner.Manager => pat.Email, _ => null },
                Status = t.Sequence == 1 ? ChecklistTaskStatus.Done : ChecklistTaskStatus.Open,
                CompletedBy = t.Sequence == 1 ? HarperEmail : null, CompletedAtUtc = t.Sequence == 1 ? now : null,
            });
        }

        db.Add(checklist);
        await db.SaveChangesAsync(cancellationToken);
    }
}
