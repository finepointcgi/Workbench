using Workbench.Contracts.Crm;

namespace Workbench.Ui.Crm;

/// <summary>The editable copy of an account behind the form.</summary>
public sealed class AccountModel
{
    public string Name { get; set; } = "";
    public CrmAccountKind Kind { get; set; } = CrmAccountKind.Agency;
    public string? Acronym { get; set; }
    public Guid? ParentId { get; set; }
    public string? Uei { get; set; }
    public string? CageCode { get; set; }
    public string? Website { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;

    public static AccountModel From(CrmAccountDto a) => new()
    {
        Name = a.Name, Kind = a.Kind, Acronym = a.Acronym, ParentId = a.ParentId, Uei = a.Uei, CageCode = a.CageCode, Website = a.Website,
        Phone = a.Phone, Address = a.Address, Notes = a.Notes, IsActive = a.IsActive,
    };

    public SaveCrmAccountRequest ToRequest() => new(Name, Kind, Acronym, ParentId, Uei, CageCode, Website, Phone, Address, Notes, IsActive);
}
