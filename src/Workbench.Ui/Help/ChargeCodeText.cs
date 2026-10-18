using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Ui.Help;

/// <summary>
/// How a charge code reads to an employee: its name first, the code after it in brackets ("Requirements review
/// (1001.01.02)"). Leave codes show just their name ("Holiday", not "LV.HOL IND").
/// </summary>
public static class ChargeCodeText
{
    public static string Label(string code, string? name, ChargeCodeKind? kind = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, code, StringComparison.OrdinalIgnoreCase))
        {
            return code;
        }

        return kind == ChargeCodeKind.Leave ? name : $"{name} ({code})";
    }

    public static string Label(ChargeCodeDto code) => Label(code.Code, code.Name, code.Kind);

    /// <summary>A second line under the name: the project for direct work, the kind for indirect and leave.</summary>
    public static string Context(ChargeCodeKind? kind, string? projectCode, string? projectName = null) => kind switch
    {
        ChargeCodeKind.Leave => "Leave",
        ChargeCodeKind.Indirect => "Indirect (not billed)",
        _ when !string.IsNullOrWhiteSpace(projectName) => $"Project {projectCode} · {projectName}",
        _ => string.IsNullOrWhiteSpace(projectCode) ? "" : $"Project {projectCode}",
    };
}
