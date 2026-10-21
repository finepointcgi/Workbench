using Workbench.Ui.Navigation;

namespace Workbench.Ui.Help;

/// <summary>One stop on a welcome tour. <see cref="Target"/> is a CSS selector for the thing to point at (none: no highlight).</summary>
public sealed record TourStep(string Title, string Text, string? Target = null, string? LinkText = null, string? LinkHref = null);

/// <summary>A short welcome tour for one kind of user. Finishing or dismissing it is remembered per user on the server, by <see cref="Key"/>.</summary>
public sealed record Tour(string Key, string Name, IReadOnlyList<TourStep> Steps);

/// <summary>The welcome tours, one per role. Each has 3–5 steps that point at where things are.</summary>
public static class Tours
{
    private const string Home = "[data-testid='nav-home']";
    private const string MyWork = "[data-testid='nav-my-work']";
    private const string Approvals = "[data-testid='nav-approvals']";
    private const string Projects = "[data-testid='nav-projects']";
    private const string FinanceMenu = "[data-testid='nav-finance']";
    private const string Reports = "[data-testid='nav-reports']";
    private const string Admin = "[data-testid='nav-admin']";
    private const string Search = "[data-testid='palette-button']";
    private const string HelpButton = "[data-testid='help-button']";

    private static readonly TourStep SearchStep = new(
        "Find anything with Ctrl+K",
        "Press Ctrl+K (⌘K on a Mac), or select Search, and type: a page, a project, a sheet, or a question for the help center.",
        Search);

    private static readonly TourStep HelpStep = new(
        "Help is one click away",
        "Select ? on any page for help about that page. To report a problem or send feedback, open your account menu at the top right. You can take this tour again from the help panel.",
        HelpButton, "Open the help center", "help");

    public static readonly Tour Employee = new("employee", "Welcome", [
        new("Welcome to Workbench", "A one-minute look at where things are. Use Next to move on, or skip it; you can come back to it from the help panel.", null),
        new("Home is your to-do list", "Home lists what needs doing (a timesheet to submit, receipts to add) with a button that goes straight to it.", Home),
        new("Your time, expenses and leave", "They're under My work. Enter your hours every day and certify your timesheet at the end of the period.", MyWork, "How to enter time", "help/entering-time"),
        SearchStep,
        HelpStep,
    ]);

    public static readonly Tour Manager = new("manager", "Welcome", [
        new("Welcome to Workbench", "A one-minute look at where your work lives as a manager. Use Next to move on, or skip it.", null),
        new("One inbox for approvals", "Timesheets, expenses, purchases and bills waiting on you are all in Approvals. The number shows how many are waiting.", Approvals, "How approving works", "help/approving"),
        new("Your projects", "Projects shows each project's health, plan, budget, team and costs on one page.", Projects, "About the project page", "help/project-page"),
        new("Your own time still counts", "Your timesheet, expenses and leave are under My work, and Home lists what's due.", MyWork),
        HelpStep,
    ]);

    public static readonly Tour Finance = new("finance", "Welcome", [
        new("Welcome to Workbench", "A one-minute look at where the Finance work lives. Use Next to move on, or skip it.", null),
        new("Finance", "Accounting, billing, payables, cash, payroll and revenue are grouped under Finance. Period close is under Accounting.", FinanceMenu, "The month-end checklist", "help/month-end-close"),
        new("Reports", "Financial statements, AP, asset and revenue reports, the report catalog and the report writer.", Reports),
        new("Approvals and to-dos", "Home lists close steps that are due, and Approvals holds anything waiting on you.", Approvals),
        HelpStep,
    ]);

    public static readonly Tour Administrator = new("admin", "Welcome", [
        new("Welcome to Workbench", "You're an administrator. Here's where setup and support live. Use Next to move on, or skip it.", null),
        new("Setup and users", "Admin holds users and security, the setup checklist, approval workflows and imports.", Admin, "Read the getting-started guide", "help/admin-getting-started"),
        new("Support requests", "When someone reports a problem, you're notified and it appears in Admin › Support requests.", Admin, "Open support requests", "admin/support"),
        SearchStep,
        HelpStep,
    ]);

    public static IReadOnlyList<Tour> All { get; } = [Employee, Manager, Finance, Administrator];

    /// <summary>The tour for a user's strongest role: administrator, then Finance, then anyone who manages or approves, then everyone else.</summary>
    public static Tour For(NavAccess access) =>
        access.IsAdministrator ? Administrator
        : access.Can(Workbench.Contracts.Identity.Permission.GlJournalsPost) ? Finance
        : access.IsPm || access.IsTimesheetApprover || access.IsLeaveApprover || access.IsResourceManager ? Manager
        : Employee;
}
