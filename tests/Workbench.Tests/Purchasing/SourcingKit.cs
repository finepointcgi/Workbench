using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Notifications;
using Workbench.Application.Purchasing;
using Workbench.Application.Purchasing.Edi;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;
using Workbench.Tests.Automation;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

/// <summary>The portal contact a test is acting as (null: nobody signed in to the portal).</summary>
internal sealed class FakeSupplierPrincipal : ISupplierPrincipal
{
    public Guid? ContactId { get; set; }

    public string? IpAddress => "203.0.113.7";
}

/// <summary>
/// <see cref="ProcurementKit"/> plus sourcing: a switchable supplier portal contact, a recording email sender and an EDI
/// file drop folder of its own. Helpers build RFQs, quotes and portal contacts.
/// </summary>
internal static class SourcingKit
{
    public static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public static async Task<(PurchasingKit Kit, FakeSupplierPrincipal Portal, RecordingEmailSender Mail, string EdiRoot)> CreateAsync()
    {
        var portal = new FakeSupplierPrincipal();
        var mail = new RecordingEmailSender();
        var ediRoot = Path.Combine(Path.GetTempPath(), $"workbench-edi-{Guid.NewGuid():N}");
        var kit = await ProcurementKit.CreateAsync(services =>
        {
            services.AddSingleton(portal);
            services.AddScoped<ISupplierPrincipal>(sp => sp.GetRequiredService<FakeSupplierPrincipal>());
            services.AddSingleton<IEmailSender>(mail);
            services.Configure<EdiOptions>(o => o.FileDropRoot = ediRoot);
        });
        return (kit, portal, mail, ediRoot);
    }

    public static IRfqService Rfqs(this PurchasingKit kit) => kit.Get<IRfqService>();

    public static ISupplierPortalService Portal(this PurchasingKit kit) => kit.Get<ISupplierPortalService>();

    public static ISupplierPortalAdminService PortalAdmin(this PurchasingKit kit) => kit.Get<ISupplierPortalAdminService>();

    public static IEdiService Edi(this PurchasingKit kit) => kit.Get<IEdiService>();

    public static SaveRfqLineRequest HandLine(this PurchasingKit kit, decimal quantity, string description = "Network switch", decimal? estimate = null) =>
        new(description, quantity, "EA", Today.AddDays(30), estimate, WbsElementId: kit.Fx.Wbs1, OdcType: OdcCostType.Equipment);

    /// <summary>finance@ creates and sends an RFQ to <paramref name="vendors"/>.</summary>
    public static async Task<RfqDto> OpenRfqAsync(this PurchasingKit kit, IReadOnlyList<SaveRfqLineRequest> lines, params VendorDto[] vendors)
    {
        kit.As(Finance);
        var rfq = await kit.Rfqs().CreateAsync(new SaveRfqRequest("Lab equipment", Today.AddDays(7), lines, vendors.Select(v => v.Id).ToList(), "Net 30", "HQ lab"));
        return await kit.Rfqs().IssueAsync(rfq.Rfq.Id);
    }

    /// <summary>finance@ records a quote: one price per RFQ line (null = no bid), same lead time.</summary>
    public static Task<RfqDto> QuoteAsync(this PurchasingKit kit, RfqDto rfq, VendorDto vendor, int leadDays, params decimal?[] prices) =>
        kit.Rfqs().RecordQuoteAsync(rfq.Rfq.Id, new SaveQuoteRequest(vendor.Id, Today, leadDays,
            rfq.Lines.Zip(prices).Select(x => new SaveQuoteLineRequest(x.First.Id, x.Second ?? 0, NoBid: x.Second is null)).ToList(), Today.AddDays(30)));

    public static async Task<SupplierContactDto> ContactAsync(this PurchasingKit kit, VendorDto vendor, string email, string name = "Sam Vendor")
    {
        kit.As(Finance);
        return await kit.PortalAdmin().AddContactAsync(new SaveSupplierContactRequest(vendor.Id, email, name, SendSignInLink: false));
    }

    public static string Sample(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "edi", fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"samples/edi/{fileName} not found above {AppContext.BaseDirectory}");
    }

    /// <summary>A minimal PDF (enough for the "%PDF-" check).</summary>
    public static MemoryStream Pdf() => new("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n"u8.ToArray());
}
