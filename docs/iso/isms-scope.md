# ISMS scope

| | |
| --- | --- |
| Document owner | `<ISMS manager>` |
| Approved by | `<CEO / top management>` |
| Version / date | `<1.0>` / `<date>` |
| Review | At least yearly, and when the organization, services or hosting change |

*ISO 27001 clauses 4.1–4.4. The scope statement appears on the certificate, so make it precise. Auditors check that
everything inside it is covered by the risk assessment and the Statement of Applicability, and that exclusions don't
leave obvious gaps.*

## 1. Scope statement

> The information security management system of `<Company legal name>` covering the provision, operation and
> support of the Workbench project ERP and work-management system, including project accounting, timekeeping,
> ODCs, budgets and financials, and project collaboration, for `<internal use / customers>`, operated from
> `<locations>` and hosted in `<on-premises data center / Azure Government region / AWS GovCloud region>`, in
> accordance with the Statement of Applicability version `<x>`.

## 2. Organization and context (4.1)

*Describe the company, its business, and the internal and external issues that affect information security.*

- Business: `<government contractor providing ...>`
- Internal issues: `<size, growth, remote workforce, legacy Costpoint/Unanet migration, skills>`
- External issues: `<customer requirements (DFARS 7012, CMMC), threat landscape against defense industrial base, regulators, cloud dependencies>`

## 3. Interested parties and their requirements (4.2)

| Interested party | Requirements relevant to information security | How addressed |
| --- | --- | --- |
| Government customers / contracting officers | DFARS 252.204-7012, NIST SP 800-171, CMMC level `<x>`, FAR 52.204-21 | `<SSP, this ISMS>` |
| DCAA | Timekeeping integrity, audit trail, records retention (FAR 4.703) | Workbench audit log, timesheet revisions, retention (protected records) |
| Employees | Privacy of personal data; usable systems | Access control, PII handling |
| Prime contractors / subcontractors | Flow-down clauses, CUI handling | Supplier agreements |
| Shareholders / management | Continuity, reputation | BC/DR plan, risk management |
| Suppliers (hosting, IdP, email) | Agreements, shared responsibility | Supplier checklist |
| `<others>` | | |

## 4. Boundaries and interfaces (4.3)

**In scope**

- Processes: `<software development, deployment and operation of Workbench; user and access management; incident management; backup; supplier management>`
- Information: project accounting data (labor hours, cost and bill rates, contracts, budgets, ODCs), employee records,
  collaboration data, security and audit logs.
- Systems: Workbench web host, worker, database, blob store, identity provider tenant `<name>`, CI/CD (GitHub
  `<org>`), desktop clients, administrator workstations.
- Locations: `<offices>`, `<hosting regions>`, remote work.
- People: `<all employees and contractors with access to Workbench or its infrastructure>`.

**Out of scope** (with justification)

| Excluded | Why it's excluded | Interface to the ISMS |
| --- | --- | --- |
| `<Corporate marketing website>` | `<No CUI or customer data; separate hosting>` | `<None>` |
| `<Payroll provider's systems>` | Supplier-operated | Supplier agreement, A.5.19–5.22 |

**Interfaces and dependencies**

| Interface | Direction | Owner | Controls |
| --- | --- | --- | --- |
| Identity provider (OIDC, SCIM) | IdP → Workbench | `<IT>` | MFA, conditional access, SCIM token in vault |
| Costpoint/Unanet imports (if used) | Legacy → Workbench | `<Finance>` | Read-only service accounts |
| Email relay | Workbench → users | `<IT / supplier>` | TLS, SPF/DKIM/DMARC |
| Hosting provider | Shared responsibility | `<supplier>` | Supplier review, FedRAMP authorization |

## 5. Hosting model

*Pick one and delete the others. The model decides which Annex A controls are yours and which are your supplier's.*

- [ ] Self-hosted on-premises: you own physical security, network, OS patching, backups.
- [ ] Self-hosted in Azure Government / AWS GovCloud: shared responsibility; the cloud provider covers physical and
  some infrastructure controls (keep their FedRAMP package / SOC reports as supplier evidence).
- [ ] Hosted by the Workbench vendor: the vendor is a critical supplier; obtain its ISO 27001 certificate or SOC 2
  Type II report and contractual security terms.

## 6. Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<CEO>` | | |
