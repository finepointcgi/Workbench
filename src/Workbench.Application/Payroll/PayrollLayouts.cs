using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>
/// Configurable Paylocity file layouts. The defaults are exactly the formats documented on <see cref="PaylocityFormats"/>;
/// a company's own Paylocity import template and register export are matched by saving a different layout under
/// Payroll › Providers. Other providers keep their adapter's fixed formats.
/// </summary>
public static class PayrollLayouts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HoursLayoutDto DefaultHours { get; } = new(
    [
        new("Company Code", HoursColumnValue.CompanyCode),
        new("Employee Id", HoursColumnValue.EmployeeId),
        new("Employee Name", HoursColumnValue.EmployeeName),
        new("Pay Period Begin", HoursColumnValue.PeriodBegin),
        new("Pay Period End", HoursColumnValue.PeriodEnd),
        new("Check Date", HoursColumnValue.CheckDate),
        new("Det", HoursColumnValue.Det),
        new("Det Code", HoursColumnValue.EarningCode),
        new("Hours", HoursColumnValue.Hours),
        new("Rate", HoursColumnValue.Rate),
        new("Amount", HoursColumnValue.Amount),
        new("Cost Center 1", HoursColumnValue.CostCenter),
    ]);

    public static RegisterLayoutDto DefaultRegister { get; } = new(
    [
        new(RegisterField.EmployeeId, ["Employee Id", "Emp Id", "Employee Number", "Employee", "Id"]),
        new(RegisterField.EmployeeName, ["Employee Name", "Name", "Emp Name"]),
        new(RegisterField.CheckDate, ["Check Date", "Pay Date"]),
        new(RegisterField.GrossPay, ["Gross Pay", "Gross", "Gross Wages", "Total Gross"]),
        new(RegisterField.EmployeeTaxes, ["EE Taxes", "Employee Taxes", "Total EE Taxes", "Withholding", "EE Tax", "Taxes"]),
        new(RegisterField.EmployerTaxes, ["ER Taxes", "Employer Taxes", "Total ER Taxes", "ER Tax"]),
        new(RegisterField.Deductions, ["Deductions", "Total Deductions", "EE Deductions"]),
        new(RegisterField.EmployerBenefits, ["ER Benefits", "Employer Benefits", "ER Contributions", "Employer Contributions", "Benefits"]),
        new(RegisterField.NetPay, ["Net Pay", "Net", "Net Check", "Net Amount"]),
    ], ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "MM/dd/yy", "M/d/yy"]);

    public static bool IsConfigurable(string providerKey) => providerKey == PaylocityPayrollProvider.ProviderKey;

    public static async Task<(HoursLayoutDto Hours, RegisterLayoutDto Register, PayrollFileLayout? Row)> LoadAsync(IWorkbenchDbContext db, string providerKey, CancellationToken ct)
    {
        var row = await db.Set<PayrollFileLayout>().AsNoTracking().FirstOrDefaultAsync(l => l.ProviderKey == providerKey, ct);
        var hours = row?.HoursLayoutJson is { } h ? JsonSerializer.Deserialize<HoursLayoutDto>(h, Json) ?? DefaultHours : DefaultHours;
        var register = row?.RegisterLayoutJson is { } r ? JsonSerializer.Deserialize<RegisterLayoutDto>(r, Json) ?? DefaultRegister : DefaultRegister;
        return (hours, register, row);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Checks a layout can be used. Throws <see cref="ArgumentException"/> listing every problem.</summary>
    public static void Validate(HoursLayoutDto hours, RegisterLayoutDto register)
    {
        var problems = new List<string>();
        if (hours.Columns.Count == 0)
        {
            problems.Add("The hours export needs at least one column.");
        }

        foreach (var value in new[] { HoursColumnValue.EmployeeId, HoursColumnValue.Hours })
        {
            if (hours.Columns.All(c => c.Value != value))
            {
                problems.Add($"The hours export needs a column for {value}.");
            }
        }

        if (hours.Columns.All(c => c.Value != HoursColumnValue.EarningCode))
        {
            problems.Add("The hours export needs an earning code column (REG, OT and leave hours are told apart by it).");
        }

        if (hours.IncludeHeader && hours.Columns.Any(c => string.IsNullOrWhiteSpace(c.Header)))
        {
            problems.Add("Every hours column needs a header.");
        }

        problems.AddRange(hours.Columns.Where(c => c.Header.Length > 100).Select(c => $"Header '{c.Header[..20]}…' is longer than 100 characters."));
        if (!TryFormat(hours.DateFormat))
        {
            problems.Add($"'{hours.DateFormat}' isn't a date format (try MM/dd/yyyy).");
        }

        if (!TryNumberFormat(hours.HoursFormat))
        {
            problems.Add($"'{hours.HoursFormat}' isn't a number format (try 0.00).");
        }

        if (Delimiter(hours.Delimiter) is null || Delimiter(register.Delimiter) is null)
        {
            problems.Add("The delimiter must be a comma, a pipe, a semicolon or \\t (tab).");
        }

        var detail = register.CodeColumn is { Length: > 0 } || register.AmountColumn is { Length: > 0 };
        if (detail && (string.IsNullOrWhiteSpace(register.CodeColumn) || string.IsNullOrWhiteSpace(register.AmountColumn)))
        {
            problems.Add("A detail register needs both the code column and the amount column.");
        }

        var required = detail ? new[] { RegisterField.EmployeeId, RegisterField.CheckDate } : [RegisterField.EmployeeId, RegisterField.CheckDate, RegisterField.GrossPay, RegisterField.NetPay];
        foreach (var field in required)
        {
            if (!register.Columns.Any(c => c.Field == field && c.Headers.Any(h => h.Trim().Length > 0)))
            {
                problems.Add($"Give at least one register header for {field}.");
            }
        }

        if (register.DateFormats.Count == 0 || register.DateFormats.Any(f => !TryFormat(f)))
        {
            problems.Add("Give the register's date format(s), for example MM/dd/yyyy.");
        }

        if (detail && (register.CodeMap ?? []).All(m => m.Target != RegisterAmountTarget.GrossPay))
        {
            problems.Add("Map at least one code to gross pay.");
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }
    }

    private static bool TryFormat(string format)
    {
        try
        {
            var text = new DateOnly(2026, 12, 31).ToString(format, CultureInfo.InvariantCulture);
            return format.Trim().Length > 0 && DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var back) && back == new DateOnly(2026, 12, 31);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryNumberFormat(string format)
    {
        try
        {
            return 12.5m.ToString(format, CultureInfo.InvariantCulture) is { Length: > 0 } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static char? Delimiter(string text) => text switch
    {
        "," => ',',
        "|" => '|',
        ";" => ';',
        "\\t" or "\t" or "tab" or "TAB" => '\t',
        _ => null,
    };

    // ---- Hours export ----

    public static string WriteHours(PayrollHoursExport export, HoursLayoutDto layout)
    {
        var delimiter = Delimiter(layout.Delimiter) ?? ',';
        var sb = new StringBuilder();
        if (layout.IncludeHeader)
        {
            AppendRow(sb, layout.Columns.Select(c => c.Header), delimiter);
        }

        foreach (var line in export.Lines.Where(l => l.Hours != 0).OrderBy(l => l.ProviderEmployeeId, StringComparer.Ordinal).ThenBy(l => l.EarningCode, StringComparer.Ordinal))
        {
            AppendRow(sb, layout.Columns.Select(c => Value(c, export, line, layout)), delimiter);
        }

        return sb.ToString();
    }

    private static string Value(HoursColumnDto column, PayrollHoursExport export, PayrollHoursLine line, HoursLayoutDto layout)
    {
        string Date(DateOnly d) => d.ToString(layout.DateFormat, CultureInfo.InvariantCulture);
        var space = line.EmployeeName.IndexOf(' ');
        return column.Value switch
        {
            HoursColumnValue.CompanyCode => export.CompanyCode,
            HoursColumnValue.EmployeeId => line.ProviderEmployeeId,
            HoursColumnValue.EmployeeName => line.EmployeeName,
            HoursColumnValue.FirstName => space > 0 ? line.EmployeeName[..space] : line.EmployeeName,
            HoursColumnValue.LastName => space > 0 ? line.EmployeeName[(space + 1)..] : "",
            HoursColumnValue.PeriodBegin => Date(export.PeriodStart),
            HoursColumnValue.PeriodEnd => Date(export.PeriodEnd),
            HoursColumnValue.CheckDate => Date(export.CheckDate),
            HoursColumnValue.Det => column.Constant is { Length: > 0 } det ? det : "E",
            HoursColumnValue.EarningCode => line.EarningCode,
            HoursColumnValue.Hours => line.Hours.ToString(layout.HoursFormat, CultureInfo.InvariantCulture),
            HoursColumnValue.CostCenter => line.CostCenter ?? "",
            HoursColumnValue.PayGroup => export.PayGroupCode,
            HoursColumnValue.Constant => column.Constant ?? "",
            _ => "",
        };
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells, char delimiter) =>
        sb.AppendJoin(delimiter, cells.Select(c => Escape(c, delimiter))).Append("\r\n");

    private static string Escape(string value, char delimiter)
    {
        // Neutralize spreadsheet formulas and quote anything with separators.
        var v = value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? "'" + value : value;
        return v.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    /// <summary>Which template headers the layout writes (and in which position), and which layout columns the template lacks.</summary>
    public static HoursTemplateTestDto TestTemplate(string content, HoursLayoutDto layout)
    {
        var example = WriteHours(ExampleExport, layout);
        var rows = ReadDelimited(content, Delimiter(layout.Delimiter) ?? ',').Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count == 0)
        {
            return new HoursTemplateTestDto(false, "The template is empty: export the Paylocity import template (or a past import file) with its header row.", [], [], example);
        }

        var template = rows[0].Select(h => h.Trim()).ToList();
        var layoutHeaders = layout.Columns.Select(c => c.Header.Trim()).ToList();
        var columns = template.Select((h, i) =>
        {
            var at = layoutHeaders.FindIndex(l => Key(l) == Key(h));
            return new HoursTemplateColumnDto(h, i + 1, at >= 0 ? layoutHeaders[at] : null, at >= 0 ? at + 1 : null, at == i);
        }).ToList();
        var extra = layoutHeaders.Where(l => template.All(h => Key(h) != Key(l))).ToList();
        var ok = columns.All(c => c.SamePosition) && extra.Count == 0;
        return new HoursTemplateTestDto(ok, null, columns, extra, example);
    }

    private static readonly PayrollHoursExport ExampleExport = new("W1234", "BW", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 20),
    [
        new("1001", "Pat Morgan", "REG", 80m, "1.01.10"),
        new("1002", "Alex Rivera", "REG", 72m, "1.02.20"),
        new("1002", "Alex Rivera", "OT", 4.5m, "1.02.20"),
        new("1002", "Alex Rivera", "PTO", 8m, "1.02.20"),
    ]);

    // ---- Payroll register ----

    /// <summary>Reads a register with a layout, for import: any unreadable line or missing column is an <see cref="ArgumentException"/>.</summary>
    public static ParsedPayrollRegister ParseRegister(string content, RegisterLayoutDto layout)
    {
        var test = TestRegister(content, layout);
        if (test.Error is { } error)
        {
            throw new ArgumentException(error);
        }

        if (test.Rows.FirstOrDefault(r => !r.Parsed) is { } bad)
        {
            throw new ArgumentException($"Line {bad.Line}: {bad.Message}");
        }

        var rows = test.Rows.Where(r => r.Parsed).Select(r => new PayrollRegisterRow(r.EmployeeId!, r.EmployeeName ?? "", r.CheckDate!.Value, r.GrossPay ?? 0, r.EmployeeTaxes ?? 0,
            r.EmployerTaxes ?? 0, r.Deductions ?? 0, r.EmployerBenefits ?? 0, r.NetPay ?? 0)).ToList();
        if (rows.Count == 0)
        {
            throw new ArgumentException("The register has no employee rows.");
        }

        return new ParsedPayrollRegister(rows, test.Warnings);
    }

    /// <summary>Reads a register with a layout and reports everything: matched columns, each line's values or problem, unused headers and codes.</summary>
    public static RegisterTestResultDto TestRegister(string content, RegisterLayoutDto layout)
    {
        RegisterTestResultDto Fail(string error, IReadOnlyList<string>? headers = null, IReadOnlyList<RegisterColumnMatchDto>? columns = null) =>
            new(false, error, headers ?? [], columns ?? [], [], [], [], [], 0, 0, 0);

        if (string.IsNullOrWhiteSpace(content))
        {
            return Fail("The register file is empty.");
        }

        var all = ReadDelimited(content, Delimiter(layout.Delimiter) ?? ',').Select((cells, i) => (Line: i + 1, Cells: cells)).Where(r => r.Cells.Any(c => c.Trim().Length > 0)).ToList();
        var detail = layout.CodeColumn is { Length: > 0 } && layout.AmountColumn is { Length: > 0 };

        // The header row is the first of the first ten rows that has the employee id and check date headers (report titles may sit above it).
        var headerIndex = all.Take(10).ToList().FindIndex(r => Find(layout, RegisterField.EmployeeId, r.Cells) >= 0 && Find(layout, RegisterField.CheckDate, r.Cells) >= 0);
        var headerRow = headerIndex >= 0 ? all[headerIndex] : all.FirstOrDefault();
        if (headerRow.Cells is null)
        {
            return Fail("The register has no rows.");
        }

        var headers = headerRow.Cells.Select(h => h.Trim()).ToList();
        var required = detail ? new[] { RegisterField.EmployeeId, RegisterField.CheckDate } : [RegisterField.EmployeeId, RegisterField.CheckDate, RegisterField.GrossPay, RegisterField.NetPay];
        var columns = Enum.GetValues<RegisterField>().Select(f =>
        {
            var i = Find(layout, f, headers);
            return new RegisterColumnMatchDto(f, required.Contains(f), i >= 0 ? headers[i] : null, i >= 0 ? i : null);
        }).ToList();
        var codeIndex = detail ? headers.FindIndex(h => Key(h) == Key(layout.CodeColumn!)) : -1;
        var amountIndex = detail ? headers.FindIndex(h => Key(h) == Key(layout.AmountColumn!)) : -1;
        var missing = columns.Where(c => c.Required && c.Index is null).Select(c => Label(c.Field)).ToList();
        if (detail && codeIndex < 0)
        {
            missing.Add(layout.CodeColumn!);
        }

        if (detail && amountIndex < 0)
        {
            missing.Add(layout.AmountColumn!);
        }

        if (missing.Count > 0)
        {
            return Fail($"The register is missing the column(s) {string.Join(", ", missing)}. Add those headers to the layout or export the register with a header row.", headers, columns);
        }

        var used = columns.Where(c => c.Index is not null).Select(c => c.Index!.Value).Concat([codeIndex, amountIndex]).ToHashSet();
        var unused = headers.Where((h, i) => !used.Contains(i) && h.Length > 0).ToList();
        var codeMap = (layout.CodeMap ?? []).GroupBy(m => Key(m.Code)).ToDictionary(g => g.Key, g => g.First().Target);
        var unmapped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RegisterTestRowDto>();
        var parsed = new List<(int Line, string Id, string Name, DateOnly Date, decimal[] Amounts, bool HasNet)>();

        foreach (var (line, cells) in all.Skip(headerIndex >= 0 ? headerIndex + 1 : 1))
        {
            string Cell(int? index) => index is { } i && i >= 0 && i < cells.Count ? cells[i].Trim() : "";
            string Of(RegisterField f) => Cell(columns[(int)f].Index);
            var id = Of(RegisterField.EmployeeId);
            if (id.Length == 0)
            {
                // Totals and blank rows at the end of a report.
                continue;
            }

            var name = Of(RegisterField.EmployeeName);
            var dateText = Of(RegisterField.CheckDate);
            if (!DateOnly.TryParseExact(dateText, [.. layout.DateFormats], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                rows.Add(Bad(line, id, name, $"\"{dateText}\" isn't a check date ({string.Join(" or ", layout.DateFormats)})."));
                continue;
            }

            var amounts = new decimal[6];
            string? problem = null;
            var hasNet = false;
            if (detail)
            {
                var code = Cell(codeIndex);
                var target = codeMap.TryGetValue(Key(code), out var t) ? t : (RegisterAmountTarget?)null;
                if (target is null)
                {
                    unmapped.Add(code.Length == 0 ? "(blank)" : code);
                    target = RegisterAmountTarget.Ignore;
                }

                if (target != RegisterAmountTarget.Ignore)
                {
                    var text = Cell(amountIndex);
                    if (text.Length > 0 && !TryMoney(text, out var amount))
                    {
                        problem = $"\"{text}\" isn't an amount.";
                    }
                    else if (text.Length > 0)
                    {
                        amounts[(int)target] += TryMoney(text, out var a) ? a : 0;
                        hasNet = target == RegisterAmountTarget.NetPay;
                    }
                }
            }
            else
            {
                var fields = new[] { RegisterField.GrossPay, RegisterField.EmployeeTaxes, RegisterField.EmployerTaxes, RegisterField.Deductions, RegisterField.EmployerBenefits, RegisterField.NetPay };
                for (var i = 0; i < fields.Length && problem is null; i++)
                {
                    var text = Of(fields[i]);
                    if (text.Length == 0)
                    {
                        if (fields[i] is RegisterField.GrossPay or RegisterField.NetPay)
                        {
                            problem = $"{Label(fields[i])} is blank.";
                        }

                        continue;
                    }

                    if (!TryMoney(text, out var amount))
                    {
                        problem = $"\"{text}\" isn't an amount ({Label(fields[i])}).";
                        continue;
                    }

                    amounts[i] = amount;
                }

                hasNet = true;
            }

            if (problem is not null)
            {
                rows.Add(Bad(line, id, name, problem));
                continue;
            }

            parsed.Add((line, id, name, date, amounts, hasNet));
        }

        // Several rows for one employee and check date (earnings detail, or a split check) are added together.
        var warnings = new List<string>();
        var merged = parsed.GroupBy(p => (Id: p.Id.ToUpperInvariant(), p.Date)).Select(g =>
        {
            var sum = new decimal[6];
            foreach (var p in g)
            {
                for (var i = 0; i < 6; i++)
                {
                    sum[i] += p.Amounts[i];
                }
            }

            if (detail && !g.Any(p => p.HasNet))
            {
                sum[5] = sum[0] - sum[1] - sum[3];
            }

            var first = g.First();
            return new RegisterTestRowDto(first.Line, true, first.Id, g.Select(p => p.Name).FirstOrDefault(n => n.Length > 0) ?? "", first.Date,
                sum[0], sum[1], sum[2], sum[3], sum[4], sum[5], g.Count() > 1 ? $"{g.Count()} rows combined (lines {string.Join(", ", g.Select(p => p.Line))})." : null);
        }).ToList();
        if (merged.Count < parsed.Count)
        {
            warnings.Add($"{parsed.Count - merged.Count} row(s) were combined with another row for the same employee and check date.");
        }

        if (detail && parsed.Count > 0 && !parsed.Any(p => p.HasNet))
        {
            warnings.Add("No code is mapped to net pay; net pay is gross − employee taxes − deductions.");
        }

        if (unmapped.Count > 0)
        {
            warnings.Add($"Codes not in the code map were ignored: {string.Join(", ", unmapped)}.");
        }

        rows.AddRange(merged);
        rows.Sort((a, b) => a.Line.CompareTo(b.Line));
        var good = rows.Where(r => r.Parsed).ToList();
        return new RegisterTestResultDto(good.Count > 0 && rows.All(r => r.Parsed), good.Count == 0 && rows.Count == 0 ? "The register has no employee rows." : null,
            headers, columns, unused, rows, unmapped.ToList(), warnings, good.Count, good.Sum(r => r.GrossPay ?? 0), good.Sum(r => r.NetPay ?? 0));
    }

    private static RegisterTestRowDto Bad(int line, string id, string name, string message) => new(line, false, id, name, null, null, null, null, null, null, null, message);

    private static int Find(RegisterLayoutDto layout, RegisterField field, IReadOnlyList<string> headers)
    {
        var accepted = layout.Columns.Where(c => c.Field == field).SelectMany(c => c.Headers).Select(Key).Where(k => k.Length > 0).ToList();
        foreach (var alias in accepted)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (Key(headers[i]) == alias)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string Label(RegisterField field) => field switch
    {
        RegisterField.EmployeeId => "Employee Id",
        RegisterField.EmployeeName => "Employee Name",
        RegisterField.CheckDate => "Check Date",
        RegisterField.GrossPay => "Gross Pay",
        RegisterField.EmployeeTaxes => "EE Taxes",
        RegisterField.EmployerTaxes => "ER Taxes",
        RegisterField.Deductions => "Deductions",
        RegisterField.EmployerBenefits => "ER Benefits",
        _ => "Net Pay",
    };

    private static string Key(string header) => new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryMoney(string text, out decimal value)
    {
        var t = text.Replace("$", "").Replace(",", "").Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative)
        {
            t = t[1..^1];
        }

        if (t.EndsWith('-'))
        {
            negative = !negative;
            t = t[..^1];
        }

        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Round(negative ? -value : value, 2, MidpointRounding.AwayFromZero);
            return true;
        }

        return false;
    }

    /// <summary>RFC 4180 records with any single-character delimiter: quoted fields, doubled quotes, CRLF or LF line ends.</summary>
    public static IEnumerable<List<string>> ReadDelimited(string content, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else if (c != '\r' && c != '﻿')
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}

/// <summary>
/// Calls a provider with the company's saved layout when it has one (Paylocity), otherwise the adapter's fixed format.
/// <see cref="PayrollRunService"/> exports and imports through these.
/// </summary>
public static class PayrollLayoutDispatch
{
    public static async Task<PayrollExportFile> ExportHoursAsync(this IPayrollProvider provider, IWorkbenchDbContext db, PayrollHoursExport export, CancellationToken ct)
    {
        if (!PayrollLayouts.IsConfigurable(provider.Key))
        {
            return provider.ExportHours(export);
        }

        var (hours, _, _) = await PayrollLayouts.LoadAsync(db, provider.Key, ct);
        var standard = provider.ExportHours(export);
        var extension = PayrollLayouts.Delimiter(hours.Delimiter) == '\t' ? ".txt" : ".csv";
        return standard with { FileName = Path.ChangeExtension(standard.FileName, extension), Content = PayrollLayouts.WriteHours(export, hours) };
    }

    public static async Task<ParsedPayrollRegister> ParseRegisterAsync(this IPayrollProvider provider, IWorkbenchDbContext db, string content, CancellationToken ct)
    {
        if (!PayrollLayouts.IsConfigurable(provider.Key))
        {
            return provider.ParseRegister(content);
        }

        var (_, register, _) = await PayrollLayouts.LoadAsync(db, provider.Key, ct);
        return PayrollLayouts.ParseRegister(content, register);
    }
}

/// <summary>Saves and tests payroll file layouts. Finance/Admin (see <c>PermissionCheckingPayrollLayoutService</c>).</summary>
public sealed class PayrollLayoutService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IPayrollLayoutService
{
    private static void Require(string providerKey)
    {
        if (!PayrollLayouts.IsConfigurable(providerKey))
        {
            throw new ArgumentException($"File layouts can be configured for Paylocity; '{providerKey}' uses its adapter's fixed format.");
        }
    }

    public async Task<PayrollLayoutDto> GetAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        Require(providerKey);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (hours, register, row) = await PayrollLayouts.LoadAsync(db, providerKey, cancellationToken);
        return new PayrollLayoutDto(providerKey, hours, register, row?.HoursLayoutJson is null, row?.RegisterLayoutJson is null, row?.UpdatedBy, row?.UpdatedAtUtc);
    }

    public Task<PayrollLayoutDto> GetDefaultAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        Require(providerKey);
        return Task.FromResult(new PayrollLayoutDto(providerKey, PayrollLayouts.DefaultHours, PayrollLayouts.DefaultRegister, true, true, null, null));
    }

    public async Task<PayrollLayoutDto> SaveAsync(string providerKey, SavePayrollLayoutRequest request, CancellationToken cancellationToken = default)
    {
        Require(providerKey);
        PayrollLayouts.Validate(request.Hours, request.Register);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var row = await db.Set<PayrollFileLayout>().FirstOrDefaultAsync(l => l.ProviderKey == providerKey, cancellationToken);
            var before = row is null ? null : new { row.HoursLayoutJson, row.RegisterLayoutJson };
            if (row is null)
            {
                row = new PayrollFileLayout { ProviderKey = providerKey, UpdatedBy = audit.Actor };
                db.Set<PayrollFileLayout>().Add(row);
            }

            row.HoursLayoutJson = PayrollLayouts.Serialize(request.Hours);
            row.RegisterLayoutJson = PayrollLayouts.Serialize(request.Register);
            row.UpdatedBy = audit.Actor;
            row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            audit.Record(db, nameof(PayrollFileLayout), row.Id, "Updated", before: before, after: new { row.HoursLayoutJson, row.RegisterLayoutJson });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(providerKey, cancellationToken);
    }

    public async Task<PayrollLayoutDto> ResetAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        Require(providerKey);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<PayrollFileLayout>().FirstOrDefaultAsync(l => l.ProviderKey == providerKey, cancellationToken) is { } row)
            {
                db.Set<PayrollFileLayout>().Remove(row);
                audit.Record(db, nameof(PayrollFileLayout), row.Id, "Reset", before: new { row.HoursLayoutJson, row.RegisterLayoutJson });
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return await GetAsync(providerKey, cancellationToken);
    }

    public async Task<RegisterTestResultDto> TestRegisterAsync(string providerKey, TestRegisterSampleRequest request, CancellationToken cancellationToken = default)
    {
        var layout = request.Layout ?? (await GetAsync(providerKey, cancellationToken)).Register;
        if (request.Layout is not null)
        {
            Require(providerKey);
        }

        try
        {
            PayrollLayouts.Validate(PayrollLayouts.DefaultHours, layout);
        }
        catch (ArgumentException ex)
        {
            return new RegisterTestResultDto(false, ex.Message, [], [], [], [], [], [], 0, 0, 0);
        }

        return PayrollLayouts.TestRegister(request.Content, layout);
    }

    public async Task<HoursTemplateTestDto> TestHoursTemplateAsync(string providerKey, TestHoursTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var layout = request.Layout ?? (await GetAsync(providerKey, cancellationToken)).Hours;
        Require(providerKey);
        try
        {
            PayrollLayouts.Validate(layout, PayrollLayouts.DefaultRegister);
        }
        catch (ArgumentException ex)
        {
            return new HoursTemplateTestDto(false, ex.Message, [], [], "");
        }

        return PayrollLayouts.TestTemplate(request.Content, layout);
    }
}

public static class PayrollLayoutRegistration
{
    /// <summary>Configurable payroll file layouts (Payroll › Providers). Above AddWorkbenchIdentity.</summary>
    public static IServiceCollection AddWorkbenchPayrollLayouts(this IServiceCollection services)
    {
        services.AddScoped<IPayrollLayoutService, PayrollLayoutService>();
        return services;
    }
}
