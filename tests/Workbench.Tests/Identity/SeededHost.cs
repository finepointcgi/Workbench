using Workbench.Application;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Identity;

/// <summary>
/// The real application registrations over an in-memory database with two workspaces: "Program Office" (one sheet
/// and a member in every role) and "Finance" (one sheet, no members). <c>admin@example.com</c> is an admin.
/// </summary>
internal sealed class SeededHost : IAsyncDisposable
{
    public const string Admin = "admin@example.com";
    public const string Owner = "pm@example.com";
    public const string Editor = "analyst@example.com";
    public const string Commenter = "reviewer@example.com";
    public const string Viewer = "viewer@example.com";
    public const string Outsider = "outsider@example.com";
    public const string Finance = "finance@example.com";
    public const string Timekeeper = "timekeeper@example.com";

    private SeededHost(CollaborationHost host, Guid programOffice, Guid sheetId, Guid itemId, Guid financeSheetId)
    {
        Host = host;
        ProgramOfficeId = programOffice;
        SheetId = sheetId;
        ItemId = itemId;
        FinanceSheetId = financeSheetId;
    }

    public CollaborationHost Host { get; }
    public Guid ProgramOfficeId { get; }
    public Guid SheetId { get; }
    public Guid ItemId { get; }
    public Guid FinanceSheetId { get; }

    public static async Task<SeededHost> CreateAsync()
    {
        var host = new CollaborationHost();
        await using var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var programOffice = new Workspace { Name = "Program Office" };
        var finance = new Workspace { Name = "Finance" };
        var sheet = new Sheet { Workspace = programOffice, Name = "Schedule" };
        var item = new Item { Title = "Kickoff" };
        sheet.Items.Add(item);
        var financeSheet = new Sheet { Workspace = finance, Name = "Budget" };
        db.Workspaces.AddRange(programOffice, finance);
        db.Sheets.AddRange(sheet, financeSheet);
        db.Set<AppUser>().AddRange(
            new AppUser { Email = Admin, DisplayName = "Admin", IsAdmin = true },
            new AppUser { Email = Owner, DisplayName = "Pat" },
            new AppUser { Email = Viewer, DisplayName = "Val" },
            new AppUser { Email = Finance, DisplayName = "Fin", IsFinance = true },
            new AppUser { Email = Timekeeper, DisplayName = "Tim", IsTimekeeper = true });
        db.Set<WorkspaceMember>().AddRange(
            new WorkspaceMember { WorkspaceId = programOffice.Id, UserEmail = Owner, Role = WorkspaceRole.Owner },
            new WorkspaceMember { WorkspaceId = programOffice.Id, UserEmail = Editor, Role = WorkspaceRole.Editor },
            new WorkspaceMember { WorkspaceId = programOffice.Id, UserEmail = Commenter, Role = WorkspaceRole.Commenter },
            new WorkspaceMember { WorkspaceId = programOffice.Id, UserEmail = Viewer, Role = WorkspaceRole.Viewer });
        await db.SaveChangesAsync();
        return new SeededHost(host, programOffice.Id, sheet.Id, item.Id, financeSheet.Id);
    }

    public SeededHost As(string email)
    {
        Host.User.Email = email;
        return this;
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
