using Workbench.Application.Travel;
using Workbench.Contracts.Travel;

namespace Workbench.Tests.Travel;

public sealed class TravelCategoryTests
{
    [Theory]
    [InlineData("UBER *TRIP DCA", "4121", "Taxi DCA to hotel", TravelCategory.Other)] // the merchant code decides, not "hotel"
    [InlineData("DELTA AIR LINES", "3058", null, TravelCategory.Airfare)]
    [InlineData("MARRIOTT WASHINGTON DC", "3509", null, TravelCategory.Lodging)]
    [InlineData("HERTZ RENT-A-CAR", "3357", null, TravelCategory.RentalCar)]
    [InlineData("PANERA BREAD", "5814", null, TravelCategory.Mie)]
    [InlineData("Hilton Arlington", null, null, TravelCategory.Lodging)]
    [InlineData("Yellow Cab", null, "Taxi to the hotel", TravelCategory.Other)]
    [InlineData("Office Depot", "5943", null, TravelCategory.Other)]
    public void Charges_are_categorized_by_merchant_code_then_words(string merchant, string? mcc, string? description, TravelCategory expected) =>
        Assert.Equal(expected, TravelCategories.Guess(merchant, mcc, description));
}
