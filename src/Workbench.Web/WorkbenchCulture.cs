using System.Globalization;

namespace Workbench.Web;

/// <summary>
/// The culture the server reads and writes dates, numbers and money in: US English. A container has no operating-system locale, so
/// without this it runs in the invariant culture and every amount formatted with "C" shows "¤960,000" instead of "$960,000".
/// </summary>
public static class WorkbenchCulture
{
    public static CultureInfo Culture { get; } = CultureInfo.ReadOnly(new CultureInfo("en-US"));

    public static void Apply()
    {
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }
}
