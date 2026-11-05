using Workbench.Ui.Theme;

namespace Workbench.Tests.Theme;

/// <summary>The Material 3 token set: complete in both schemes, mapped into the MudBlazor palette, readable (WCAG 2.1 AA), and the static stylesheet for public pages in step with it.</summary>
public sealed class M3TokenTests
{
    private static readonly string[] Roles =
    [
        "primary", "on-primary", "primary-container", "on-primary-container",
        "secondary", "on-secondary", "secondary-container", "on-secondary-container",
        "tertiary", "on-tertiary", "tertiary-container", "on-tertiary-container",
        "error", "on-error", "error-container", "on-error-container",
        "surface", "on-surface", "surface-variant", "on-surface-variant", "outline", "outline-variant",
        "surface-container-lowest", "surface-container-low", "surface-container", "surface-container-high", "surface-container-highest",
        "inverse-surface", "inverse-on-surface", "inverse-primary", "scrim",
    ];

    [Fact]
    public void Both_schemes_define_every_role_and_start_from_the_brand_seed()
    {
        foreach (var scheme in new[] { M3Tokens.Light, M3Tokens.Dark })
        {
            foreach (var role in Roles)
            {
                Assert.True(scheme.ContainsKey(role), role);
            }
        }

        Assert.Equal(M3Tokens.Seed, M3Tokens.Light["primary"]);
    }

    [Fact]
    public void The_mud_palette_is_filled_from_the_roles()
    {
        var theme = WorkbenchTheme.Default;
        Assert.Equal(M3Tokens.Light["primary"], theme.PaletteLight.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant());
        Assert.Equal(M3Tokens.Dark["primary"], theme.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant());
        Assert.Equal(M3Tokens.Light["surface-container-low"], theme.PaletteLight.DrawerBackground.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_roles_meet_AA_on_their_containers_and_surfaces(bool dark)
    {
        var c = M3Tokens.Scheme(dark);
        foreach (var family in new[] { "primary", "secondary", "tertiary", "error" })
        {
            Assert.True(Ratio(c[$"on-{family}"], c[family]) >= 4.5, $"on-{family} on {family}");
            Assert.True(Ratio(c[$"on-{family}-container"], c[$"{family}-container"]) >= 4.5, $"on-{family}-container");
        }

        foreach (var surface in new[] { "surface", "surface-container-low", "surface-container", "surface-container-high", "surface-container-highest" })
        {
            Assert.True(Ratio(c["on-surface"], c[surface]) >= 4.5, $"on-surface on {surface}");
            Assert.True(Ratio(c["on-surface-variant"], c[surface]) >= 4.5, $"on-surface-variant on {surface}");
            Assert.True(Ratio(c["primary"], c[surface]) >= 4.5, $"primary text on {surface}");
            Assert.True(Ratio(c["error"], c[surface]) >= 4.5, $"error text on {surface}");
        }

        Assert.True(Ratio(c["on-secondary-container"], c["surface-container-low"]) >= 3, "nav indicator against the drawer");
        Assert.True(Ratio(c["inverse-on-surface"], c["inverse-surface"]) >= 4.5, "snackbar");
        Assert.True(Ratio(c["outline"], c["surface"]) >= 3, "input outline");
        foreach (var status in new[] { "success", "warning", "info" })
        {
            Assert.True(Ratio(c[$"on-{status}"], c[status]) >= 4.5, $"on-{status}");
            Assert.True(Ratio(c[status], c["surface"]) >= 3, $"{status} icon");
        }
    }

    [Fact]
    public void The_public_pages_stylesheet_is_the_generated_one()
    {
        var path = Path.Combine(RepoRoot(), "src", "Workbench.Web", "wwwroot", "css", "m3-tokens.css");
        var expected = M3Tokens.ToStaticStylesheet();
        if (Environment.GetEnvironmentVariable("WORKBENCH_UPDATE_TOKENS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.Equal(expected.ReplaceLineEndings("\n"), File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static double Ratio(string a, string b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo)
        {
            (hi, lo) = (lo, hi);
        }

        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var v = Convert.ToInt32(hex.Substring(1 + (i * 2), 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(1)) + (0.0722 * Channel(2));
    }
}
