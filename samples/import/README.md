# Import samples

Example files for the import wizard (**Setup → Import**, `/setup/import`). They move a customer from Costpoint and
Smartsheet into Workbench: org units, employees with labor categories and cost rates, a customer and contract with
CLINs and funding, project 2001 with its WBS, bill rates and a budget, and a Smartsheet plan for 2001 whose rows link
to the WBS.

## How an import works

1. **Upload** a `.csv` or `.xlsx` export (Costpoint report title rows above the headers are skipped), or **connect** to
   Smartsheet with an API token and pick sheets. The token is encrypted with Data Protection, kept for at most 8 hours
   and discarded when the import finishes.
2. **Map**: headers are matched to fields automatically (case-, space- and punctuation-insensitive; Costpoint's own
   column names and common variants are aliases). Change any match; for Smartsheet also choose which columns to
   create and their types.
3. **Validate**: row-level errors (missing required values, bad dates or numbers, unknown references, duplicates).
4. **Dry run**: what will be created, updated and skipped. Nothing changes yet.
5. **Import**: valid rows go through the regular services, so their validation, permissions and audit log apply.
   Rows with errors are skipped and listed.
6. **Roll back** from the history: deletes what the job created and restores what it changed, unless something was
   changed or started referencing those records since (the reasons are listed; roll back later jobs first).

Imports are repeatable: rows match on natural keys (employee id, org id, PLC, customer id, contract number, contract +
CLIN, project/WBS code, contract + PLC + date, project + budget version name, sheet name and Smartsheet row id), so
importing a file again updates changed records and skips unchanged ones. Finance and administrators only.

## Costpoint order

Import in dependency order: `organizations.csv`, `labor-categories.csv`, `employees.csv`, `cost-rates.csv`,
`customers.csv`, `contracts.csv`, `clins.csv`, `projects.csv`, `bill-rates.csv`, `budgets.csv`, then `fixed-assets.csv`
(after the GL balances; see below). Dates may be
`MM/DD/YYYY` (Costpoint's default), `YYYY-MM-DD` or `DD-MON-YYYY`; amounts may contain `$` and thousands separators.

Notes:

- **Employees**: a `Last, First` name is turned around. A PLC Workbench doesn't have yet is created. An hourly rate
  becomes an effective-dated cost rate (effective date, else hire date). Supervisors are set after every row exists.
- **Cost rates** are append-only: the same employee and date with a different amount is an error (add a correction
  with a new date).
- **Contracts** with CLINs take their value and funding from the CLINs.
- **CLINs**: the funded column is cumulative. The difference from Workbench's funded value is obligated (or
  de-obligated) through one executed modification per contract (`IMP-xxxxxx`), as are option exercises and value
  changes on funded CLINs. SLINs (`0001AA`) need their CLIN in the file or in Workbench.
- **Projects**: a top-level id (`2001`) is the project; `2001.01` and `2001.01.02` are WBS elements under the element of
  their dotted prefix. `ALLOW_CHARGES_FL` defaults to Y on the lowest level; Direct elements require charge
  assignments by default, Indirect and Leave don't. `CLIN_ID` maps an element to a CLIN of the project's contract.
- **Budgets**: rows with a PLC are labor hours (priced at Workbench's planning rates), rows without are ODC dollars.
  `FY_CD` + `PD_NO` (or a `yyyy-MM` period) phase a row into a fiscal period; `TOTAL` or blank is unphased. Lines go
  into the version named in the options (default "Costpoint budget", a Current version) and replace its lines.
  The sample uses fiscal year 2027 (Oct 2026 – Sep 2027); adjust `FY_CD` if your fiscal calendar doesn't have it.
- **Fixed assets (opening balances)**: each asset comes over with its cost, in-service date and accumulated
  depreciation (or net book value; with both, cost − accumulated must equal it) as of the cut-over date, which is a
  month end. **Nothing is posted**: the cost and accumulated depreciation are already in the asset and accumulated
  depreciation accounts through the GL balance import. Workbench depreciates from the month after the cut-over and
  spreads what's left (cost − salvage − accumulated) over the rest of the life in the asset's method, so the first
  depreciation run catches up any months since the cut-over once and never repeats months Costpoint already took. An
  asset whose life ended with value left above salvage is an error (fix its life, salvage or accumulated
  depreciation). The category must exist in Workbench (Fixed assets › Asset categories); life, method and convention
  default from it. Life may be in months (whole years only). Re-importing updates an asset until depreciation has been
  posted on it; a job rolls back until then too. Government property (GFP/CAP) isn't imported here; enter it on the
  register. The sample is as of 2026-09-30, so the October 2026 run depreciates 1,566.67.
- **Smartsheet**: TEXT_NUMBER becomes Text or Number/Currency, DATE Date, CHECKBOX Checkbox, PICKLIST Dropdown with its
  options, CONTACT_LIST Person (email). Duration fills a missing finish date (working days). Predecessors (`3FS +2d`,
  `4SS -1d`, `9FF +1w`) become Gantt dependencies with lag in working days. In an `.xlsx` export the row hierarchy comes
  from the outline (grouping) level, or else from the indentation of the cells. Choose a project and map a WBS column
  to link rows to the project's WBS elements and make the sheet its plan sheet.

## Expected columns

### Organizations

Costpoint organizations become org units. Dotted org ids (1.01.10) nest under their parent.

Sample: [`organizations.csv`](costpoint/organizations.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Org ID | yes | `OrgId`, `Org ID`, `ORG_ID`, `Org`, `Organization`, `Organization ID`, `Org Code` | Costpoint org id, e.g. 1.01.10. |
| Org name | yes | `Name`, `Org name`, `ORG_NAME`, `Organization Name`, `ORG_DESC`, `Description` | Display name. |
| Parent org |  | `ParentOrgId`, `Parent org`, `PARENT_ORG_ID`, `Parent Org`, `Parent` | Parent org id. Defaults to the dotted prefix when that org exists. |

### Labor categories (PLCs)

Project labor categories, used for budgets and bill rates.

Sample: [`labor-categories.csv`](costpoint/labor-categories.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| PLC | yes | `Code`, `PLC`, `BILL_LAB_CAT_CD`, `LAB_CAT_CD`, `PLC Code`, `GENL_LAB_CAT_CD`, `Labor Category`, `LCAT`, `Labor Category Code` | Labor category code. |
| Description | yes | `Name`, `Description`, `BILL_LAB_CAT_DESC`, `LAB_CAT_DESC`, `PLC Description`, `GENL_LAB_CAT_DESC`, `Labor Category Name` | Labor category name. |
| Active |  | `Active`, `ACTIVE_FL`, `Active Flag`, `Status` | Y/N. Defaults to Y. |

### Employees

Employees with their home org, labor category (PLC), supervisor and current hourly cost rate.

Sample: [`employees.csv`](costpoint/employees.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Employee ID | yes | `EmployeeNumber`, `Employee ID`, `EMPL_ID`, `Emp ID`, `Employee Number`, `Employee`, `Empl Id` | Natural key: the Costpoint employee id. |
| Name |  | `Name`, `EMPL_NAME`, `LAST_FIRST_NAME`, `Employee Name`, `Full Name` | Full name; \"Last, First\" is turned around. Or map first and last name. |
| First name |  | `FirstName`, `First name`, `FIRST_NAME`, `First`, `Given Name` |  |
| Last name |  | `LastName`, `Last name`, `LAST_NAME`, `Last`, `Surname`, `Family Name` |  |
| Email | yes | `Email`, `EMAIL_ID`, `EMAIL`, `Email Address`, `Work Email`, `E-mail` | Work email; links the employee to their sign-in. |
| Home org |  | `OrgId`, `Home org`, `ORG_ID`, `Home Org`, `Org`, `Organization` | Org id of an existing org unit. |
| PLC |  | `LaborCategory`, `PLC`, `BILL_LAB_CAT_CD`, `GENL_LAB_CAT_CD`, `LAB_CAT_CD`, `Labor Category` | Default labor category code; created when missing. |
| PLC description |  | `LaborCategoryName`, `PLC description`, `BILL_LAB_CAT_DESC`, `GENL_LAB_CAT_DESC`, `PLC Description`, `Labor Category Name` | Name for a PLC created by the import. |
| Hire date | yes | `HireDate`, `Hire date`, `ORIG_HIRE_DT`, `HIRE_DT`, `ADJ_HIRE_DT`, `Hire Date`, `Start Date` |  |
| Termination date |  | `TerminationDate`, `Termination date`, `TERM_DT`, `Termination Date`, `End Date` |  |
| Supervisor |  | `Supervisor`, `SUPERVISOR_EMPL_ID`, `MGR_EMPL_ID`, `Manager`, `Manager ID`, `Supervisor ID` | Supervisor's employee id (in the file or in Workbench). |
| Standard hours/week |  | `StandardHours`, `Standard hours/week`, `STD_EST_HRS`, `STD_HRS`, `Hours Per Week`, `Standard Hours` | Defaults to 40. |
| Exempt |  | `Exempt`, `EXMPT_FL`, `EXEMPT_FL`, `FLSA Exempt` | Y/N. Defaults to Y. |
| Hourly cost |  | `HourlyCost`, `Hourly cost`, `HRLY_AMT`, `Hourly Rate`, `Cost Rate`, `Hourly Cost`, `LAB_COST_AMT` | Hourly cost rate; added as an effective-dated rate. |
| Rate effective |  | `RateEffective`, `Rate effective`, `EFFECT_DT`, `Effective Date`, `Rate Effective Date`, `Rate Start` | Effective date of the hourly cost; defaults to the hire date. |

### Employee cost rates

Effective-dated hourly cost rates (Employee Salary Info). Rates are append-only: an existing date with a different amount is an error.

Sample: [`cost-rates.csv`](costpoint/cost-rates.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Employee ID | yes | `EmployeeNumber`, `Employee ID`, `EMPL_ID`, `Emp ID`, `Employee Number`, `Employee` |  |
| Effective date | yes | `EffectiveDate`, `Effective date`, `EFFECT_DT`, `Effective Date`, `START_DT`, `Start Date`, `Rate Effective Date` |  |
| Hourly cost | yes | `HourlyCost`, `Hourly cost`, `HRLY_AMT`, `Hourly Rate`, `Cost Rate`, `Hourly Cost`, `LAB_COST_AMT`, `Rate` |  |
| Reason |  | `Reason`, `COMMENTS`, `Note`, `S_SAL_CHG_RSN_CD`, `Change Reason` | Why the rate changed; defaults to the import job. |

### Customers

Agencies and primes you hold contracts with.

Sample: [`customers.csv`](costpoint/customers.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Customer ID | yes | `Code`, `Customer ID`, `CUST_ID`, `Customer`, `Customer Code`, `CUST_CD` | Natural key. |
| Customer name | yes | `Name`, `Customer name`, `CUST_NAME`, `CUST_LONG_NAME`, `Customer Name` |  |
| Category |  | `Category`, `CUST_TYPE`, `CUST_TYPE_CD`, `Customer Type`, `Agency Type` | For example DoD, Civilian, Commercial or Prime contractor. |

### Contracts

Contract headers: customer, type, period of performance, value and funding. Contracts with CLINs take their totals from the CLINs.

Sample: [`contracts.csv`](costpoint/contracts.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Contract number | yes | `ContractNumber`, `Contract number`, `PRIME_CONTR_ID`, `CONTRACT_ID`, `CONTR_ID`, `Contract`, `Contract No`, `Contract Number`, `Contract ID` | Natural key. |
| Contract name | yes | `Name`, `Contract name`, `CONTR_NAME`, `CONTRACT_NAME`, `CONTR_DESC`, `Contract Name`, `Description`, `Title` |  |
| Customer ID | yes | `Customer`, `Customer ID`, `CUST_ID` | Customer id of an existing customer. |
| Contract type | yes | `Type`, `Contract type`, `CONTR_TYPE`, `CONTR_TYPE_CD`, `PROJ_TYPE`, `Contract Type` | FFP, T&M, LH, CPFF, CPAF or CPIF. |
| Status |  | `Status`, `STATUS`, `S_CONTR_STATUS_CD`, `ACTIVE_FL` | Active, Pending or Closed (A/P/C, Y/N). Defaults to Active. |
| Prime |  | `IsPrime`, `Prime`, `PRIME_FL`, `PRIME_CONTR_FL`, `Is Prime` | Y when you're the prime. Defaults to Y. |
| Period start | yes | `Start`, `Period start`, `POP_START_DT`, `PERF_START_DT`, `START_DT`, `Start Date`, `Period Start`, `POP Start` |  |
| Period end | yes | `End`, `Period end`, `POP_END_DT`, `PERF_END_DT`, `END_DT`, `End Date`, `Period End`, `POP End` |  |
| Total value |  | `Ceiling`, `Total value`, `TOT_CONTR_VALUE`, `CONTR_VALUE_AMT`, `CEILING_AMT`, `TOT_VALUE_AMT`, `Total Value`, `Contract Value` | Ceiling / total contract value. |
| Funded |  | `Funded`, `TOT_FUNDED_AMT`, `FUNDED_AMT`, `Funded Value`, `Funded Amount` | Funded (obligated) to date. |

### CLINs and funding

Contract line items (and SLINs such as 0001AA) with value and cumulative funding. Funding changes are executed as one modification per contract.

Sample: [`clins.csv`](costpoint/clins.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Contract number | yes | `ContractNumber`, `Contract number`, `PRIME_CONTR_ID`, `CONTRACT_ID`, `CONTR_ID`, `Contract`, `Contract No`, `Contract Number` |  |
| CLIN | yes | `ClinNumber`, `CLIN`, `CLIN_ID`, `CLIN_NO`, `CLIN Number`, `Line Item`, `Item No`, `SLIN` | CLIN (0001) or SLIN (0001AA). |
| Description | yes | `Description`, `CLIN_DESC`, `CLIN_NAME`, `Line Item Description`, `Title` |  |
| Pricing |  | `Pricing`, `CLIN_TYPE`, `CLIN_TYPE_CD`, `Contract Type`, `Type` | FFP, T&M, LH, CPFF, CPAF, CPIF or CNF. Defaults to the contract's type. |
| Period start |  | `Start`, `Period start`, `POP_START_DT`, `START_DT`, `Start Date`, `Period Start` | Defaults to the contract's. |
| Period end |  | `End`, `Period end`, `POP_END_DT`, `END_DT`, `End Date`, `Period End` | Defaults to the contract's. |
| Value | yes | `Ceiling`, `Value`, `CLIN_VALUE_AMT`, `CLIN_VALUE`, `CEILING_AMT`, `TOT_VALUE_AMT`, `Total Value`, `Amount` | CLIN ceiling / total value. |
| Funded to date |  | `Funded`, `Funded to date`, `FUNDED_AMT`, `TOT_FUNDED_AMT`, `CLIN_FUNDED_AMT`, `Funded Value`, `Funded Amount` | Cumulative funded amount. |
| Fee % |  | `FeeRate`, `Fee %`, `FEE_RT`, `FEE_PCT`, `FEE_RATE`, `Fee Rate`, `Fee` | Fee as a percent (7) or fraction (0.07), for cost-plus CLINs. |
| Option |  | `IsOption`, `Option`, `OPTION_FL`, `Is Option` | Y for an option CLIN. |
| Exercised |  | `IsExercised`, `Exercised`, `EXERCISED_FL`, `Option Exercised` | Y when an option has been exercised. |
| Funding date |  | `FundingDate`, `Funding date`, `FUNDING_DT`, `FUND_DT`, `Funding Date`, `Effective Date` | Effective date of the funding change; defaults to today. |

### Bill rates (project PLC rates)

Hourly bill rates per labor category, effective-dated. Rows name a project (its contract gets the rate) or a contract.

Sample: [`bill-rates.csv`](costpoint/bill-rates.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Project ID |  | `ProjectId`, `Project ID`, `PROJ_ID`, `Project`, `Project Code` | Project (any WBS level); the rate goes to its contract. |
| Contract number |  | `ContractNumber`, `Contract number`, `PRIME_CONTR_ID`, `CONTRACT_ID`, `Contract`, `Contract Number` | Use instead of a project id. |
| PLC | yes | `LaborCategory`, `PLC`, `BILL_LAB_CAT_CD`, `LAB_CAT_CD`, `Labor Category` | Labor category code. |
| Bill rate | yes | `Rate`, `Bill rate`, `BILL_RT_AMT`, `BILL_RATE`, `BILL_RT`, `Bill Rate`, `Hourly Bill Rate`, `Hourly Rate` | Hourly bill rate. |
| Effective date | yes | `Start`, `Effective date`, `START_DT`, `EFFECT_DT`, `Effective Date`, `Start Date`, `Rate Start` |  |

### Projects and WBS

Projects with their full WBS tree (Costpoint project id levels such as 2001.01.02), charge flags, open/closed and direct vs. indirect.

Sample: [`projects.csv`](costpoint/projects.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Project ID | yes | `ProjectId`, `Project ID`, `PROJ_ID`, `Project`, `Project Code`, `Project Number`, `WBS`, `WBS Code` | Costpoint project id at any level: 2001 is the project, 2001.01.02 a WBS element. |
| Name | yes | `Name`, `PROJ_NAME`, `PROJ_DESC`, `Project Name`, `Description`, `Title` |  |
| Level |  | `Level`, `LVL_NO`, `Level No`, `PROJ_LVL_NO` | Costpoint level number; used only as a check against the id. |
| Active |  | `Active`, `ACTIVE_FL`, `Status`, `Open`, `S_PROJ_STATUS_CD` | Y = open/active, N = closed. Defaults to Y. |
| Allow charges |  | `AllowCharges`, `Allow charges`, `ALLOW_CHARGES_FL`, `ALLOW_CHRG_FL`, `Charge Allowed`, `Chargeable`, `Allow Charges`, `Charge Flag` | Y when time and costs may be charged. Defaults to Y on the lowest level. |
| Direct / indirect |  | `Indirect`, `Direct / indirect`, `PROJ_TYPE_DC`, `PROJ_TYPE`, `INDIRECT_FL`, `Project Type`, `Direct/Indirect`, `Charge Type`, `Type` | Direct, Indirect or Leave (also Y/N for an indirect flag). Defaults to the project's. |
| Restrict to assigned |  | `RequiresAssignment`, `Restrict to assigned`, `LIMIT_TO_EMPL_FL`, `Restrict Employees`, `Requires Assignment`, `Employee Restrictions` | Y when only assigned employees may charge. Defaults to Y for direct, N for indirect and leave. |
| Contract |  | `ContractNumber`, `Contract`, `PRIME_CONTR_ID`, `CONTRACT_ID`, `Contract Number` | Contract number of an existing contract (project level). |
| CLIN |  | `Clin`, `CLIN`, `CLIN_ID`, `SLIN`, `Line Item` | CLIN or SLIN this WBS element charges to. |
| Start |  | `Start`, `PROJ_START_DT`, `START_DT`, `POP_START_DT`, `Start Date` | Project start (project level). |
| End |  | `End`, `PROJ_END_DT`, `END_DT`, `POP_END_DT`, `End Date`, `Finish` | Project end (project level). |
| Project manager |  | `Manager`, `Project manager`, `PROJ_MGR_EMPL_ID`, `PM_EMPL_ID`, `PROJ_MGR_ID`, `Project Manager`, `PM` | Employee id (or email) of the project manager. |

### Budgets and EACs

Budget hours by WBS and PLC (and ODC dollars), whole-project or by fiscal period. Replaces the lines of the chosen budget version.

Sample: [`budgets.csv`](costpoint/budgets.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Project / WBS ID | yes | `WbsCode`, `Project / WBS ID`, `PROJ_ID`, `WBS`, `WBS Code`, `WBS ID`, `Project ID`, `Project` | The WBS element the amount is budgeted on (Costpoint project id at the budget level). |
| PLC |  | `LaborCategory`, `PLC`, `BILL_LAB_CAT_CD`, `LAB_CAT_CD`, `Labor Category` | Labor category; leave blank for ODC dollars. |
| Hours |  | `Hours`, `BUD_HRS`, `EAC_HRS`, `BUDGET_HRS`, `Budget Hours`, `EAC Hours` | Budget (or EAC) hours for labor rows. |
| Amount |  | `Amount`, `BUD_AMT`, `EAC_AMT`, `BUDGET_AMT`, `Budget Amount`, `EAC Amount`, `Cost` | Dollars for ODC rows (labor rows are priced from hours). |
| Fiscal year |  | `FiscalYear`, `Fiscal year`, `FY_CD`, `Fiscal Year`, `FY` | With the period number, phases the amount into a fiscal period. |
| Period |  | `Period`, `PD_NO`, `Fiscal Period`, `Month` | Period number (1–12) with the fiscal year, or a month as yyyy-MM. Blank or TOTAL = unphased. |
| ODC type |  | `OdcType`, `ODC type`, `ODC_TYPE`, `Cost Element`, `ACCT_DESC`, `Expense Type`, `ODC Type` | Travel, Materials, Equipment, Subcontract, Consultant or Other (ODC rows). |

### Fixed assets (opening balances)

Assets with cost, in-service date and accumulated depreciation (or net book value) as of the cut-over month end. Nothing is posted; depreciation continues from the month after.

Sample: [`fixed-assets.csv`](costpoint/fixed-assets.csv)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Asset number | yes | `AssetId`, `Asset number`, `ASSET_ID`, `ASSET_NO`, `Asset No`, `Asset ID`, `Tag`, `Tag Number`, `ASSET_ID_ITEM` | Becomes the asset tag; re-importing the same number updates the asset. |
| Description | yes | `Description`, `ASSET_DESC`, `ASSET_S_DESC`, `Asset Description`, `Name` | |
| Category | yes | `Category`, `ASSET_CLASS_CD`, `PROP_TYPE_CD`, `Asset Class`, `Property Type`, `Category Code`, `Class` | Workbench asset category code (COMP, FURN, LHI or your own). |
| In-service date | yes | `InServiceDate`, `In-service date`, `PL_IN_SERV_DT`, `IN_SERV_DT`, `Placed in Service`, `Service Date`, `Depreciation Start` | Date placed in service; depreciation started that month. |
| Acquisition date |  | `AcquisitionDate`, `Acquisition date`, `ACQ_DT`, `ACQUIRED_DT`, `Acquired`, `Purchase Date` | Defaults to the in-service date. |
| Acquisition cost | yes | `Cost`, `Acquisition cost`, `ACQ_COST_AMT`, `TOT_COST_AMT`, `COST_AMT`, `Original Cost`, `Book Cost` | Capitalized cost. |
| Accumulated depreciation |  | `AccumulatedDepreciation`, `Accumulated depreciation`, `ACCUM_DEPR_AMT`, `TOT_ACCUM_DEPR`, `Accum Depr`, `Accum Depreciation`, `Depreciation to Date` | Through the cut-over date. Give this or the net book value (or both, which must agree). |
| Net book value |  | `NetBookValue`, `Net book value`, `NET_BOOK_VAL`, `NBV`, `Book Value` | Cost less accumulated depreciation at the cut-over. |
| Cut-over date | yes | `CutoverDate`, `Cut-over date`, `AS_OF_DT`, `BAL_DT`, `As Of`, `As Of Date`, `Cut-over`, `Balance Date`, `Through Date` | The month end the balances are as of, e.g. 2026-09-30. |
| Useful life (years) |  | `UsefulLifeYears`, `Useful life (years)`, `LIFE_YRS`, `USEFUL_LIFE`, `Useful Life`, `Life` | Defaults to the category's. |
| Useful life (months) |  | `UsefulLifeMonths`, `Useful life (months)`, `LIFE_MOS`, `USEFUL_LIFE_MOS`, `Life (Months)`, `Months` | Alternative to years; must be whole years (36, 60...). |
| Depreciation method |  | `Method`, `Depreciation method`, `DEPR_METHOD_CD`, `DEPR_MTHD` | SL (straight line), DDB/DB (declining balance) or SYD. Defaults to the category's. |
| Convention |  | `Convention`, `AVG_CONV_CD`, `CONVENTION_CD`, `Averaging Convention` | Full month, half month (mid-month) or following month. Defaults to the category's. |
| Salvage value |  | `SalvageValue`, `Salvage value`, `SALVAGE_AMT`, `SALV_VAL`, `Salvage`, `Residual Value` | Defaults to 0. |
| Serial number |  | `SerialNumber`, `Serial number`, `SERIAL_NO`, `SERIAL_NUM`, `Serial` | |
| Location |  | `Location`, `LOCATION_CD`, `LOC_DESC` | |
| Org |  | `OrgId`, `Org`, `ORG_ID`, `Organization` | Org id of an existing org unit (depreciation expense segment). |
| Project |  | `ProjectId`, `Project`, `PROJ_ID`, `Project ID` | Code of an existing project (depreciation expense segment). |

### Smartsheet sheets (API and .xlsx)

Sample: [`project-plan.xlsx`](smartsheet/project-plan.xlsx)

| Field | Required | Accepted headers (case-, space- and punctuation-insensitive) | Notes |
|---|---|---|---|
| Task name | yes | `Title`, `Task name`, `Task Name`, `Task`, `Name`, `Primary`, `Activity` | The primary column: each row's title. |
| Start |  | `Start`, `Start Date`, `Begin`, `Planned Start` | Start date (the Gantt start column). |
| Finish |  | `Finish`, `End Date`, `Finish Date`, `End`, `Due Date`, `Due`, `Planned Finish` | Finish date (the Gantt end column). |
| Duration |  | `Duration`, `Dur` | Used for the finish date when a row has none (working days). |
| Predecessors |  | `Predecessors`, `Predecessor`, `Depends On` | Smartsheet predecessors (FS/SS/FF/SF with lag) become Gantt dependencies. |
| WBS code |  | `WbsCode`, `WBS code`, `WBS`, `WBS Code`, `WBS ID`, `WBS Element`, `Charge Code`, `Costpoint WBS`, `PROJ_ID`, `Project ID` | Links rows to the chosen project's WBS elements by code. |
| Row ID |  | `RowId`, `Row ID`, `Smartsheet Row ID` | .xlsx only: a column with the Smartsheet row id, for matching on re-import. Without it rows match on their title path. |
