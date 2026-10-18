using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>MultiTenant: the Data Protection key ring lives in the catalog database, shared by every server instance.</summary>
public sealed class CatalogXmlRepository(IDbContextFactory<CatalogDbContext> catalog) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = catalog.CreateDbContext();
        return db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml).ToList().Select(XElement.Parse).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = catalog.CreateDbContext();
        db.DataProtectionKeys.Add(new CatalogDataProtectionKey { FriendlyName = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting) });
        db.SaveChanges();
    }
}
