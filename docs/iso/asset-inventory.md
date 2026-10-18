# Asset inventory

| | |
| --- | --- |
| Owner | `<ISMS manager>` |
| Controls | A.5.9, A.5.10, A.5.11, A.5.12, A.5.13 |
| Last reviewed | `<date>` |

*List information and the assets that store or process it, each with an owner and classification. Keep it current
(review yearly and on change). Workbench's software components are listed per build in the CycloneDX SBOM (CI
artifact), so don't duplicate packages here.*

## Classification scheme (A.5.12)

| Level | Examples | Handling |
| --- | --- | --- |
| **CUI** | Customer-provided CUI, covered defense information, export-controlled data | NIST SP 800-171 environment only; marked; encrypted; need-to-know |
| **Confidential** | Cost and bill rates, contract values, budgets, salaries, employee PII, security logs | Role-restricted (Finance/Admin); encrypted in transit and at rest; exports logged |
| **Internal** | Project plans, sheets, timesheets of others | Workspace members; not shared externally without approval |
| **Public** | Marketing material | No restrictions |

Labelling (A.5.13): CUI is marked per the CUI Registry and the contract; exports containing Confidential data are
named and stored accordingly `<rule>`.

## Information assets

| ID | Information | Classification | Owner | Location | Retention |
| --- | --- | --- | --- | --- | --- |
| I01 | Labor hours, timesheets, revisions, approvals | Confidential | `<Controller>` | Workbench DB | ≥ 3 years after contract close (protected) |
| I02 | Cost rates, bill rates, labor categories | Confidential | `<CFO>` | Workbench DB | As I01 |
| I03 | Contracts, funding, budgets, forecasts | Confidential | `<CFO>` | Workbench DB | As I01 |
| I04 | ODCs, expense reports, receipts | Confidential | `<Controller>` | Workbench DB + blob store | As I01 |
| I05 | Employee records | Confidential (PII) | `<HR>` | Workbench DB | `<HR schedule>` |
| I06 | Project sheets, comments, attachments | Internal / `<CUI if applicable>` | Workspace owners | Workbench DB + blob store | `<>` |
| I07 | Audit log (financial changes) | Confidential | `<Controller>` | Workbench DB | ≥ 3 years (protected) |
| I08 | Security event log | Confidential | `<ISMS manager>` | Workbench DB (+ SIEM) | 1 year (configurable) |
| I09 | Access review records | Confidential | `<ISMS manager>` | Workbench DB | ≥ 3 years (protected) |
| I10 | Source code | Confidential | `<Engineering lead>` | `<GitHub>` | Indefinite |
| I11 | Backups | Highest of contents | `<Operations>` | `<locations>` | Per backup policy |
| I12 | Secrets and keys (OIDC secret, SCIM token, DB password, Data Protection certificate) | Confidential | `<Operations>` | `<vault>` | Until rotated |

## Technology and service assets

| ID | Asset | Type | Owner | Environment | Holds | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| T01 | Workbench web host | Application server | `<Operations>` | Production | I01–I09 in transit | `<host/cluster>` |
| T02 | Workbench worker | Background service | `<Operations>` | Production | | |
| T03 | Database (PostgreSQL) | Data store | `<Operations>` | Production | I01–I09, I12 (DP keys) | `<server, version>` |
| T04 | Blob store | Data store | `<Operations>` | Production | I04, I06 | |
| T05 | Identity provider tenant | SaaS | `<IT>` | | Identities | Supplier |
| T06 | CI/CD (GitHub Actions) | SaaS | `<Engineering>` | | I10 | Supplier |
| T07 | Email relay | SaaS / server | `<IT>` | | Notifications | |
| T08 | Admin workstations | Endpoints | `<IT>` | | | MDM, encryption |
| T09 | Staff laptops with the desktop client | Endpoints | `<IT>` | | Tokens only | |

## Return of assets (A.5.11)

Leavers return devices and media; IT confirms wipe and Workbench deactivation on the offboarding checklist.
