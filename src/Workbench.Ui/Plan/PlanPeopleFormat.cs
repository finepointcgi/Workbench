using System.Globalization;
using Workbench.Contracts.Resourcing;

namespace Workbench.Ui.Plan;

/// <summary>What the task dialog asks for: the changes, whether to try them first, and the names of people it mentions.</summary>
public sealed record PlanTaskPeopleResult(IReadOnlyList<PlanChange> Changes, bool TryFirst, IReadOnlyDictionary<Guid, string> Names);

/// <summary>Plain words for the Plan tab's people, hours and cost.</summary>
public static class PlanPeopleFormat
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Hours(decimal hours) => hours.ToString("#,##0.##", C);

    public static string Money(decimal? amount) => amount is { } a ? (a < 0 ? "−$" : "$") + Math.Abs(a).ToString("#,##0", C) : "";

    public static string Date(DateOnly? date) => date?.ToString("MMM d, yyyy", C) ?? "";

    public static string Short(DateOnly date) => date.ToString("MMM d", C);

    public static string Range(DateOnly? from, DateOnly? to) =>
        from is { } f && to is { } t ? f.Year == t.Year ? $"{Short(f)} – {Date(t)}" : $"{Date(f)} – {Date(t)}" : Date(from ?? to);

    /// <summary>"no room to slip", "can slip 3 working days" or "misses a deadline by 2 working days".</summary>
    public static string Float(int? days) => days switch
    {
        null => "",
        0 => "no room to slip",
        > 0 => $"can slip {Days(days.Value)}",
        _ => $"misses a deadline by {Days(-days.Value)}",
    };

    public static string Days(int days) => days == 1 ? "1 working day" : $"{days} working days";

    /// <summary>"Booked 72 of 40 h the week of Oct 19 (32 h on other work)".</summary>
    public static string Overbooked(AssignmentOverbookedWeekDto week) =>
        $"booked {Hours(week.ThisAssignment + week.OtherHours)} of {Hours(week.Capacity)} h the week of {Short(week.WeekStart)}"
        + (week.OtherHours > 0 ? $" ({Hours(week.OtherHours)} h on other work)" : "");

    /// <summary>A change in a sentence, for the "Trying" list.</summary>
    public static string Describe(PlanChange change, ResourcePlanDto plan, IReadOnlyDictionary<Guid, string> names)
    {
        var task = change.ItemId is { } itemId ? plan.Task(itemId)?.Title ?? "a task" : null;
        var person = change.AssignmentId is { } id ? FindPerson(plan, id) : null;
        string Name(Guid? employeeId) => employeeId is { } e && names.TryGetValue(e, out var n) ? n : "someone";
        return change.Kind switch
        {
            PlanChangeKind.MoveTask when change.Start is { } s && change.Finish is { } f => $"Move {task} to {Range(s, f)}",
            PlanChangeKind.MoveTask when change.Start is { } s => $"Start {task} on {Date(s)}",
            PlanChangeKind.MoveTask => $"Finish {task} on {Date(change.Finish)}",
            PlanChangeKind.RemovePerson => $"Take {person?.Person.Name ?? "someone"} off {person?.Task ?? "the plan"}",
            PlanChangeKind.SetPerson when change.AssignmentId is null => $"Add {Name(change.EmployeeId)} to {task ?? "the project"}: {Hours(change.Hours ?? 0)} h",
            _ when change.EmployeeId is { } other && other != person?.Person.EmployeeId =>
                $"Give {person?.Person.Name ?? "someone"}'s hours on {person?.Task ?? "the plan"} to {Name(other)}",
            _ when change.To is { } to && change.Hours is null => $"Spread {person?.Person.Name ?? "someone"}'s hours on {person?.Task ?? "the plan"} to {Date(to)}",
            _ => $"{person?.Person.Name ?? "Someone"} on {person?.Task ?? "the plan"}: {Hours(change.Hours ?? person?.Person.Hours ?? 0)} h",
        };
    }

    private static (PlanPersonDto Person, string Task)? FindPerson(ResourcePlanDto plan, Guid assignmentId)
    {
        foreach (var row in plan.Tasks)
        {
            if (row.People.FirstOrDefault(p => p.AssignmentId == assignmentId) is { } p)
            {
                return (p, row.Title);
            }
        }

        return plan.NotOnATask.FirstOrDefault(p => p.AssignmentId == assignmentId) is { } q ? (q, "the project") : null;
    }
}
