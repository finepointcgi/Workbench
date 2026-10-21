# Customization: custom fields, picklists, status labels and numbering

Administrators shape Workbench's data without code changes, under **Admin › Customization** (`/admin/customization`).

## Custom fields

Thirteen record types take custom fields: projects, contracts, WBS elements, employees, vendors, customers, purchase orders,
AP bills, invoices, expense reports, timesheets (header), parts and opportunities (keys in `CustomizableEntityTypes`).

| Type | Stored as | Entered as |
| --- | --- | --- |
| Text, long text | text (500 / 4,000 characters) | as typed; text is one line |
| Number, currency | decimal (18,4); currency rounds to the cent | `1234.5` (`$` and thousands separators accepted) |
| Date | date | `yyyy-MM-dd` |
| Yes / no | flag | `true`/`false`, `yes`/`no` |
| Picklist, multi-select | a value list's codes (multi: JSON array) | codes or labels; multi comma-separated |
| Person | the user's lower-case email (must be an active user) | email |
| Link to a record | the record's id (must exist, in the chosen record type) | id |
| Web address | absolute `http(s)` URL | URL |

Each field has a label, help text, required flag, default value (for new records), "read-only once approved" (projects and
contracts: closed; POs: issued; AP bills: approved/posted; invoices: approved; expense reports and timesheets: approved),
an active flag and a sensitive flag. **Retiring** a field hides it from records and forms; its values and audit history stay,
and restoring it brings them back. A field's key (`program_office`) never changes; its type can't change once it has values.

**Storage: one shared value table** (`CustomFieldValues`: field, record, typed `TextValue`/`NumberValue`/`DateValue`/`BoolValue`
and a lower-case `SearchText`) rather than a JSON column per entity:

- a record type opts in with one registration line and no schema change (`CustomizationRegistration`), so feature branches
  never touch each other's tables;
- typed columns filter and sort the same way on SQLite, PostgreSQL and InMemory, with plain LINQ (JSON queries differ per provider);
- retired fields keep their rows, and one index (`FieldId, SearchText`) serves search across every record type.

Where values show up:

- **Pages and dialogs**: `CustomFieldsSection` (the "Details" card, with Edit, and the record's sub-status) on every record page;
  `CustomFieldsEditor` in the vendor, part, purchase order, expense report and WBS dialogs (call `ValidateAsync` before the save,
  `SaveAsync(id)` after). Customers have a Details button on Setup › Customers.
- **Audit**: every change is an `AuditEvent` on the record's own history (`Custom fields changed`, before/after by key);
  sensitive values are recorded as "(sensitive value)", never the value.
- **Search and filter**: `ICustomFieldService.SearchAsync` (any record type) and `FilterAsync` (equals, not equals, contains,
  blank, not blank, at least, at most); "Find records by their details" on the Fields tab.
- **Export**: `ExportCsvAsync` (every record of a type with its values; Fields tab, `GET /api/v1/customization/values/{type}/export.csv`)
  and the **report writer**: sources whose rows are a customizable record (invoices, AP bills, projects/WBS, employees) offer
  each active custom field as a `cf.<key>` column, filter and sort.
- **API**: `/api/v1/customization/...` (in the API key "setup" area): definitions, values, validate, filter, table, search,
  export, copy, lists, statuses, numbering, and the entity field catalog.
- **AI assistant**: read tools `get_custom_fields` (a record's values) and `list_custom_field_matches` (search).
- **Cloning**: `CopyValuesAsync(type, from, to)` copies active fields' values onto a copy of a record. Workbench has no
  record-cloning feature yet; whichever adds one calls it after creating the copy.

### Permissions

Administrators define fields. A record's values follow the record: whoever can open it (its module's own permission-checked
`Get...Async`) reads them; its module's managing roles (`ManageRoles` in the registration, e.g. Finance/Admin for POs, HR/Finance
for employees) or the employee who owns it (timesheets, expense reports) or manages it (projects) change them. Lists, exports,
filters and search across records need the managing roles, else results are limited to records the user can open (checked one
by one, up to 200). Decorators: `Identity/CustomizationPermissionDecorators.cs`.

**Sensitive fields** carry the permission key `field.<entity>.<key>`. Whether the current user may see them is
`ICustomFieldAccessPolicy` (default `FinanceAdminCustomFieldAccessPolicy`: Finance and administrators). Without it the value is
masked (`••••••`) everywhere: record pages, the API, tables, CSV, the report writer, the assistant and the catalog; it can't be
written, filtered or searched. The custom roles permission catalog replaces the policy with
`services.Replace(ServiceDescriptor.Scoped<ICustomFieldAccessPolicy, ...>())` and lists the keys from
`ICustomFieldService.ListDefinitionsAsync` (`PermissionKey` on sensitive fields).

### Entity field catalog (for automations, forms and list views)

`Contracts/Customization/EntityFieldCatalog.cs` (`IEntityFieldCatalog`), implemented by `Application/Customization/EntityFieldCatalog.cs`
and registered with `Replace` so it wins over built-in-only stubs. Entity types are the kebab-case keys above. Built-in fields
are the entity's public scalar properties, keyed by camelCase property name (`status`, `projectManagerId`) with field types
`Text`, `Number`, `Date`, `YesNo`, `Picklist` (enums, options = names) and `RecordLink` (ids). Custom fields are `cf.<key>`,
`IsCustom = true`, `PermissionKey` set when sensitive. `SetValueAsync` writes custom fields through the validated, audited,
permission-checked service; built-in fields only where the entity's registration lists them in `EditableBuiltIns` (none today:
statuses and amounts change through their modules' rules), otherwise `ArgumentException`.

### Adding a record type

One statement in `CustomizationRegistration.AddWorkbenchCustomization`:

```csharp
services.AddCustomizableEntity(new CustomizableEntity<D.Purchasing.Subcontract>
{
    Key = "subcontract", Label = "Subcontract", LabelPlural = "Subcontracts",
    Title = s => new RecordTitle(s.Id, s.Number), Link = id => $"purchasing/subcontracts/{id}",
    ManageRoles = r => r.IsFinance(),
    CanReadAsync = CustomizableEntity.ReadVia<ISubcontractService>(async (s, id, ct) => await s.GetAsync(id, ct)),
});
```

Then drop `<CustomFieldsSection EntityType="subcontract" RecordId="..." />` on its page.

## Picklists (value lists)

Lists have codes (what records store, fixed once used) and labels (what people see). Built-in lists answer with their code
defaults until an administrator first edits them; "Reset to defaults" deletes the override. Codes stored on records can be turned
off but not removed. Lists that used to be hard-coded dropdowns:

| List | Was | Used by |
| --- | --- | --- |
| `vendor-socioeconomic` | `PurchasingCatalog.SocioEconomicCategories` | vendor dialog, `VendorService` validation, AP small-business report |
| `vendor-business-size` | `PurchasingCatalog.BusinessSizes` | vendor dialog (values normalized to the list's codes) |
| `tax-exemption-reasons` | inline array in the sales tax setup page | exemption certificates |

NCR defect codes were already an administrator-managed table (`DefectCode`, Quality › Inspection plans). Expense/ODC categories
and opportunity sources carry behavior (per diem rules, SAM.gov leads), so they are **label sets** (below), not lists: their
labels change, their values don't. Read lists in a unit of work with `ValueLists.ItemsAsync(db, key, ct)`.

## Status labels and sub-statuses

`StatusSetCatalog` lists the built-in value sets administrators may relabel: project, contract, purchase order, invoice, AP bill,
timesheet and expense report statuses, opportunity stages and sources, and expense/ODC categories. A label is display only:
the enum values, state machines, approvals, charging and reports are unchanged (test: `Relabeling_a_status_changes_only_its_label`).
Pages load labels with `StatusLabels.LoadAsync(Services)` and call `Label(set, value, fallback)` (the projects list and project page do today).

Sets that allow it take **sub-statuses** (project, contract, purchase order, invoice statuses; opportunity stages): informational
refinements of one built-in status, set per record (`SetSubStatusAsync`, audited on the record) and shown in its Details card.
A sub-status only applies while the record is still in its parent status.

## Numbering

`DocumentNumberTypes` lists the configurable document numbers, each with today's format as its default: PO, REQ, RCV, RFQ, V
(AP bills), PR (payment runs), ASN, invoices (`{contract}-{seq:4}`), SO, PK, SH, NCR, CAR, ECO, INS, DM, TA, OPP. Patterns
combine literal text with `{YYYY}` `{YY}` `{MM}` (document date), `{FY}` (fiscal year containing it), `{company}` (company code),
`{contract}` (invoices only) and exactly one `{seq:N}`. Sequences can be per company (`{company}` required) and restart each
year (`{YYYY}`, `{YY}` or `{FY}` required), so a pattern can never repeat a number. Saving checks the longest example against
the number column's length.

Allocation (`DocumentNumbers`):

- `NextAsync` / `AllocateAsync` (services that can inject it): a short unit of work on the `NumberSequences` row with an
  optimistic `Version` check, retried on conflict, so concurrent requests never share a number on InMemory, SQLite or
  PostgreSQL (tests run 24 in parallel on both databases). A failed document save leaves a gap.
- `NextInUnitOfWorkAsync(db, ...)` (static helpers, and invoices): the row is updated in the caller's unit of work, so numbers are
  gapless; a concurrent allocation makes that save fail with a concurrency error, never a duplicate. Invoices use it so invoice
  numbers have no gaps.
- A new sequence starts after the highest number already stored in its scope; a number taken by a record made some other way
  (imports, seeds) is skipped.
