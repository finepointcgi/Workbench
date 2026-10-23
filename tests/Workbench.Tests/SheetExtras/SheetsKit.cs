using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Identity;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;
using Workbench.Tests.Identity;

namespace Workbench.Tests.SheetExtras;

/// <summary>Helpers over <see cref="SeededHost"/>: the Program Office "Schedule" sheet, and "Budget" in the Finance workspace (admins only).</summary>
internal static class SheetsKit
{
    public static ISheetLinkService Links(this SeededHost host) => host.Host.Get<ISheetLinkService>();

    public static ISheetFormulaService Formulas(this SeededHost host) => host.Host.Get<ISheetFormulaService>();

    public static ISheetTemplateService Templates(this SeededHost host) => host.Host.Get<ISheetTemplateService>();

    public static ISheetReportService Reports(this SeededHost host) => host.Host.Get<ISheetReportService>();

    public static Task<ColumnDto> AddColumnAsync(this SeededHost host, Guid sheetId, string name, ColumnKind kind, params string[] options) =>
        host.Host.Columns.CreateAsync(sheetId, new CreateColumnRequest(name, kind, options));

    public static Task<ItemDto> AddItemAsync(this SeededHost host, Guid sheetId, string title, params (ColumnDto Column, string? Value)[] values) =>
        host.Host.Items.CreateAsync(sheetId, new CreateItemRequest(title, Values: values.ToDictionary(v => v.Column.Id.ToString(), v => v.Value)));

    public static async Task<ItemDto> ItemAsync(this SeededHost host, Guid sheetId, Guid itemId) =>
        (await host.Host.Items.ListAsync(sheetId)).Single(i => i.Id == itemId);

    public static string? Cell(this ItemDto item, ColumnDto column) => item.Values.GetValueOrDefault(column.Id.ToString());

    public static Task<ItemDto> SetAsync(this SeededHost host, Guid sheetId, ItemDto item, ColumnDto column, string? value) =>
        host.Host.Items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId,
            new Dictionary<string, string?>(item.Values) { [column.Id.ToString()] = value }));

    /// <summary>A third sheet in Program Office, so its members can see both ends of a link.</summary>
    public static async Task<Guid> NewProgramSheetAsync(this SeededHost host, string name)
    {
        host.As(SeededHost.Owner);
        return (await host.Host.Sheets.CreateAsync(new CreateSheetRequest(name, null, false))).Id;
    }

    public static async Task MakeAsync(this SeededHost host, string email, OrgRoles roles)
    {
        await using var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            await Application.Identity.RoleStore.AddUserAsync(db, new AppUser { Email = email, DisplayName = email }, roles);
        }
        else
        {
            await Application.Identity.RoleStore.GrantBuiltInAsync(db, user, roles);
        }

        await db.SaveChangesAsync();
    }

    public static async Task<T> DbAsync<T>(this SeededHost host, Func<IWorkbenchDbContext, Task<T>> read)
    {
        await using var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await read(db);
    }
}
