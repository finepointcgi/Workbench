using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Hr;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using HrStatus = Workbench.Domain.Hr.EmploymentStatus;
using R = Workbench.Contracts.Payroll.PaylocityRosterFields;

namespace Workbench.Application.Payroll;

/// <summary>One employee row of a Paylocity employee export, read leniently (see <see cref="PaylocityRosterParser"/>).</summary>
public sealed record PaylocityRosterRecord(
    int Line,
    string PaylocityId,
    string FirstName,
    string LastName,
    string Name,
    string? Email,
    DateOnly? HireDate,
    DateOnly? RehireDate,
    DateOnly? TerminationDate,
    bool? TerminatedStatus,
    string? Department,
    string? JobTitle,
    bool? IsExempt,
    EmploymentTypeKind? EmploymentType,
    PayTypeKind? PayType,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a Paylocity employee export (CSV with a header row). Paylocity's employee listing is a report the customer
/// builds, so its columns vary: headers are matched ignoring case, spaces and punctuation, against the aliases below.
/// Only these columns are read; everything else in the file (SSN, address, pay rate, bank data...) is ignored and never
/// stored. Required: an employee id and a name (<c>First Name</c> + <c>Last Name</c>, or <c>Employee Name</c>, which may be
/// "Last, First").
/// <para>
/// Paylocity publishes no fixed employee-export layout (Report Writer exports are customer-defined and its Web Link import
/// guide, docs.paylocity.com/weblink/PDFs/Web_Link_Manual_Imports.pdf, covers imports into Paylocity only), so these
/// aliases are a best guess at common report column names: check a real export in the roster preview.
/// <para>
/// Aliases: <c>Employee Id</c> (Emp Id, Employee Number, Employee Code, Id), <c>First Name</c>, <c>Last Name</c>,
/// <c>Employee Name</c> (Name, Full Name), <c>Email</c> (Work Email, Email Address), <c>Hire Date</c> (Original Hire
/// Date, Start Date), <c>Rehire Date</c> (Latest Hire Date, Last Hire Date), <c>Termination Date</c> (Term Date,
/// Separation Date), <c>Status</c> (Employee Status, Employment Status), <c>Department</c> (Dept, Cost Center, Cost Center 1),
/// <c>Job Title</c> (Title, Position), <c>FLSA Status</c> (FLSA, Exempt), <c>Employment Type</c> (Full Time/Part Time,
/// Employee Type), <c>Pay Type</c> (Salary/Hourly, Rate Type). Dates are MM/dd/yyyy or yyyy-MM-dd.
/// </para>
/// </para>
/// </summary>
public static class PaylocityRosterParser
{
    private static readonly Dictionary<string, string[]> Columns = new()
    {
        ["id"] = ["employeeid", "empid", "employeenumber", "employeecode", "employeeno", "eeid", "employee", "id"],
        ["first"] = ["firstname", "first", "legalfirstname"],
        ["last"] = ["lastname", "last", "legallastname"],
        ["name"] = ["employeename", "name", "fullname", "empname"],
        ["email"] = ["email", "workemail", "emailaddress", "companyemail", "workemailaddress"],
        ["hire"] = ["hiredate", "originalhiredate", "orighiredate", "startdate", "datehired"],
        ["rehire"] = ["rehiredate", "latesthiredate", "lasthiredate", "mostrecenthiredate"],
        ["term"] = ["terminationdate", "termdate", "terminateddate", "separationdate", "dateterminated"],
        ["status"] = ["status", "employeestatus", "employmentstatus"],
        ["dept"] = ["department", "dept", "departmentdescription", "costcenter", "costcenter1", "costcenter1description"],
        ["title"] = ["jobtitle", "title", "position", "positiontitle"],
        ["flsa"] = ["flsastatus", "flsa", "exempt", "exemptstatus", "overtimestatus", "otexempt"],
        ["emptype"] = ["employmenttype", "fulltimeparttime", "fulltimeorparttime", "employeetype", "ftpt"],
        ["paytype"] = ["paytype", "salaryhourly", "salaryorhourly", "ratetype"],
    };

    public static IReadOnlyList<PaylocityRosterRecord> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("The roster file is empty.");
        }

        var rows = PaylocityFormats.ReadCsv(content.TrimStart('﻿')).Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count < 2)
        {
            throw new ArgumentException("The roster needs a header row and at least one employee row.");
        }

        var header = rows[0].Select(Key).ToList();
        var cols = Columns.ToDictionary(c => c.Key, c => c.Value.Select(a => header.IndexOf(a)).FirstOrDefault(i => i >= 0, -1));
        var missing = new List<string>();
        if (cols["id"] < 0)
        {
            missing.Add("Employee Id");
        }

        if (cols["name"] < 0 && (cols["first"] < 0 || cols["last"] < 0))
        {
            missing.Add("First Name and Last Name (or Employee Name)");
        }

        if (missing.Count > 0)
        {
            throw new ArgumentException($"The roster is missing the column(s) {string.Join(", ", missing)}. Export the Paylocity employee list as CSV with a header row.");
        }

        var result = new List<PaylocityRosterRecord>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            string Cell(string key) => cols[key] is var c && c >= 0 && c < row.Count ? row[c].Trim() : "";
            var errors = new List<string>();
            var warnings = new List<string>();

            DateOnly? Date(string key, string label)
            {
                var text = Cell(key);
                if (text.Length == 0)
                {
                    return null;
                }

                if (DateOnly.TryParseExact(text, ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "MM/dd/yy", "M/d/yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    return d;
                }

                errors.Add($"{label} \"{text}\" isn't a date (use MM/dd/yyyy or yyyy-MM-dd).");
                return null;
            }

            var first = Cell("first");
            var last = Cell("last");
            var full = Cell("name");
            if (full.Length > 0 && (first.Length == 0 || last.Length == 0))
            {
                // "Last, First" (Paylocity's report style) or "First Last".
                var comma = full.IndexOf(',');
                if (comma > 0)
                {
                    (last, first) = (full[..comma].Trim(), full[(comma + 1)..].Trim());
                }
                else
                {
                    var space = full.LastIndexOf(' ');
                    (first, last) = space > 0 ? (full[..space].Trim(), full[(space + 1)..].Trim()) : (full, "");
                }
            }

            var display = string.Join(' ', new[] { first, last }.Where(p => p.Length > 0));
            var email = Cell("email").ToLowerInvariant();
            if (email.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(email, out _))
            {
                errors.Add($"\"{email}\" isn't an email address.");
                email = "";
            }

            var status = Cell("status");
            bool? terminatedStatus = status.Length == 0 ? null : status.StartsWith('T') || status.StartsWith("Sep", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Inact", StringComparison.OrdinalIgnoreCase);

            bool? exempt = null;
            var flsa = Key(Cell("flsa"));
            if (flsa.Length > 0)
            {
                exempt = flsa switch
                {
                    "exempt" or "e" or "yes" or "y" or "true" => true,
                    "nonexempt" or "ne" or "no" or "n" or "false" or "nonexemptovertimeeligible" => false,
                    _ => null,
                };
                if (exempt is null)
                {
                    warnings.Add($"FLSA status \"{Cell("flsa")}\" isn't recognized (use Exempt or Non-Exempt); ignored.");
                }
            }

            EmploymentTypeKind? type = null;
            var et = Key(Cell("emptype"));
            if (et.Length > 0)
            {
                type = et switch
                {
                    "fulltime" or "ft" or "regularfulltime" => EmploymentTypeKind.FullTime,
                    "parttime" or "pt" or "regularparttime" => EmploymentTypeKind.PartTime,
                    "temporary" or "temp" => EmploymentTypeKind.Temporary,
                    "seasonal" => EmploymentTypeKind.Seasonal,
                    "intern" => EmploymentTypeKind.Intern,
                    _ => null,
                };
                if (type is null)
                {
                    warnings.Add($"Employment type \"{Cell("emptype")}\" isn't recognized; ignored.");
                }
            }

            PayTypeKind? payType = null;
            var pt = Key(Cell("paytype"));
            if (pt.Length > 0)
            {
                payType = pt switch
                {
                    "salary" or "salaried" or "s" => PayTypeKind.Salaried,
                    "hourly" or "h" => PayTypeKind.Hourly,
                    _ => null,
                };
            }

            var id = Cell("id");
            var hire = Date("hire", "Hire date");
            var rehire = Date("rehire", "Rehire date");
            var term = Date("term", "Termination date");
            if (id.Length == 0)
            {
                if (row.Any(c => c.Trim().Length > 0) && (display.Length > 0))
                {
                    errors.Add("No employee id.");
                }
                else
                {
                    continue; // a totals or blank row
                }
            }

            if (display.Length == 0)
            {
                errors.Add("No name.");
            }

            var title = Cell("title");
            var dept = Cell("dept");
            result.Add(new PaylocityRosterRecord(i + 1, id, first, last, display, email.Length == 0 ? null : email, hire, rehire, term, terminatedStatus,
                dept.Length == 0 ? null : dept, title.Length == 0 ? null : title, exempt, type, payType, errors, warnings));
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("The roster has no employee rows.");
        }

        return result;
    }

    private static string Key(string header) => new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// Imports a Paylocity employee export into the personnel records. Rows are matched on the Paylocity employee id (the
/// payroll profile's provider id), then email, then employee number; a row whose id and email point at different people
/// is an error, never a guess. Fields Workbench owns (<see cref="PaylocityRosterSetting"/>) are never changed on an
/// existing employee; pay rates are never read; terminations and rehires change nothing unless asked for. The file's
/// content is not kept (only its name and checksum, in the audit trail).
/// </summary>
public sealed class PaylocityRosterImporter(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock, SecurityEventLog? securityLog = null)
{
    public const string AuditEntityType = "PaylocityRoster";
    public const string RosterImportedEvent = "hr.paylocity_roster_imported";
    public const int MaxBytes = 5 * 1024 * 1024;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public static IReadOnlyList<string> ParseOwned(string? stored) =>
        (stored ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => R.All.Any(a => a.Key == k)).Distinct().ToList();

    public async Task<PaylocityRosterResultDto> ImportAsync(PaylocityRosterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > MaxBytes)
        {
            throw new ArgumentException(string.IsNullOrWhiteSpace(request.Content) ? "The roster file is empty." : "The roster file is larger than 5 MB.");
        }

        var records = PaylocityRosterParser.Parse(request.Content);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Content))).ToLowerInvariant();
        var today = DateOnly.FromDateTime(UtcNow);
        await using var db = await dbFactory.CreateAsync(ct);
        if (request.PayGroupId is { } gid && !await db.Set<PayGroup>().AnyAsync(g => g.Id == gid && g.ProviderKey == PaylocityPayrollProvider.ProviderKey, ct))
        {
            throw new ArgumentException("Pick one of your Paylocity pay groups.");
        }

        var owned = ParseOwned((await db.Set<PaylocityRosterSetting>().AsNoTracking().FirstOrDefaultAsync(ct))?.OwnedFields);
        var employees = await db.Set<Employee>().ToListAsync(ct);
        var profiles = await db.Set<EmployeePayProfile>().ToListAsync(ct);
        var hrProfiles = await db.Set<EmployeeHrProfile>().ToDictionaryAsync(p => p.EmployeeId, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToListAsync(ct);
        var byId = profiles.Where(p => !string.IsNullOrWhiteSpace(p.ProviderEmployeeId)).GroupBy(p => p.ProviderEmployeeId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var byEmail = employees.GroupBy(e => e.Email.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        var byNumber = employees.GroupBy(e => e.EmployeeNumber.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var employeesById = employees.ToDictionary(e => e.Id);

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmails = new HashSet<string>();
        var rows = new List<PaylocityRosterRowDto>();
        var warnings = new List<string>();
        var touched = new HashSet<Guid>();
        var pending = new List<Action>();

        foreach (var rec in records)
        {
            var errors = rec.Errors.ToList();
            var notices = rec.Warnings.ToList();
            var changes = new List<RosterFieldChange>();
            Employee? employee = null;
            var matchedBy = "none";

            if (rec.PaylocityId.Length > 0 && !seenIds.Add(rec.PaylocityId))
            {
                errors.Add($"Employee id {rec.PaylocityId} appears more than once in the file.");
            }

            if (rec.Email is not null && !seenEmails.Add(rec.Email))
            {
                errors.Add($"Email {rec.Email} appears more than once in the file.");
            }

            if (errors.Count == 0)
            {
                (employee, matchedBy) = Match(rec, byId, byEmail, byNumber, employeesById, errors);
            }

            var terminatedInFile = rec.TerminationDate is not null && rec.TerminationDate <= today || rec.TerminatedStatus == true;
            if (errors.Count > 0)
            {
                rows.Add(Row(rec, RosterAction.Error, employee, matchedBy, changes, errors, notices));
                continue;
            }

            if (employee is null)
            {
                // A new employee.
                if (terminatedInFile)
                {
                    notices.Add("Already terminated in Paylocity: not created.");
                    rows.Add(Row(rec, RosterAction.Skip, null, matchedBy, changes, errors, notices));
                    continue;
                }

                if (rec.Email is null)
                {
                    errors.Add("A new employee needs an email address (the sign-in).");
                }

                if (rec.HireDate is null)
                {
                    errors.Add("A new employee needs a hire date.");
                }

                if (errors.Count > 0)
                {
                    rows.Add(Row(rec, RosterAction.Error, null, matchedBy, changes, errors, notices));
                    continue;
                }

                var org = FindOrg(rec.Department, orgs, notices);
                changes.Add(new RosterFieldChange("Employee", null, $"{rec.Name} <{rec.Email}>, hired {rec.HireDate:yyyy-MM-dd}", true, null));
                if (org is not null)
                {
                    changes.Add(new RosterFieldChange("Org unit", null, org.Name, true, null));
                }

                if (rec.JobTitle is not null)
                {
                    changes.Add(new RosterFieldChange("Job title", null, rec.JobTitle, true, null));
                }

                if (request.PayGroupId is null)
                {
                    notices.Add("No pay group chosen: the Paylocity id isn't linked, so this person's hours can't be exported until it is.");
                }
                else
                {
                    changes.Add(new RosterFieldChange("Paylocity id", null, rec.PaylocityId, true, null));
                }

                if (!request.DryRun)
                {
                    var r = rec;
                    var o = org;
                    pending.Add(() =>
                    {
                        var created = new Employee
                        {
                            EmployeeNumber = r.PaylocityId,
                            Email = r.Email!,
                            DisplayName = r.Name,
                            OrgUnitId = o?.Id,
                            HireDate = r.HireDate!.Value,
                            TerminationDate = r.TerminationDate,
                            IsExempt = r.IsExempt ?? true,
                        };
                        db.Set<Employee>().Add(created);
                        var type = r.EmploymentType ?? EmploymentTypeKind.FullTime;
                        db.Set<EmployeeHrProfile>().Add(new EmployeeHrProfile
                        {
                            EmployeeId = created.Id,
                            JobTitle = Trim(r.JobTitle, 200),
                            EmploymentType = (EmploymentType)(int)type,
                            Status = created.HireDate > today ? HrStatus.PendingStart : HrStatus.Active,
                            UpdatedAtUtc = UtcNow,
                            UpdatedBy = audit.Actor,
                        });
                        if (request.PayGroupId is { } group)
                        {
                            db.Set<EmployeePayProfile>().Add(new EmployeePayProfile
                            {
                                EmployeeId = created.Id,
                                PayGroupId = group,
                                ProviderEmployeeId = r.PaylocityId,
                                PayType = (PayType)(int)(r.PayType ?? (created.IsExempt ? PayTypeKind.Salaried : PayTypeKind.Hourly)),
                                EmploymentType = (EmploymentType)(int)type,
                                EffectiveFrom = created.HireDate,
                                UpdatedAtUtc = UtcNow,
                                UpdatedBy = audit.Actor,
                            });
                        }

                        audit.Record(db, "Employee", created.Id, "Created from Paylocity roster", after: new { created.EmployeeNumber, created.DisplayName, created.Email, created.HireDate }, reason: $"Roster import {request.FileName} ({sha[..12]})");
                    });
                }

                rows.Add(Row(rec, RosterAction.Create, null, matchedBy, changes, errors, notices));
                continue;
            }

            // An existing employee.
            var pay = profiles.FirstOrDefault(p => p.EmployeeId == employee.Id);
            hrProfiles.TryGetValue(employee.Id, out var hr);
            var setters = new List<Action>();

            void Field(string key, string label, string? from, string? to, Action apply)
            {
                if (string.Equals(from ?? "", to ?? "", StringComparison.Ordinal))
                {
                    return;
                }

                if (owned.Contains(key))
                {
                    changes.Add(new RosterFieldChange(label, from, to, false, "Workbench owns this field"));
                    return;
                }

                changes.Add(new RosterFieldChange(label, from, to, true, null));
                setters.Add(apply);
            }

            var e = employee;
            Field(R.Name, "Name", e.DisplayName, rec.Name, () => e.DisplayName = rec.Name);
            if (rec.Email is not null)
            {
                if (byEmail.TryGetValue(rec.Email, out var holders) && holders.Any(h => h.Id != e.Id))
                {
                    errors.Add($"Email {rec.Email} already belongs to {holders.First(h => h.Id != e.Id).DisplayName}.");
                }
                else
                {
                    Field(R.Email, "Email", e.Email, rec.Email, () => e.Email = rec.Email);
                }
            }

            if (rec.HireDate is { } hire)
            {
                Field(R.HireDate, "Hire date", e.HireDate.ToString("yyyy-MM-dd"), hire.ToString("yyyy-MM-dd"), () => e.HireDate = hire);
            }

            if (rec.Department is not null && FindOrg(rec.Department, orgs, notices) is { } org2)
            {
                var current = orgs.FirstOrDefault(o => o.Id == e.OrgUnitId);
                Field(R.OrgUnit, "Org unit", current?.Name, org2.Name, () => e.OrgUnitId = org2.Id);
            }

            if (rec.JobTitle is not null)
            {
                Field(R.JobTitle, "Job title", hr?.JobTitle, rec.JobTitle, () => Hr(e.Id).JobTitle = Trim(rec.JobTitle, 200));
            }

            if (rec.IsExempt is { } exempt)
            {
                Field(R.Exempt, "Exempt (FLSA)", e.IsExempt ? "Exempt" : "Non-exempt", exempt ? "Exempt" : "Non-exempt", () => e.IsExempt = exempt);
            }

            if (rec.EmploymentType is { } type2)
            {
                var from = hr is null ? (pay?.EmploymentType ?? EmploymentType.FullTime).ToString() : hr.EmploymentType.ToString();
                Field(R.EmploymentType, "Employment type", from, type2.ToString(), () =>
                {
                    Hr(e.Id).EmploymentType = (EmploymentType)(int)type2;
                    if (pay is not null)
                    {
                        pay.EmploymentType = (EmploymentType)(int)type2;
                    }
                });
            }

            // The link to Paylocity: the id on the payroll profile.
            if (pay is null)
            {
                if (request.PayGroupId is { } group)
                {
                    changes.Add(new RosterFieldChange("Paylocity id", null, rec.PaylocityId, true, null));
                    setters.Add(() => db.Set<EmployeePayProfile>().Add(new EmployeePayProfile
                    {
                        EmployeeId = e.Id,
                        PayGroupId = group,
                        ProviderEmployeeId = rec.PaylocityId,
                        PayType = (PayType)(int)(rec.PayType ?? (e.IsExempt ? PayTypeKind.Salaried : PayTypeKind.Hourly)),
                        EmploymentType = hr?.EmploymentType ?? EmploymentType.FullTime,
                        EffectiveFrom = e.HireDate,
                        UpdatedAtUtc = UtcNow,
                        UpdatedBy = audit.Actor,
                    }));
                }
                else
                {
                    notices.Add("No payroll profile and no pay group chosen: the Paylocity id isn't linked.");
                }
            }
            else if (string.IsNullOrWhiteSpace(pay.ProviderEmployeeId))
            {
                changes.Add(new RosterFieldChange("Paylocity id", null, rec.PaylocityId, true, null));
                setters.Add(() => pay.ProviderEmployeeId = rec.PaylocityId);
            }

            // Termination and rehire awareness.
            var terminatedHere = e.TerminationDate is { } t && t <= today;
            if (rec.TerminationDate is { } term && e.TerminationDate != term)
            {
                var applied = request.ApplyTerminations;
                changes.Add(new RosterFieldChange("Termination date", e.TerminationDate?.ToString("yyyy-MM-dd"), term.ToString("yyyy-MM-dd"), applied,
                    applied ? null : "Not applied: turn on \"Apply terminations\""));
                notices.Add(terminatedHere
                    ? $"Terminated in Paylocity on {term:MM/dd/yyyy}; Workbench has {e.TerminationDate:MM/dd/yyyy}."
                    : $"Terminated in Paylocity on {term:MM/dd/yyyy}; still active in Workbench.");
                if (applied)
                {
                    setters.Add(() => e.TerminationDate = term);
                }

                var latest = rec.TerminationDate.Value;
                var after = await db.Set<TimeEntry>().AsNoTracking().CountAsync(x => x.WorkDate > latest
                    && db.Set<Timesheet>().Any(s => s.Id == x.TimesheetId && s.EmployeeId == e.Id), ct);
                if (after > 0)
                {
                    notices.Add($"{after} time entr{(after == 1 ? "y" : "ies")} after the termination date: review them before payroll.");
                }
            }
            else if (rec.TerminationDate is null && !terminatedInFile && terminatedHere)
            {
                // Active in Paylocity, terminated in Workbench: a rehire (or a termination entered by mistake).
                var applied = request.ApplyRehires;
                changes.Add(new RosterFieldChange("Termination date", e.TerminationDate?.ToString("yyyy-MM-dd"), null, applied, applied ? null : "Not applied: turn on \"Apply rehires\""));
                notices.Add($"Active in Paylocity but terminated in Workbench on {e.TerminationDate:MM/dd/yyyy}"
                    + (rec.RehireDate is { } rh ? $" (Paylocity rehire date {rh:MM/dd/yyyy})." : ". Rehire or a mistaken termination."));
                if (applied)
                {
                    setters.Add(() => e.TerminationDate = null);
                    if (rec.RehireDate is not null)
                    {
                        notices.Add("Workbench keeps one hire date, so the gap between the two stints counts as employed: review the timesheets for it.");
                    }
                }
            }
            else if (terminatedInFile && e.TerminationDate is null && rec.TerminationDate is null)
            {
                notices.Add("Status is terminated in Paylocity but the file has no termination date: set it by hand.");
            }

            if (errors.Count > 0)
            {
                rows.Add(Row(rec, RosterAction.Error, e, matchedBy, changes, errors, notices));
                continue;
            }

            var appliedChanges = changes.Where(c => c.Applied).ToList();
            if (!request.DryRun && setters.Count > 0)
            {
                var before = new { e.DisplayName, e.Email, e.HireDate, e.OrgUnitId, e.IsExempt, e.TerminationDate };
                var applyNow = setters.ToList();
                var summary = string.Join(", ", appliedChanges.Select(c => c.Field));
                pending.Add(() =>
                {
                    foreach (var s in applyNow)
                    {
                        s();
                    }

                    audit.Record(db, "Employee", e.Id, "Updated from Paylocity roster", before, new { e.DisplayName, e.Email, e.HireDate, e.OrgUnitId, e.IsExempt, e.TerminationDate },
                        $"Roster import {request.FileName} ({sha[..12]}): {summary}");
                });
            }

            touched.Add(e.Id);
            rows.Add(Row(rec, appliedChanges.Count > 0 ? RosterAction.Update : RosterAction.NoChange, e, matchedBy, changes, errors, notices));
        }

        var inFile = rows.Where(r => r.EmployeeId is not null).Select(r => r.EmployeeId!.Value).ToHashSet();
        var linked = profiles.Where(p => !string.IsNullOrWhiteSpace(p.ProviderEmployeeId) && employeesById.ContainsKey(p.EmployeeId) && !inFile.Contains(p.EmployeeId)
                && (p.PayGroupId == request.PayGroupId || request.PayGroupId is null))
            .Select(p => new RosterMissingDto(p.EmployeeId, employeesById[p.EmployeeId].DisplayName, p.ProviderEmployeeId!, employeesById[p.EmployeeId].IsActiveOn(today)))
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        if (linked.Count > 0)
        {
            warnings.Add($"{linked.Count} employee(s) with a Paylocity id aren't in the file (nothing is changed for them).");
        }

        // Hr(...) needs the profile row to exist before it's edited.
        EmployeeHrProfile Hr(Guid employeeId)
        {
            if (!hrProfiles.TryGetValue(employeeId, out var p))
            {
                p = new EmployeeHrProfile { EmployeeId = employeeId, UpdatedAtUtc = UtcNow, UpdatedBy = audit.Actor };
                hrProfiles[employeeId] = p;
                db.Set<EmployeeHrProfile>().Add(p);
            }

            p.UpdatedAtUtc = UtcNow;
            p.UpdatedBy = audit.Actor;
            return p;
        }

        if (!request.DryRun && pending.Count > 0)
        {
            foreach (var action in pending)
            {
                action();
            }

            audit.Record(db, AuditEntityType, Guid.CreateVersion7(), "Roster imported", after: new
            {
                request.FileName,
                Sha256 = sha,
                Created = rows.Count(r => r.Action == RosterAction.Create),
                Updated = rows.Count(r => r.Action == RosterAction.Update),
                Errors = rows.Count(r => r.Action == RosterAction.Error),
            });
            await db.SaveChangesAsync(ct);
            if (securityLog is not null)
            {
                await securityLog.RecordAsync(RosterImportedEvent, request.FileName, detail: $"{rows.Count(r => r.Action == RosterAction.Create)} created, {rows.Count(r => r.Action == RosterAction.Update)} updated", cancellationToken: ct);
            }
        }

        if (rows.Any(r => r.Action == RosterAction.Error))
        {
            warnings.Add($"{rows.Count(r => r.Action == RosterAction.Error)} row(s) have errors and " + (request.DryRun ? "won't be imported." : "were skipped."));
        }

        return new PaylocityRosterResultDto(request.DryRun, request.FileName, sha, rows, linked, warnings);
    }

    private static (Employee? Employee, string MatchedBy) Match(PaylocityRosterRecord rec, Dictionary<string, List<EmployeePayProfile>> byId,
        Dictionary<string, List<Employee>> byEmail, Dictionary<string, List<Employee>> byNumber, Dictionary<Guid, Employee> employees, List<string> errors)
    {
        Employee? ById = null;
        if (byId.TryGetValue(rec.PaylocityId, out var profiles))
        {
            if (profiles.Select(p => p.EmployeeId).Distinct().Count() > 1)
            {
                errors.Add($"Paylocity id {rec.PaylocityId} is on more than one Workbench employee: fix the payroll profiles first.");
                return (null, "id");
            }

            ById = employees.GetValueOrDefault(profiles[0].EmployeeId);
        }

        Employee? ByEmail = null;
        if (rec.Email is not null && byEmail.TryGetValue(rec.Email, out var withEmail))
        {
            if (withEmail.Count > 1)
            {
                errors.Add($"Email {rec.Email} is on more than one Workbench employee.");
                return (null, "email");
            }

            ByEmail = withEmail[0];
        }

        if (ById is not null && ByEmail is not null && ById.Id != ByEmail.Id)
        {
            errors.Add($"Paylocity id {rec.PaylocityId} belongs to {ById.DisplayName} but email {rec.Email} belongs to {ByEmail.DisplayName}. Fix one of them; nothing was guessed.");
            return (null, "conflict");
        }

        if (ById is not null)
        {
            return (ById, "Paylocity id");
        }

        if (ByEmail is not null)
        {
            // The same person by email: they must not already be linked to a different Paylocity id.
            var linkedTo = byId.Values.SelectMany(v => v).FirstOrDefault(p => p.EmployeeId == ByEmail.Id)?.ProviderEmployeeId;
            if (!string.IsNullOrWhiteSpace(linkedTo) && !string.Equals(linkedTo.Trim(), rec.PaylocityId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{ByEmail.DisplayName} (email {rec.Email}) is already linked to Paylocity id {linkedTo}, not {rec.PaylocityId}.");
                return (null, "email");
            }

            return (ByEmail, "email");
        }

        if (byNumber.TryGetValue(rec.PaylocityId, out var withNumber))
        {
            if (withNumber.Count > 1)
            {
                errors.Add($"Employee number {rec.PaylocityId} is on more than one Workbench employee.");
                return (null, "employee number");
            }

            var linkedTo = byId.Values.SelectMany(v => v).FirstOrDefault(p => p.EmployeeId == withNumber[0].Id)?.ProviderEmployeeId;
            if (!string.IsNullOrWhiteSpace(linkedTo) && !string.Equals(linkedTo.Trim(), rec.PaylocityId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{withNumber[0].DisplayName} (employee number {rec.PaylocityId}) is already linked to Paylocity id {linkedTo}.");
                return (null, "employee number");
            }

            return (withNumber[0], "employee number");
        }

        return (null, "none");
    }

    private static OrgUnit? FindOrg(string? department, IReadOnlyList<OrgUnit> orgs, List<string> notices)
    {
        if (string.IsNullOrWhiteSpace(department))
        {
            return null;
        }

        var d = department.Trim();
        var hit = orgs.FirstOrDefault(o => string.Equals(o.Code, d, StringComparison.OrdinalIgnoreCase)) ?? orgs.FirstOrDefault(o => string.Equals(o.Name, d, StringComparison.OrdinalIgnoreCase));
        if (hit is null && !notices.Any(n => n.StartsWith($"Department \"{d}\"", StringComparison.Ordinal)))
        {
            notices.Add($"Department \"{d}\" isn't an org unit code or name in Workbench; the org unit is left alone.");
        }

        return hit;
    }

    private static string? Trim(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private static PaylocityRosterRowDto Row(PaylocityRosterRecord rec, RosterAction action, Employee? employee, string matchedBy,
        List<RosterFieldChange> changes, List<string> errors, List<string> notices) =>
        new(rec.Line, rec.PaylocityId, rec.Name, rec.Email, action, employee?.Id, matchedBy, changes, errors, notices);
}
