using System.Globalization;
using System.Text.RegularExpressions;
using Workbench.Contracts.Receipts;

namespace Workbench.Application.Receipts;

/// <summary>
/// Heuristics for the text of receipts and hotel folios (pure, no I/O). A folio gives the hotel, its city and state, the
/// arrival and departure dates, and its charge lines, which are sorted into room, lodging taxes and other charges
/// (parking, room service, phone, fees); payments and credits are ignored. Any receipt gives at least the vendor (first
/// heading line), a date and the total. What isn't found stays null for the person to type.
/// </summary>
public static partial class ReceiptParser
{
    private static readonly string[] DateFormats =
    [
        "M/d/yyyy", "M/d/yy", "M-d-yyyy", "yyyy-MM-dd", "d-MMM-yy", "d-MMM-yyyy", "d MMM yyyy", "d MMMM yyyy",
        "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy", "MMM. d, yyyy", "ddd M/d/yyyy",
    ];

    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "DC", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD",
        "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "PR", "GU", "VI",
    };

    public static ReceiptExtractionDto Parse(IReadOnlyList<string> rawLines)
    {
        var lines = rawLines.Select(l => Spaces().Replace(l.Replace('\t', ' '), m => m.Length > 1 ? "  " : " ").Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return ReceiptExtractionDto.Empty(["The file has no text to read (it may be a scan or a photo)."]);
        }

        var notes = new List<string>();
        var text = string.Join("\n", lines);
        var vendor = FindVendor(lines);
        var (city, state) = FindCityState(lines);
        var checkIn = LabeledDate(text, ArrivalLabel());
        var checkOut = LabeledDate(text, DepartureLabel());
        var rateLabel = LabeledAmount(lines, RateLabel());
        var nightsLabel = NightsLabel().Match(text) is { Success: true } nm ? int.Parse(nm.Groups["n"].Value, CultureInfo.InvariantCulture) : (int?)null;
        var total = FindTotal(lines);
        var charges = FindCharges(lines);

        var rooms = charges.Where(c => c.Kind == ReceiptChargeKind.Room).ToList();
        var hotelWords = HotelWords().IsMatch(text);
        var isLodging = rooms.Count > 0 || (checkIn is not null && checkOut is not null) || (hotelWords && (checkIn is not null || rateLabel is not null));

        DateOnly? date = LabeledDate(text, DateLabel());
        int? nights = null;
        decimal? nightly = null, roomTotal = null, taxes = null, other = null;
        if (isLodging)
        {
            if (checkIn is null && rooms.Count > 0 && rooms.All(r => r.Date is not null))
            {
                checkIn = rooms.Min(r => r.Date);
                checkOut ??= rooms.Max(r => r.Date)!.Value.AddDays(1);
            }

            nights = checkIn is { } a && checkOut is { } d && d > a ? d.DayNumber - a.DayNumber : nightsLabel ?? (rooms.Count > 0 ? rooms.Count : null);
            if (nightsLabel is { } labeled && nights is { } computed && labeled != computed)
            {
                notes.Add($"The folio says {labeled} nights but its dates give {computed}.");
            }

            roomTotal = rooms.Count > 0 ? rooms.Sum(r => r.Amount) : null;
            taxes = Sum(charges, ReceiptChargeKind.LodgingTax);
            other = charges.Any(c => c.Kind is not (ReceiptChargeKind.Room or ReceiptChargeKind.LodgingTax))
                ? charges.Where(c => c.Kind is not (ReceiptChargeKind.Room or ReceiptChargeKind.LodgingTax)).Sum(c => c.Amount)
                : 0m;
            var distinct = rooms.Select(r => r.Amount).Where(a => a > 0).Distinct().ToList();
            nightly = rateLabel
                ?? (distinct.Count == 1 ? distinct[0] : (decimal?)null)
                ?? (roomTotal is { } rt && nights is > 0 ? decimal.Round(rt / nights.Value, 2) : null);
            if (distinct.Count > 1)
            {
                notes.Add($"The room rate changes during the stay ({string.Join(", ", distinct.Select(Money))}); each night is checked on its own.");
            }

            if (roomTotal is null && nightly is { } n && nights is { } count)
            {
                roomTotal = n * count;
            }

            date ??= checkIn;
            if (total is null && roomTotal is not null)
            {
                total = roomTotal + (taxes ?? 0m) + (other ?? 0m);
                notes.Add("The folio has no total line; the total is the sum of its charges.");
            }
            else if (total is { } t && roomTotal is { } room && room + (taxes ?? 0m) + (other ?? 0m) is var sum && Math.Abs(sum - t) > 0.01m)
            {
                notes.Add($"The charges add up to {Money(sum)} but the folio total is {Money(t)}. Check the amounts.");
            }
        }
        else
        {
            date ??= FirstDate(text);
            if (total is null && charges.Count > 0)
            {
                total = charges.Sum(c => c.Amount);
                notes.Add("No total line was found; the total is the sum of the amounts on the receipt.");
            }
        }

        var found = new List<string>();
        void Mark(object? value, string field)
        {
            if (value is not null)
            {
                found.Add(field);
            }
        }

        Mark(vendor, ReceiptFields.Vendor);
        Mark(city, ReceiptFields.Location);
        Mark(date, ReceiptFields.Date);
        Mark(isLodging ? checkIn : null, ReceiptFields.CheckIn);
        Mark(isLodging ? checkOut : null, ReceiptFields.CheckOut);
        Mark(nights, ReceiptFields.Nights);
        Mark(nightly, ReceiptFields.NightlyRate);
        Mark(roomTotal, ReceiptFields.RoomTotal);
        Mark(isLodging ? taxes : null, ReceiptFields.LodgingTaxes);
        Mark(isLodging ? other : null, ReceiptFields.OtherCharges);
        Mark(total, ReceiptFields.Total);
        var method = found.Count == 0 ? ReceiptReadMethod.None : ReceiptReadMethod.TextLayer;
        return new ReceiptExtractionDto(
            isLodging ? ReceiptDocumentKind.LodgingFolio : ReceiptDocumentKind.Receipt,
            method, vendor, city, state, date,
            isLodging ? checkIn : null, isLodging ? checkOut : null, nights, nightly, roomTotal,
            isLodging ? taxes ?? 0m : null, isLodging ? other : null, total,
            isLodging ? charges : charges.Select(c => c with { Kind = ReceiptChargeKind.Other }).ToList(),
            found, found.ToDictionary(f => f, _ => ReceiptReadMethod.TextLayer), notes);
    }

    // ---- Dates and amounts ----

    public static DateOnly? ParseDate(string value)
    {
        var s = value.Trim().TrimEnd('.', ',');
        s = Spaces().Replace(s.Replace(",", ", "), " ").Replace(" ,", ",");
        foreach (var candidate in new[] { s, s.Replace(",", "") })
        {
            if (DateOnly.TryParseExact(candidate, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d))
            {
                return d.Year < 100 ? d.AddYears(2000) : d;
            }
        }

        return null;
    }

    /// <summary>Money amounts with two decimals ("1,234.56", "$45.00", "(10.00)" and "-10.00" are negative); integers such as room numbers and zip codes aren't amounts.</summary>
    public static IReadOnlyList<(decimal Value, int Index)> Amounts(string line)
    {
        var result = new List<(decimal, int)>();
        foreach (Match m in AmountToken().Matches(line))
        {
            var value = decimal.Parse(m.Groups["num"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
            var negative = m.Groups["neg"].Success || m.Groups["paren"].Success || m.Groups["cr"].Success;
            result.Add((negative ? -value : value, m.Index));
        }

        return result;
    }

    private static DateOnly? FirstDate(string text) =>
        DateToken().Matches(text).Select(m => ParseDate(m.Value)).FirstOrDefault(d => d is not null);

    private static DateOnly? LabeledDate(string text, Regex label)
    {
        foreach (Match m in label.Matches(text))
        {
            var rest = text[(m.Index + m.Length)..];
            var newline = rest.IndexOf('\n');
            var sameLine = newline < 0 ? rest : rest[..newline];
            if (DateToken().Match(sameLine) is { Success: true } d && d.Index <= 12 && ParseDate(d.Value) is { } date)
            {
                return date;
            }
        }

        return null;
    }

    private static decimal? LabeledAmount(IReadOnlyList<string> lines, Regex label)
    {
        foreach (var line in lines)
        {
            if (label.Match(line) is { Success: true } m && Amounts(line[(m.Index + m.Length)..]) is { Count: > 0 } amounts && amounts[0].Index <= 6)
            {
                return Math.Abs(amounts[0].Value);
            }
        }

        return null;
    }

    // ---- Vendor and place ----

    private static string? FindVendor(IReadOnlyList<string> lines)
    {
        var candidates = lines.Take(8)
            .Select(l => l.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0])
            .Where(l => l.Length >= 3 && l.Any(char.IsLetter) && !char.IsDigit(l[0]) && !NotAVendor().IsMatch(l) && !CityStateZip().IsMatch(l) && !DateToken().IsMatch(l))
            .ToList();
        var pick = candidates.FirstOrDefault(c => HotelWords().IsMatch(c)) ?? candidates.FirstOrDefault();
        if (pick is null)
        {
            return null;
        }

        pick = pick.Trim(' ', '*', '-', '=');
        if (pick.Length > 200)
        {
            pick = pick[..200];
        }

        // HILTON ARLINGTON → Hilton Arlington (an all-caps heading reads badly in a line description).
        return pick.Any(char.IsLower) ? pick : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(pick.ToLowerInvariant());
    }

    private static (string? City, string? State) FindCityState(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(20))
        {
            foreach (Match m in CityStateZip().Matches(line))
            {
                var state = m.Groups["state"].Value;
                if (!States.Contains(state) || (!m.Groups["zip"].Success && line.Length > m.Length + 4))
                {
                    continue;
                }

                var city = m.Groups["city"].Value.Trim();

                // "2399 Richmond Hwy Arlington" → "Arlington": drop a street address in front of the city.
                var words = city.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var lastStreetWord = Array.FindLastIndex(words, w => StreetWord().IsMatch(w));
                if (lastStreetWord >= 0 && lastStreetWord < words.Length - 1)
                {
                    city = string.Join(' ', words[(lastStreetWord + 1)..]);
                }

                if (city.Length >= 2 && !HotelWords().IsMatch(city))
                {
                    return (city.Any(char.IsLower) ? city : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(city.ToLowerInvariant()), state);
                }
            }
        }

        return (null, null);
    }

    // ---- Charges and totals ----

    private static decimal? FindTotal(IReadOnlyList<string> lines)
    {
        // Strongest label first: "Total charges" beats a bare "Total", which beats "Balance due" (often 0.00 once paid).
        foreach (var label in new[] { TotalChargesLabel(), TotalLabel(), AmountPaidLabel(), BalanceLabel() })
        {
            foreach (var line in lines)
            {
                if (label.Match(line) is { Success: true } m && !NotATotal().IsMatch(line)
                    && Amounts(line[(m.Index + m.Length)..]) is { Count: > 0 } amounts && Math.Abs(amounts[0].Value) > 0)
                {
                    return Math.Abs(amounts[0].Value);
                }
            }
        }

        return null;
    }

    private static List<ReceiptChargeDto> FindCharges(IReadOnlyList<string> lines)
    {
        var charges = new List<ReceiptChargeDto>();
        ReceiptChargeKind? previousKind = null;
        foreach (var line in lines)
        {
            var amounts = Amounts(line);
            if (amounts.Count == 0 || SummaryLine().IsMatch(line) || RateLabel().IsMatch(line) || HeaderLabels().IsMatch(line))
            {
                continue;
            }

            DateOnly? date = null;
            var start = 0;
            if (LeadingDate().Match(line) is { Success: true } d && ParseDate(d.Groups["date"].Value) is { } parsed)
            {
                date = parsed;
                start = d.Length;
            }

            var label = line[start..amounts[0].Index].Trim(' ', '$', '.', ':', '-');
            if (label.Length < 3 || !label.Any(char.IsLetter))
            {
                continue;
            }

            label = Spaces().Replace(label, " ");
            if (PaymentWords().IsMatch(label))
            {
                continue;
            }

            var kind = Classify(label, previousKind);
            previousKind = kind;
            charges.Add(new ReceiptChargeDto(date, label.Length > 120 ? label[..120] : label, amounts[0].Value, kind));
        }

        return charges;
    }

    private static ReceiptChargeKind Classify(string label, ReceiptChargeKind? previous)
    {
        if (TaxWords().IsMatch(label))
        {
            // "Restaurant tax" or the tax line straight after room service belongs to that charge, not to the room.
            if (FoodWords().IsMatch(label) || ParkingWords().IsMatch(label))
            {
                return FoodWords().IsMatch(label) ? ReceiptChargeKind.Food : ReceiptChargeKind.Parking;
            }

            return previous is ReceiptChargeKind.Food or ReceiptChargeKind.Parking && !RoomWords().IsMatch(label) && !LodgingTaxWords().IsMatch(label)
                ? previous.Value
                : ReceiptChargeKind.LodgingTax;
        }

        if (ParkingWords().IsMatch(label))
        {
            return ReceiptChargeKind.Parking;
        }

        if (FoodWords().IsMatch(label))
        {
            return ReceiptChargeKind.Food;
        }

        if (PhoneWords().IsMatch(label))
        {
            return ReceiptChargeKind.Phone;
        }

        if (FeeWords().IsMatch(label))
        {
            return ReceiptChargeKind.Fee;
        }

        return RoomWords().IsMatch(label) ? ReceiptChargeKind.Room : ReceiptChargeKind.Other;
    }

    private static decimal? Sum(IEnumerable<ReceiptChargeDto> charges, ReceiptChargeKind kind) =>
        charges.Where(c => c.Kind == kind).Select(c => (decimal?)c.Amount).Sum();

    private static string Money(decimal value) => value.ToString("C", CultureInfo.GetCultureInfo("en-US"));

    // ---- Patterns ----

    private const string DatePattern =
        @"(?:\d{4}-\d{2}-\d{2}|\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{1,2}-[A-Za-z]{3}-\d{2,4}|\d{1,2} [A-Za-z]{3,9} \d{4}|[A-Za-z]{3,9}\.? \d{1,2},? \d{4})";

    [GeneratedRegex(DatePattern)]
    private static partial Regex DateToken();

    [GeneratedRegex(@"^(?:(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*\.?\s+)?(?<date>" + DatePattern + @")\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingDate();

    [GeneratedRegex(@"(?<neg>-\s?)?(?<paren>\()?\$?\s?(?<num>\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2})\)?(?<cr>\s?CR\b)?(?![\d%])")]
    private static partial Regex AmountToken();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(?:arrival(?: date)?|arrive[ds]?|check[- ]?in(?: date)?|date in|in date)\s*[:#]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArrivalLabel();

    [GeneratedRegex(@"\b(?:departure(?: date)?|depart(?:ed|s)?|check[- ]?out(?: date)?|date out|out date)\s*[:#]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DepartureLabel();

    [GeneratedRegex(@"(?:^|\s{2}|\b)(?:invoice date|receipt date|bill date|folio date|date of (?:service|purchase)|transaction date|date)\s*[:#]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DateLabel();

    [GeneratedRegex(@"\b(?:room rate|daily rate|nightly rate|avg\.? rate|average rate|rate)\s*[:#]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RateLabel();

    [GeneratedRegex(@"\b(?:nights?|no\.? of nights|number of nights)\s*[:#]\s*(?<n>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex NightsLabel();

    [GeneratedRegex(@"\b(?:total charges|grand total|total due|total amount|amount due|invoice total|folio total|total for stay)\b\s*[:#]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TotalChargesLabel();

    [GeneratedRegex(@"(?<!sub)(?<!sub )\btotal\b\s*[:#]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLabel();

    [GeneratedRegex(@"\b(?:amount paid|you paid|total paid|amount charged|paid)\b\s*[:#]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPaidLabel();

    [GeneratedRegex(@"\bbalance(?: due)?\b\s*[:#]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceLabel();

    [GeneratedRegex(@"\b(?:sub\s?total|total (?:tax(?:es)?|credits?|payments?|room|lodging)|tax total|tip|gratuity|change due)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotATotal();

    [GeneratedRegex(@"^(?:\S+\s+)?(?:sub\s?total|total|balance|amount due|amount paid|grand total|you paid|change|tip|gratuity)\b|\b(?:total charges|balance due|total due)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"\b(?:folio|confirmation|conf\.?|account|acct|room|guest|membership|rewards|card|auth(?:orization)?|cashier|clerk|phone|tel|fax)\s*(?:no\.?|number|#)?\s*[:#]", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLabels();

    [GeneratedRegex(@"\b(?:visa|master\s?card|mc|amex|american express|discover|diners|payment|paid|deposit|cash|check|credit card|card \*|\*{2,}\d{4}|x{2,}\d{4}|refund|thank you|advance)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentWords();

    [GeneratedRegex(@"\b(?:tax|taxes|occupancy|tourism|tot\b|tmd|tid|assessment|levy)", RegexOptions.IgnoreCase)]
    private static partial Regex TaxWords();

    [GeneratedRegex(@"\b(?:occupancy|lodging|hotel|room|transient|tourism|tot\b|tmd|tid)", RegexOptions.IgnoreCase)]
    private static partial Regex LodgingTaxWords();

    [GeneratedRegex(@"\b(?:room(?! service)|accommodation|lodging|guest room|daily rate|night|nightly|king|queen|double|suite|rate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoomWords();

    [GeneratedRegex(@"\b(?:parking|valet|garage|self[- ]park)", RegexOptions.IgnoreCase)]
    private static partial Regex ParkingWords();

    [GeneratedRegex(@"\b(?:restaurant|room service|in-room dining|dining|bar|lounge|mini ?bar|breakfast|lunch|dinner|food|beverage|cafe|café|coffee|market|pantry|grill|bistro|starbucks|snack)", RegexOptions.IgnoreCase)]
    private static partial Regex FoodWords();

    [GeneratedRegex(@"\b(?:telephone|phone|internet|wi-?fi|long distance)", RegexOptions.IgnoreCase)]
    private static partial Regex PhoneWords();

    [GeneratedRegex(@"\b(?:resort fee|destination fee|amenity fee|facility fee|urban fee|service fee|fee)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeeWords();

    [GeneratedRegex(@"\b(?:hotel|inn|suites?|resort|lodge|motel|marriott|hilton|hyatt|sheraton|westin|hampton|courtyard|residence|embassy|holiday inn|doubletree|radisson|wyndham|best western|ritz|omni|fairfield|homewood|springhill|staybridge|aloft|kimpton)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HotelWords();

    [GeneratedRegex(@"\b(?:guest folio|folio|invoice|statement|receipt|tax id|page \d|tel\b|phone|fax|www\.|https?:|@|^thank|welcome|information|copy|duplicate)", RegexOptions.IgnoreCase)]
    private static partial Regex NotAVendor();

    [GeneratedRegex(@"(?<city>[A-Za-z][A-Za-z .'-]{1,40}),\s*(?<state>[A-Z]{2})\b(?:\s+(?<zip>\d{5}(?:-\d{4})?))?")]
    private static partial Regex CityStateZip();

    [GeneratedRegex(@"^(?:st|street|ave|avenue|hwy|highway|blvd|boulevard|rd|road|dr|drive|way|pkwy|parkway|ln|lane|ct|court|pl|place|sq|square|ste|suite|\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex StreetWord();
}
