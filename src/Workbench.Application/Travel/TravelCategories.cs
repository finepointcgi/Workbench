using System.Globalization;
using Workbench.Contracts.Travel;
using Workbench.Domain.PerDiem;

namespace Workbench.Application.Travel;

/// <summary>
/// Which trip cost category an expense line or card charge falls in, when the traveler hasn't chosen one: the per diem
/// line kind, else the card's merchant category code, else words in the merchant or description.
/// </summary>
public static class TravelCategories
{
    private static readonly string[] AirWords = ["airfare", "airline", "airlines", "airways", "flight", "flights", "delta", "united", "southwest", "jetblue", "frontier", "spirit"];
    private static readonly string[] LodgingWords = ["hotel", "hotels", "lodging", "inn", "motel", "marriott", "hilton", "hyatt", "sheraton", "westin", "resort", "suites", "airbnb"];
    private static readonly string[] CarWords = ["hertz", "avis", "enterprise", "alamo", "budget", "national", "thrifty", "dollar", "sixt", "zipcar"];
    private static readonly string[] GroundWords = ["taxi", "cab", "uber", "lyft", "parking", "shuttle", "amtrak", "train", "metro", "toll", "tolls", "mileage"];
    private static readonly string[] MealWords = ["restaurant", "cafe", "coffee", "starbucks", "grill", "diner", "bistro", "bar", "meal", "meals", "dinner", "lunch", "breakfast", "food", "deli", "pizza"];

    public static TravelCategory FromPerDiem(PerDiemLineKind kind, string description) => kind switch
    {
        PerDiemLineKind.Mie => TravelCategory.Mie,
        PerDiemLineKind.Unallowable when description.Contains("M&IE", StringComparison.OrdinalIgnoreCase) => TravelCategory.Mie,
        _ => TravelCategory.Lodging,
    };

    /// <summary>From an ISO 18245 merchant category code ("4511") or a category name ("Airlines", "Car rental").</summary>
    public static TravelCategory? FromMerchantCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        if (int.TryParse(category.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mcc))
        {
            return mcc switch
            {
                >= 3000 and <= 3299 or 4511 => TravelCategory.Airfare,
                >= 3351 and <= 3441 or 7512 => TravelCategory.RentalCar,
                >= 3501 and <= 3999 or 7011 => TravelCategory.Lodging,
                5811 or 5812 or 5813 or 5814 => TravelCategory.Mie,
                _ => TravelCategory.Other,
            };
        }

        return FromWords(category);
    }

    /// <summary>A numeric merchant category code decides; otherwise the words of the merchant, then of the description.</summary>
    public static TravelCategory Guess(string? merchant, string? merchantCategory, string? description = null) =>
        IsCode(merchantCategory) ? FromMerchantCategory(merchantCategory)!.Value
        : FromMerchantCategory(merchantCategory) is { } c and not TravelCategory.Other ? c
        : FromWords(merchant ?? "") ?? FromWords(description ?? "") ?? TravelCategory.Other;

    private static bool IsCode(string? category) => int.TryParse(category?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static TravelCategory? FromWords(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("rental car", StringComparison.Ordinal) || lower.Contains("car rental", StringComparison.Ordinal))
        {
            return TravelCategory.RentalCar;
        }

        var words = lower.Split([' ', ',', '.', '-', '/', '(', ')', '*', '#', '·', '&', ':'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (words.Contains("m&ie") || lower.Contains("m&ie", StringComparison.Ordinal) || lower.Contains("per diem meals", StringComparison.Ordinal))
        {
            return TravelCategory.Mie;
        }

        return words.Overlaps(GroundWords) ? TravelCategory.Other
            : words.Overlaps(AirWords) || lower.Contains("air lines", StringComparison.Ordinal) ? TravelCategory.Airfare
            : words.Overlaps(LodgingWords) ? TravelCategory.Lodging
            : words.Overlaps(CarWords) ? TravelCategory.RentalCar
            : words.Overlaps(MealWords) ? TravelCategory.Mie
            : null;
    }
}
