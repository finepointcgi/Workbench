# Asset management policy

| | |
| --- | --- |
| Owner | `<ISMS manager>` |
| Approved by | `<top management>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.5.9, A.5.10, A.5.11, A.5.12, A.5.13, A.5.14, A.7.9, A.7.10, A.7.14, A.8.1, A.8.10 · NIST SP 800-171 3.4.1, 3.8.x |
| Register | [asset-inventory.md](asset-inventory.md) |

## 1. Inventory (A.5.9)

All information and assets in scope are listed in the [asset inventory](asset-inventory.md) with an owner and a
classification. Software components of Workbench are listed per build in its CycloneDX SBOM; hosts and cloud resources
come from `<CMDB / cloud inventory>`. The inventory is reviewed `<yearly>` and when systems change.

## 2. Owners

Each asset has a named owner who decides who may access it, approves access in access reviews, and ensures it is
classified, protected and disposed of correctly.

## 3. Classification and labelling (A.5.12, A.5.13)

Use the scheme in [asset-inventory.md](asset-inventory.md#classification-scheme-a512) (CUI, Confidential, Internal,
Public). In Workbench:

| Information | Classification | Protection in Workbench |
| --- | --- | --- |
| Cost rates, labor cost, budgets, contract values | Confidential | Finance/Admin/PM only (permission decorators); restricted sheet columns |
| Pay rates, bank account numbers, vendor TINs | Confidential | Masked; reveal is Finance/Admin, needs a recent sign-in and is logged; encrypted at rest (bank, TIN) |
| Employee PII | Confidential | Role-restricted; retention rules |
| Customer CUI in attachments or sheets | CUI | Only in a NIST 800-171 environment (see `../compliance/nist-800-171/`); mark per the CUI Registry |
| Timesheets, project plans | Internal | Workspace and supervisor rules |

## 4. Acceptable use (A.5.10)

Staff use Workbench and its data only for company business; don't export Confidential/CUI data to personal storage,
don't share accounts, report suspected misuse. `<Link to the acceptable use policy and acknowledgement record.>`

## 5. Transfer (A.5.14)

Exports (CSV/XLSX) of Confidential data are logged (`export.*` events); send them only through `<approved channel>`,
encrypted when outside the company. CUI transfers follow the contract and DFARS 252.204-7012.

## 6. Endpoints and media (A.7.9, A.7.10, A.8.1)

Laptops: full-disk encryption, MDM, patched, screen lock `<10 min>`. Removable media for CUI: `<prohibited / encrypted
and logged>`. Workbench keeps data on the server (thin web and desktop clients).

## 7. Return and disposal (A.5.11, A.7.14, A.8.10)

Leavers return equipment; their Workbench account is deactivated (SCIM or **Users**), never deleted, so history keeps
its author. Disks and media are wiped (NIST SP 800-88) or destroyed with a certificate. Workbench records follow the
retention schedule (**Users and security › Retention**); protected records are never auto-deleted; legal holds block
deletion.

## Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<role>` | | |
