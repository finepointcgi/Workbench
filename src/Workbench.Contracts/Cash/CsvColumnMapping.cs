namespace Workbench.Contracts.Cash;

// Stays in core with the statement file readers (Application/Cash/Statements), which corporate card feeds use too.
/// <summary>
/// How a bank's CSV export maps to bank items. Columns are header names (case-insensitive) or 1-based column numbers
/// (<c>"3"</c>). Use <see cref="Amount"/> for one signed column, or <see cref="Debit"/> and <see cref="Credit"/> for two.
/// <see cref="DateFormat"/> is a .NET format such as <c>MM/dd/yyyy</c>; empty tries the common ones.
/// </summary>
public sealed record CsvColumnMapping(
    string Date,
    string Description,
    string? Amount = null,
    string? Debit = null,
    string? Credit = null,
    string? CheckNumber = null,
    string? Reference = null,
    string? Type = null,
    string? DateFormat = null,
    bool HasHeader = true,
    string Delimiter = ",",
    bool InvertSign = false);
