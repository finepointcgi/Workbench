using System.Globalization;
using Workbench.Web;

namespace Workbench.Tests.Platform;

/// <summary>A server with no operating-system locale (a container) still shows money as dollars: the suite itself runs in en-US, which hid this.</summary>
public sealed class WorkbenchCultureTests
{
    [Fact]
    public void A_server_in_the_invariant_culture_formats_money_as_dollars_once_the_culture_is_applied()
    {
        var before = (CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture);
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("¤960,000.00", 960_000m.ToString("C")); // how a container formats before

            WorkbenchCulture.Apply();

            Assert.Equal("$960,000.00", 960_000m.ToString("C"));
            Assert.Equal("10/27/2026", new DateOnly(2026, 10, 27).ToString("d"));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture) = before;
        }
    }
}
