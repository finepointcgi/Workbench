using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Sheets;

internal static class Mapping
{
    public static ColumnDto ToDto(this Column c) =>
        new(c.Id, c.Name, (ColumnKind)(int)c.Type, c.Order, c.Options.ToList(), c.Formula, c.IsRestricted,
            OptionColors: c.Type is ColumnType.Dropdown or ColumnType.MultiSelect ? StatusLabels.Colors(c.Options, c.OptionColors) : null);

    public static ItemDto ToDto(this Item i) =>
        new(i.Id, i.ParentId, i.Order, i.Title, i.Start, i.Finish, i.CostpointWbsId,
            new Dictionary<string, string?>(i.Values), i.IsMilestone);

    public static ColumnType ToDomain(this ColumnKind kind) => (ColumnType)(int)kind;

    public static ColumnKind ToContract(this ColumnType type) => (ColumnKind)(int)type;
}
