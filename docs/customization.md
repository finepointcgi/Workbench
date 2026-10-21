# Screen customization: form layouts, list views and role home pages

Administrators arrange Workbench's record forms and design each role's home page; everyone saves their own list views.
Nothing changes until someone edits a layout: with no saved layout every page renders exactly as it was built.

- **Admin › Customization › Layouts** (`/admin/customization/layouts`): the **Forms** tab (form layouts) and the
  **Home pages** tab (role home pages).
- On every converted list page: the view menu, **Columns**, **Filter**, **Group** and **Export** in the list's toolbar.
- On the home page: **Customize** (reorder and hide widgets).

Code: `Contracts/Customization`, `Application/Customization`, `Ui/Customization`, decorators in
`Application/Identity/CustomizationPermissionDecorators.cs`, API `Api/CustomizationEndpoints.cs` (`/api/v1/customization/…`,
closed to API keys), clients `ApiClient/HttpCustomizationServices.cs`, seed `Infrastructure/Persistence/CustomizationSeed.cs`.

## Who may do what

| Action | Who |
| --- | --- |
| See a form in its layout, use list views, rearrange your own home page | Everyone signed in |
| Save personal list views, pick your default view, export the view you see | Everyone signed in |
| Share a list view with a role or everyone, change a shared view | `customization.views.share` (Admin until the permission catalog lands) |
| Change form layouts and role home pages, preview them as a role | `customization.layouts.manage` (Admin until the permission catalog lands) |

Both keys are `CustomizationPermissions` constants. They are decided behind **`ICustomizationAccess`** (a seam,
`DefaultCustomizationAccess`, registered with `TryAdd`): it answers who is asking (`CustomizationCaller`: email, role keys
most specific first, the company they work in, the two permissions) and which field permission keys someone holds
(`GrantedAsync`, `GrantedToRoleAsync`). Until custom roles merge, field keys resolve through `FieldPermissionGrants`
(the built-in roles the permission catalog grants each key to; unknown keys are Admin only).

## Form layouts

A layout is a list of sections (key, heading) with fields in order, each optionally **hidden** or **required**. It can be
set for everyone, a role, a company, or a role in a company. The most specific one applies:

1. the caller's role in the company they work in (their roles in order: Admin, Finance, Project manager, Timekeeper,
   Resource manager, HR, Materials, Business development, Pricing; the first with a layout wins),
2. everyone in that company,
3. the role in every company,
4. everyone in every company,
5. otherwise the **built-in layout**: the page as built (`BuiltInForms`).

The company is the one picked in the app bar switcher (else the user's default company). Rules (`FormLayoutEngine`):

- A field the domain needs (`EntityFieldDto.IsRequired`) can never be hidden and is always required.
- Switches and computed figures (`Bool`, `Display`) can't be made required; a field can't be both hidden and required.
- A saved layout is brought in step with today's fields on every read: unknown fields drop out, fields added since go back
  next to their built-in neighbour, custom fields the layout doesn't place go at the end.
- A field whose `PermissionKey` the viewer lacks is hidden for them (never a field the domain needs).
- **The server checks requirements.** Every save of a customizable record runs through `FormRequirementGuard`
  (a `FormRequirementProxy<T>` around each save service, registered by `AddWorkbenchCustomization`): fields the
  caller's layout makes required must have a value in the save request, or the save is refused (400, "Start is required.").
  The service's own validation still runs afterwards, so a layout never relaxes a rule. Jobs and the system user aren't
  checked. Built-in field keys are the camel-case names of the save request's properties, which is how the guard reads them.
- Hidden fields keep their values: pages still send the loaded value back unchanged, so hiding a field never clears data.

### Converted forms

| Entity type | Page or dialog | Save request |
| --- | --- | --- |
| `Project` | Setup › Projects › Details (`Setup/ProjectPage.razor`) | `SaveProjectRequest` |
| `Contract` | Setup › Contracts › Details (`Setup/ContractPage.razor`) | `SaveContractRequest` |
| `Employee` | Setup › People, edit employee (`Setup/EmployeeEditor.razor`) | `SaveEmployeeRequest` |
| `Vendor` | Purchasing › Vendors, new/edit vendor (`Purchasing/VendorDialog.razor`) | `SaveVendorRequest` |
| `Customer` | Setup › Customers, new/edit customer (`Setup/CodeNameDialog.razor` with `EntityType`) | `SaveCustomerRequest` |
| `PurchaseOrder` | New PO and change order dialog, header (`Purchasing/Procurement/PurchaseOrderDialog.razor`) | `SavePurchaseOrderRequest` |
| `ApBill` | Purchasing › Bills › New/edit bill, header (`Purchasing/BillEditPage.razor`) | `SaveBillRequest` |
| `Invoice` | Billing › New invoice, step 1 (`Billing/NewInvoicePage.razor`) | `GenerateInvoiceRequest` |
| `ExpenseReport` | Expense report header dialog (`Odcs/ExpenseReportDialog.razor`; not the expense line dialog) | `SaveExpenseReportRequest` |
| `Part` | Materials › Parts, new/edit part (`Materials/PartDialog.razor`) | `SavePartRequest` |
| `Opportunity` | Business development › Opportunity › Details (`Crm/OpportunityDetailsForm.razor`) | `SaveOpportunityRequest` |

### Converting a form

```razor
<MudForm @ref="_form">
    <ConfigurableForm EntityType="@CustomizableEntities.Project" RecordId="_project?.Id" @ref="_layout">
        <FormField Key="code" Sm="4">                       @* domain-required: keeps its own Required="true" *@
            <MudTextField @bind-Value="_code" Label="Code" Required="true" />
        </FormField>
        <FormField Key="start" Sm="4" Context="field">      @* optional: an administrator may require it *@
            <MudDatePicker @bind-Date="_start" Label="Start" Required="field.Required" />
        </FormField>
        <FormContent After="type" Md="4">…helper buttons that aren't fields…</FormContent>
    </ConfigurableForm>
</MudForm>
```

- Declare every field once, in the built order, with the same key and grid widths it had (`Xs`/`Sm`/`Md`/`Lg`, `ItemClass`,
  `NewRow` for a deliberate gap). `When="…"` leaves out a field that only sometimes applies. `Section` and
  `SectionTitles` keep headings right before the layout has loaded. Add the field to `BuiltInForms` in the same order.
- After saving the record, call `_layout.SaveCustomFieldsAsync(id)`. A dialog that returns a request to its page takes a
  `CustomFieldValues` parameter, fills it with `CopyFrom(_layout)` on Save, and the page calls
  `ConfigurableForm.SaveCustomFieldsAsync(Services, entityType, id, values)` once the record exists.
- With no `IFormLayoutService` registered (bUnit tests), the form renders as declared.

## The field catalog

`Contracts/Customization/EntityFieldCatalog.cs` (`IEntityFieldCatalog`, `EntityFieldDto`) is shared with the custom
fields module, which owns it. This branch ships `BuiltInEntityFieldCatalog` (built-in fields only, `TryAdd`ed): it lists the
fields of each form (`BuiltInForms`) and the list-only types `Timesheet` and `SalesOrder`. Built-in values are read and saved
through each record's own service, so it answers null for `GetValueAsync` and refuses `SetValueAsync`; the custom fields
module's catalog stores custom values. Entity type keys are `CustomizableEntities`.

## List views

`ConfigurableList<T>` (on `MudDataGrid`) takes a `ListDefinition<T>`: the list key (`CustomizableLists`), the entity type
(for custom field columns), the record id and every column the viewer may see. A `ListColumn<T>` has a key, title, raw
`Value` (sorting, filters, grouping, export), optional `Display` text and `Cell` template, a `Kind`
(Text, Number, Money, Percent, Date, Bool), whether it's in the **standard view**, and an optional field `PermissionKey`.

- **Columns a person can't see are never offered**: row-level rules decide which columns the page puts in the definition
  (Projects has a money and a plain definition), field permissions drop columns whose key isn't granted, and custom field
  columns follow their catalog permission. A saved view that names a column someone can't see just shows them the rest
  (`ListViewEngine.Restrict`).
- Rows always come from the page's own permission-checked services; the page's own controls (status, vendor, "Mine")
  stay in the toolbar (`ToolBarContent`) and filter the rows first.
- Views hold columns, sorts, filters (every condition must match) and a group-by column. Quick search looks at the columns shown
  and isn't saved. Personal views belong to their owner; shared views (a role, or everyone) are changed by those who may share.
  Each person picks their own default view (`ListViewPreference`).
- **Export matches the view**: the screen and the file both come from `ListViewEngine.Apply` and `Visible`; the server
  renders the .xlsx/.csv with `ReportFiles` (formula injection neutralized) and records an `export.list_view` security event.
- Totals under a list use `Summary`, which gets the rows the view shows.

### Converted lists

| List key | Page |
| --- | --- |
| `projects` | Projects (`Projects/ProjectListPage.razor`) |
| `contracts` | Setup › Contracts (`Setup/ContractsPage.razor`) |
| `people` | People (`Hr/PeoplePage.razor`) |
| `vendors` | Purchasing › Vendors (`Purchasing/VendorsPage.razor`) |
| `purchase-orders` | Purchasing › Purchase orders (`Purchasing/Procurement/PurchaseOrdersPage.razor`) |
| `bills` | Purchasing › AP bills (`Purchasing/BillsPage.razor`) |
| `invoices` | Billing › Invoices (`Billing/InvoicesPage.razor`) |
| `timesheets` | Time › Team time (`Time/TeamPage.razor`) |
| `expense-reports` | My expense reports (`Pages/Odcs/Expenses.razor`) |
| `opportunities` | Business development › Opportunities (`Crm/OpportunityListPage.razor`) |
| `parts` | Materials › Parts (`Materials/PartsPage.razor`) |
| `sales-orders` | Materials › Sales orders (`Sales/SalesOrdersPage.razor`) |

To add a list: give the page an `internal static readonly ListDefinition<T>`, render `<ConfigurableList>`, add the key to
`CustomizableLists`, and add a `ListSource<T>` to `Ui/Customization/ListSources.cs` so its saved views can be charted.

## Role home pages

A role's home page is an ordinary **dashboard** linked to the role (`RoleHomePage`), built from the dashboard widgets
(`WidgetType`) with the same `WidgetDialog`, `WidgetView` and widget data. Role home dashboards aren't listed under
Dashboards. People get the home page of their first role that has one, else Everyone's, else the **built-in home page**
(to do, this week, waiting on you, my sheet items), which renders exactly as before until someone customizes it.

| Widget | Shows |
| --- | --- |
| To do, This week, Waiting on you, My sheet items | The viewer's own (the existing home page cards) |
| KPI tile (`KpiKind`) | Funding remaining, utilization, AR aging, open POs, pipeline: computed by `HomeWidgetData` through the permission-checked services; a figure the viewer can't see shows a message instead |
| Report chart | A saved report-writer report, first text column by first numeric column |
| List view chart | A saved list view's rows counted by its group-by column (else its first column that groups), read through the list's own services (`ListSources`) |
| Shortcuts | Links to Workbench pages (app-relative only) |
| Announcement | A Markdown notice |

People reorder and hide widgets on their own page (`HomePagePreference`, also for the built-in page); a **pinned** widget
(`WidgetConfig.Pinned`) can't be hidden. New widgets appear after a person's own order. Removing a role's home page removes
everyone's changes to it.

## Seeded demo data

- A **"My open projects"** view on Projects, shared with everyone: projects you're on (`Mine` is yes) that aren't closed.
- A **Finance home page**: a pinned month-end announcement, KPI tiles (funding remaining, AR aging, open POs, utilization),
  to-dos (pinned), waiting on you, shortcuts, this week and a chart of the "My open projects" view. Pat Morgan (Finance) sees it.

## For the integrators

- **Custom fields**: keep your `EntityFieldCatalog.cs` and register your catalog; `BuiltInEntityFieldCatalog` is `TryAdd`ed.
  Your built-in field keys must match `BuiltInForms` (request property names, camel case) and your entity type keys
  `CustomizableEntities`. Mark fields the domain needs `IsRequired`. Your values API replaces
  `/api/v1/customization/entities/…` and `HttpEntityFieldCatalog` if you ship your own.
- **Custom roles**: register `customization.layouts.manage` and `customization.views.share` in the permission catalog
  (area Administration), then replace `ICustomizationAccess` with one that answers from `AccessControl.HasAsync`, adds
  custom roles to `RoleKeys` and `ListRolesAsync`, and grants field keys from the caller's permissions. Nothing else here
  uses `OrgRoles` checks.
