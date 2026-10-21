using System.Globalization;
using System.Runtime.CompilerServices;

namespace Workbench.Tests;

/// <summary>
/// The suite runs in en-US whatever the machine's locale: messages format money and rates with the current culture, and
/// a CI runner without LANG runs in the invariant culture ("¤75.00", "5.00 %").
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseEnglishUnitedStates()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
