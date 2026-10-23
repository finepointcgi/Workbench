using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>A baseline to write into an MS Project file: its dates by Workbench task (item) id.</summary>
public sealed record MsProjectBaseline(string Name, IReadOnlyDictionary<Guid, (DateOnly? Start, DateOnly? Finish)> Dates);

/// <summary>
/// MS Project's XML format (MSPDI: File › Save As › XML in MS Project), read for import and written for export. Workbench
/// doesn't read or write .mpp, MS Project's own closed binary format; save as .xml in MS Project to bring a plan in.
/// <list type="bullet">
/// <item>Read: tasks in ID order become rows (the row number is MS Project's task ID), with outline level, duration (working days at
/// the file's hours per day), start and finish, predecessors with type and lag, constraints, % complete, notes, WBS and
/// resources by name or email. The task GUID is the Unique ID, so a file Workbench wrote matches its tasks again.</item>
/// <item>Write: the same fields, the plan's calendar (work week and days off) as the Standard calendar, assignments, and
/// up to 11 baselines (Baseline, Baseline1…Baseline10). Elements follow the MSPDI schema's order, which MS Project checks.</item>
/// </list>
/// </summary>
public static class MsProjectXml
{
    public const string ContentType = "application/xml";

    private const string Ns = "http://schemas.microsoft.com/project";
    private const int MinutesPerDay = 480;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>True for a file name MS Project XML import should read.</summary>
    public static bool IsXml(string? fileName) => string.Equals(Path.GetExtension(fileName ?? ""), ".xml", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- write

    public static byte[] Write(
        string planName,
        IReadOnlyList<PlanSheetRow> rows,
        WorkCalendar calendar,
        IReadOnlyDictionary<string, string?> resourceEmails,
        IReadOnlyList<MsProjectBaseline> baselines,
        DateTime createdUtc)
    {
        var dated = rows.Where(r => r.Start is not null).ToList();
        var projectStart = dated.Count > 0 ? dated.Min(r => r.Start!.Value) : DateOnly.FromDateTime(createdUtc);
        var projectFinish = rows.Where(r => (r.Finish ?? r.Start) is not null).Select(r => (r.Finish ?? r.Start)!.Value).DefaultIfEmpty(projectStart).Max();
        var workWeek = Enumerable.Range(0, 7).Count(d => (calendar.WorkDays & (1 << d)) != 0);

        // Resources: everyone named on a task, in order of first appearance.
        var resources = new List<string>();
        var taskResources = rows.Select(r => Names(r.Resources).ToList()).ToList();
        foreach (var name in taskResources.SelectMany(n => n))
        {
            if (!resources.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                resources.Add(name);
            }
        }

        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument(standalone: true);
            w.WriteStartElement("Project", Ns);
            El(w, "SaveVersion", "14");
            El(w, "Name", FileName(planName) + ".xml");
            El(w, "Title", planName);
            El(w, "CreationDate", DateTimeText(createdUtc));
            El(w, "ScheduleFromStart", "1");
            El(w, "StartDate", Stamp(projectStart, start: true));
            El(w, "FinishDate", Stamp(projectFinish, start: false));
            El(w, "CalendarUID", "1");
            El(w, "DefaultStartTime", "08:00:00");
            El(w, "DefaultFinishTime", "17:00:00");
            El(w, "MinutesPerDay", MinutesPerDay.ToString(Inv));
            El(w, "MinutesPerWeek", (MinutesPerDay * workWeek).ToString(Inv));
            El(w, "DaysPerMonth", "20");
            El(w, "DefaultTaskType", "1"); // fixed duration: assigning people doesn't change a task's length
            El(w, "DurationFormat", "7"); // days

            WriteCalendar(w, calendar);

            w.WriteStartElement("Tasks");
            WriteProjectSummary(w, planName, projectStart, projectFinish);
            var uidOf = rows.Select((r, i) => (r.Id ?? i + 1, i + 1)).ToDictionary(x => x.Item1, x => x.Item2);
            for (var i = 0; i < rows.Count; i++)
            {
                WriteTask(w, rows[i], i + 1, uidOf, baselines);
            }

            w.WriteEndElement();

            w.WriteStartElement("Resources");
            for (var i = 0; i < resources.Count; i++)
            {
                w.WriteStartElement("Resource");
                El(w, "UID", (i + 1).ToString(Inv));
                El(w, "ID", (i + 1).ToString(Inv));
                El(w, "Name", resources[i]);
                El(w, "Type", "1");
                El(w, "IsNull", "0");
                if (resourceEmails.TryGetValue(resources[i], out var email) && !string.IsNullOrWhiteSpace(email))
                {
                    El(w, "EmailAddress", email);
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();

            w.WriteStartElement("Assignments");
            var assignment = 0;
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var name in taskResources[i])
                {
                    w.WriteStartElement("Assignment");
                    El(w, "UID", (++assignment).ToString(Inv));
                    El(w, "TaskUID", (i + 1).ToString(Inv));
                    El(w, "ResourceUID", (resources.FindIndex(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) + 1).ToString(Inv));
                    El(w, "Units", "1");
                    w.WriteEndElement();
                }
            }

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return stream.ToArray();
    }

    private static void WriteCalendar(XmlWriter w, WorkCalendar calendar)
    {
        w.WriteStartElement("Calendars");
        w.WriteStartElement("Calendar");
        El(w, "UID", "1");
        El(w, "Name", "Standard");
        El(w, "IsBaseCalendar", "1");
        El(w, "BaseCalendarUID", "-1");
        w.WriteStartElement("WeekDays");
        for (var day = 0; day < 7; day++)
        {
            w.WriteStartElement("WeekDay");
            El(w, "DayType", (day + 1).ToString(Inv)); // 1 = Sunday
            var working = (calendar.WorkDays & (1 << day)) != 0;
            El(w, "DayWorking", working ? "1" : "0");
            if (working)
            {
                WorkingTimes(w);
            }

            w.WriteEndElement();
        }

        // Exceptions as MS Project has always read them: DayType 0 with the day's time period.
        foreach (var (date, working) in calendar.Holidays.Select(d => (d, false)).Concat(calendar.ExtraWorkingDays.Select(d => (d, true))).OrderBy(x => x.Item1))
        {
            w.WriteStartElement("WeekDay");
            El(w, "DayType", "0");
            El(w, "DayWorking", working ? "1" : "0");
            w.WriteStartElement("TimePeriod");
            El(w, "FromDate", date.ToString("yyyy-MM-dd", Inv) + "T00:00:00");
            El(w, "ToDate", date.ToString("yyyy-MM-dd", Inv) + "T23:59:00");
            w.WriteEndElement();
            if (working)
            {
                WorkingTimes(w);
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WorkingTimes(XmlWriter w)
    {
        w.WriteStartElement("WorkingTimes");
        foreach (var (from, to) in new[] { ("08:00:00", "12:00:00"), ("13:00:00", "17:00:00") })
        {
            w.WriteStartElement("WorkingTime");
            El(w, "FromTime", from);
            El(w, "ToTime", to);
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void WriteProjectSummary(XmlWriter w, string planName, DateOnly start, DateOnly finish)
    {
        w.WriteStartElement("Task");
        El(w, "UID", "0");
        El(w, "ID", "0");
        El(w, "Name", planName);
        El(w, "Type", "1");
        El(w, "IsNull", "0");
        El(w, "OutlineNumber", "0");
        El(w, "OutlineLevel", "0");
        El(w, "Start", Stamp(start, start: true));
        El(w, "Finish", Stamp(finish, start: false));
        El(w, "Summary", "1");
        w.WriteEndElement();
    }

    private static void WriteTask(XmlWriter w, PlanSheetRow row, int uid, IReadOnlyDictionary<int, int> uidOf, IReadOnlyList<MsProjectBaseline> baselines)
    {
        var milestone = !row.IsSummary && row.DurationDays == 0;
        w.WriteStartElement("Task");
        El(w, "UID", uid.ToString(Inv));
        if (row.UniqueId is { } guid)
        {
            El(w, "GUID", guid.ToString().ToUpperInvariant());
        }

        El(w, "ID", uid.ToString(Inv));
        El(w, "Name", row.Name);
        El(w, "Active", "1");
        El(w, "Manual", "0");
        El(w, "Type", "1");
        El(w, "IsNull", "0");
        El(w, "WBS", string.IsNullOrWhiteSpace(row.WbsCode) ? row.OutlineNumber : row.WbsCode);
        El(w, "OutlineNumber", row.OutlineNumber);
        El(w, "OutlineLevel", row.Level.ToString(Inv));
        if (row.Start is { } start)
        {
            El(w, "Start", Stamp(start, start: true));
            El(w, "Finish", milestone ? Stamp(start, start: true) : Stamp(row.Finish ?? start, start: false));
        }

        El(w, "Duration", Duration(row.DurationDays ?? (row.Start is null ? 1 : 0)));
        El(w, "DurationFormat", "7");
        El(w, "Estimated", row.Start is null ? "1" : "0");
        El(w, "Milestone", milestone ? "1" : "0");
        El(w, "Summary", row.IsSummary ? "1" : "0");
        if (row.PercentComplete is { } percent)
        {
            El(w, "PercentComplete", Math.Round(percent, 0, MidpointRounding.AwayFromZero).ToString("0", Inv));
        }

        var constraint = row.Constraint is { Kind: not PlanConstraintKind.AsSoonAsPossible } c ? c : null;
        El(w, "ConstraintType", ((int)(constraint?.Kind ?? PlanConstraintKind.AsSoonAsPossible)).ToString(Inv));
        if (constraint is not null)
        {
            var finishKind = constraint.Kind is PlanConstraintKind.MustFinishOn or PlanConstraintKind.FinishNoEarlierThan or PlanConstraintKind.FinishNoLaterThan;
            El(w, "ConstraintDate", Stamp(constraint.Date, start: !finishKind));
        }

        if (!string.IsNullOrWhiteSpace(row.Notes))
        {
            El(w, "Notes", row.Notes);
        }

        foreach (var link in ScheduleEngine.ParsePredecessors(row.Predecessors, out _))
        {
            if (!uidOf.TryGetValue(link.TaskNumber, out var predecessorUid))
            {
                continue;
            }

            w.WriteStartElement("PredecessorLink");
            El(w, "PredecessorUID", predecessorUid.ToString(Inv));
            El(w, "Type", LinkType(link.Type).ToString(Inv));
            El(w, "CrossProject", "0");
            El(w, "LinkLag", (link.LagDays * MinutesPerDay * 10).ToString(Inv));
            El(w, "LagFormat", "7");
            w.WriteEndElement();
        }

        for (var n = 0; n < Math.Min(baselines.Count, 11); n++)
        {
            if (row.UniqueId is not { } id || !baselines[n].Dates.TryGetValue(id, out var b) || (b.Start ?? b.Finish) is not { } bs)
            {
                continue;
            }

            var bf = b.Finish ?? bs;
            w.WriteStartElement("Baseline");
            El(w, "Number", n.ToString(Inv));
            El(w, "Start", Stamp(bs, start: true));
            El(w, "Finish", milestone && bs == bf ? Stamp(bs, start: true) : Stamp(bf, start: false));
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    /// <summary>MSPDI link types: 0 FF, 1 FS, 2 SF, 3 SS.</summary>
    private static int LinkType(DependencyKind kind) => kind switch
    {
        DependencyKind.FinishToFinish => 0,
        DependencyKind.StartToFinish => 2,
        DependencyKind.StartToStart => 3,
        _ => 1,
    };

    private static DependencyKind LinkKind(int type) => type switch
    {
        0 => DependencyKind.FinishToFinish,
        2 => DependencyKind.StartToFinish,
        3 => DependencyKind.StartToStart,
        _ => DependencyKind.FinishToStart,
    };

    private static string Duration(int days) => $"PT{(days * MinutesPerDay / 60).ToString(Inv)}H0M0S";

    private static string Stamp(DateOnly date, bool start) => date.ToString("yyyy-MM-dd", Inv) + (start ? "T08:00:00" : "T17:00:00");

    private static string DateTimeText(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss", Inv);

    private static void El(XmlWriter w, string name, string? value) => w.WriteElementString(name, Ns, value ?? "");

    private static IEnumerable<string> Names(string? resources) =>
        (resources ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return safe.Length == 0 ? "Plan" : safe;
    }

    // ---------------------------------------------------------------- read

    /// <summary>
    /// Reads an MS Project XML file into import rows (one per task, in ID order; the row number is the task ID). Throws
    /// <see cref="ArgumentException"/> when the file isn't MS Project XML; problems with one task are reported on its row.
    /// </summary>
    public static IReadOnlyList<PlanFileRow> Read(string fileName, byte[] content)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = 100_000_000 };
            using var reader = XmlReader.Create(new MemoryStream(content), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ArgumentException($"{fileName} isn't readable XML ({ex.Message}). In MS Project, use File › Save As and choose XML Format (*.xml).");
        }

        var project = doc.Root;
        if (project is null || project.Name.LocalName != "Project")
        {
            throw new ArgumentException($"{fileName} isn't an MS Project XML file. In MS Project, use File › Save As and choose XML Format (*.xml); Workbench can't read .mpp files.");
        }

        var minutesPerDay = Int(Child(project, "MinutesPerDay")) is { } m and > 0 ? m : MinutesPerDay;
        var minutesPerWeek = Int(Child(project, "MinutesPerWeek")) is { } mw and > 0 ? mw : minutesPerDay * 5;
        var workingDaysPerElapsedDay = minutesPerWeek / (double)minutesPerDay / 7; // 5/7 on a Monday-to-Friday calendar

        var resources = Children(project, "Resources", "Resource")
            .Select(r => (Uid: Int(Child(r, "UID")), Name: Child(r, "Name")?.Trim(), Email: Child(r, "EmailAddress")?.Trim()))
            .Where(r => r.Uid is not null && !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Uid!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var assignments = Children(project, "Assignments", "Assignment")
            .Select(a => (Task: Int(Child(a, "TaskUID")), Resource: Int(Child(a, "ResourceUID"))))
            .Where(a => a.Task is not null && a.Resource is { } r && resources.ContainsKey(r))
            .ToLookup(a => a.Task!.Value, a => resources[a.Resource!.Value]);

        var tasks = Children(project, "Tasks", "Task")
            .Where(t => Child(t, "IsNull") != "1" && Int(Child(t, "OutlineLevel")) is not 0 && Int(Child(t, "UID")) is not 0)
            .Select((t, i) => (Element: t, Uid: Int(Child(t, "UID")), Id: Int(Child(t, "ID")) ?? i + 1, Order: i))
            .OrderBy(t => t.Id)
            .ThenBy(t => t.Order)
            .ToList();

        // Predecessors name UIDs; rows (and Workbench predecessor text) use the task IDs people see in MS Project.
        var idOfUid = new Dictionary<int, int>();
        for (var i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].Uid is { } uid)
            {
                idOfUid.TryAdd(uid, tasks[i].Id);
            }
        }

        var rows = new List<PlanFileRow>(tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i].Element;
            var id = tasks[i].Id;
            var errors = new List<string>();
            var warnings = new List<string>();
            var name = (Child(t, "Name") ?? "").Trim();
            if (name.Length == 0)
            {
                errors.Add("The task has no name.");
            }
            else if (name.Length > 500)
            {
                errors.Add("Task name is longer than 500 characters.");
            }

            var level = Int(Child(t, "OutlineLevel")) ?? 1;
            var outlineNumber = Child(t, "OutlineNumber") ?? "";
            var summary = Child(t, "Summary") == "1";
            var milestone = Child(t, "Milestone") == "1";

            int? duration = null;
            if (Child(t, "Duration") is { Length: > 0 } durationText)
            {
                try
                {
                    var minutes = XmlConvert.ToTimeSpan(durationText).TotalMinutes;
                    if (IsElapsed(Int(Child(t, "DurationFormat"))))
                    {
                        var elapsed = minutes / 1440;
                        minutes = elapsed * workingDaysPerElapsedDay * minutesPerDay;
                        warnings.Add($"Its duration is {elapsed.ToString("0.##", Inv)} elapsed days, read as {Math.Round(minutes / minutesPerDay).ToString(Inv)} working days.");
                    }

                    var days = minutes / minutesPerDay;
                    var whole = (int)Math.Round(days, MidpointRounding.AwayFromZero);
                    if (Math.Abs(days - whole) > 0.01)
                    {
                        warnings.Add($"Its duration ({days.ToString("0.##", Inv)} days) is rounded to {whole} working day{(whole == 1 ? "" : "s")}.");
                    }

                    duration = Math.Clamp(whole, 0, 3650);
                }
                catch (FormatException)
                {
                    errors.Add($"Duration \"{durationText}\" isn't an MS Project duration.");
                }
            }

            if (milestone)
            {
                duration = 0;
            }
            else if (duration == 0 && !summary)
            {
                milestone = true;
            }

            var start = Date(Child(t, "Start"), finish: false, errors, "Start");
            var finish = Date(Child(t, "Finish"), finish: true, errors, "Finish");
            if (start is { } s && finish is { } f && f < s)
            {
                finish = milestone ? s : f;
            }

            if (milestone && start is { } ms)
            {
                finish = ms;
            }

            decimal? percent = null;
            if (Child(t, "PercentComplete") is { Length: > 0 } percentText)
            {
                if (decimal.TryParse(percentText, NumberStyles.Number, Inv, out var p) && p is >= 0 and <= 100)
                {
                    // MS Project writes 0 for every task not started; that's no progress, not a value worth a column.
                    percent = p == 0 ? null : p;
                }
                else
                {
                    errors.Add($"% Complete \"{percentText}\" isn't between 0 and 100.");
                }
            }

            PlanConstraintDto? constraint = null;
            var constraintType = Int(Child(t, "ConstraintType")) ?? 0;
            if (constraintType == (int)PlanConstraintKind.AsLateAsPossible)
            {
                warnings.Add("As Late As Possible isn't supported; it's scheduled As Soon As Possible.");
            }
            else if (constraintType is >= 2 and <= 7)
            {
                var kind = (PlanConstraintKind)constraintType;
                if (Date(Child(t, "ConstraintDate"), finish: kind is PlanConstraintKind.MustFinishOn or PlanConstraintKind.FinishNoEarlierThan or PlanConstraintKind.FinishNoLaterThan,
                        errors, "Constraint date") is { } date)
                {
                    constraint = new PlanConstraintDto(kind, date);
                }
                else
                {
                    errors.Add($"{PlanConstraints.Label(kind)} has no constraint date.");
                }
            }
            else if (constraintType != 0)
            {
                warnings.Add($"Constraint type {constraintType} isn't an MS Project constraint; it's ignored.");
            }

            var predecessors = new List<string>();
            foreach (var link in t.Elements().Where(e => e.Name.LocalName == "PredecessorLink"))
            {
                if (Child(link, "CrossProject") == "1")
                {
                    warnings.Add("A link to another project is left out.");
                    continue;
                }

                if (Int(Child(link, "PredecessorUID")) is not { } predecessorUid || !idOfUid.TryGetValue(predecessorUid, out var predecessorId))
                {
                    warnings.Add($"A predecessor (UID {Child(link, "PredecessorUID")}) isn't a task in the file; that link is left out.");
                    continue;
                }

                var lag = 0;
                var lagFormat = Int(Child(link, "LagFormat"));
                if (lagFormat is 19 or 20 or 51 or 52)
                {
                    if (Int(Child(link, "LinkLag")) is not (null or 0))
                    {
                        warnings.Add($"The lag on its link to task {predecessorId} is a percentage, which Workbench doesn't support; it's left out.");
                    }
                }
                else if (double.TryParse(Child(link, "LinkLag"), NumberStyles.Float, Inv, out var tenths) && tenths != 0)
                {
                    var minutes = tenths / 10;
                    var days = IsElapsed(lagFormat) ? minutes / 1440 * workingDaysPerElapsedDay : minutes / minutesPerDay;
                    lag = (int)Math.Round(days, MidpointRounding.AwayFromZero);
                    if (IsElapsed(lagFormat))
                    {
                        warnings.Add($"The lag on its link to task {predecessorId} is {(minutes / 1440).ToString("0.##", Inv)} elapsed days, read as {lag} working day{(Math.Abs(lag) == 1 ? "" : "s")}.");
                    }
                    else if (Math.Abs(days - lag) > 0.01)
                    {
                        warnings.Add($"The lag on its link to task {predecessorId} is rounded to {lag} working day{(Math.Abs(lag) == 1 ? "" : "s")}.");
                    }
                }

                predecessors.Add(ScheduleEngine.FormatPredecessor(predecessorId, LinkKind(Int(Child(link, "Type")) ?? 1), lag));
            }

            // MS Project fills WBS with the outline number by default; that isn't a charge code, so leave it to Workbench.
            var wbs = Child(t, "WBS")?.Trim();
            if (string.IsNullOrEmpty(wbs) || wbs == outlineNumber)
            {
                wbs = null;
            }
            else if (wbs.Length > 64)
            {
                errors.Add("WBS code is longer than 64 characters.");
            }

            var people = tasks[i].Uid is { } taskUid
                ? assignments[taskUid].Select(r => string.IsNullOrWhiteSpace(r.Email) ? r.Name! : r.Email!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            Guid? uniqueId = Guid.TryParse(Child(t, "GUID"), out var g) ? g : null;
            var notes = Child(t, "Notes");

            rows.Add(new PlanFileRow(
                id,
                new PlanSheetRow(id, outlineNumber, Math.Max(level, 1), wbs, name, duration, start, finish,
                    predecessors.Count == 0 ? null : string.Join(", ", predecessors), percent,
                    people.Count == 0 ? null : string.Join("; ", people), string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), uniqueId,
                    summary, constraint),
                duration?.ToString(Inv),
                errors,
                warnings));
        }

        return rows;
    }

    /// <summary>Elapsed duration formats (calendar time rather than working time), including their estimated variants.</summary>
    private static bool IsElapsed(int? format) => format is 4 or 6 or 8 or 10 or 12 or 20 or 36 or 38 or 40 or 42 or 44 or 52;

    private static DateOnly? Date(string? text, bool finish, List<string> errors, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DateTime.TryParse(text, Inv, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var value))
        {
            errors.Add($"{what} \"{text}\" isn't a date.");
            return null;
        }

        // A finish at midnight is the end of the day before.
        return DateOnly.FromDateTime(finish && value.TimeOfDay == TimeSpan.Zero ? value.AddDays(-1) : value);
    }

    private static string? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static IEnumerable<XElement> Children(XElement project, string list, string item) =>
        project.Elements().Where(e => e.Name.LocalName == list).Elements().Where(e => e.Name.LocalName == item);

    private static int? Int(string? text) => int.TryParse(text, NumberStyles.Integer, Inv, out var n) ? n : null;
}
