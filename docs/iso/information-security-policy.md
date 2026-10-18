# Information security policy

| | |
| --- | --- |
| Document owner | `<ISMS manager>` |
| Approved by | `<CEO>` |
| Version / date | `<1.0>` / `<date>` |
| Classification | Internal |
| Review | Yearly, and after significant change |

*ISO 27001 clause 5.2 and A.5.1. Keep this short and signed by top management; detailed rules go in topic-specific
policies (access control, secure development, backup, and others). Communicate it to all staff and make it available
to interested parties.*

## 1. Purpose

`<Company>` holds information that customers, employees and the government trust us to protect: labor and cost
records audited by DCAA, contract and financial data, employee personal data, and information that may be Controlled
Unclassified Information. This policy sets our commitment and the framework for protecting its confidentiality,
integrity and availability.

## 2. Scope

All information, systems, people and locations in the ISMS scope (`isms-scope.md`), including employees,
contractors and suppliers who handle our information.

## 3. Commitments

Top management commits to:

1. Meet applicable legal, regulatory and contractual requirements, including `<DFARS 252.204-7012, NIST SP 800-171,
   FAR 4.703 records retention, state privacy laws>`.
2. Set information security objectives (section 5) and provide the resources to achieve them.
3. Assess and treat information security risks using the method in `risk-assessment-method.md`, and accept residual
   risk only within the defined criteria.
4. Continually improve the ISMS through internal audit, management review and corrective action.

## 4. Principles

- **Least privilege and need to know.** Access is granted by role, reviewed quarterly and removed promptly when no
  longer needed (`access-control-policy.md`).
- **Strong identity.** Single sign-on with multi-factor authentication for all access to in-scope systems.
- **Security by design.** Systems are built and changed under the secure development policy.
- **Records integrity.** Timekeeping, financial and audit records are never altered without trace and are kept for
  their retention period.
- **Prepared response.** Incidents are reported immediately and handled under the incident response plan.
- **Resilience.** Critical information is backed up and restorable within agreed targets.

## 5. Information security objectives

*Objectives must be measurable, monitored and reviewed (clause 6.2). Examples; adjust targets.*

| Objective | Measure | Target | Owner |
| --- | --- | --- | --- |
| Access is reviewed | Quarterly access reviews completed in Workbench | 4 per year, 100% decided | `<IT lead>` |
| Leavers lose access promptly | Deactivation within 1 business day of termination | 100% | `<HR + IT>` |
| Vulnerabilities fixed | Critical/High fixed within 14/30 days | ≥ 95% | `<Engineering lead>` |
| Backups restorable | Quarterly restore test passes | 4 per year | `<Operations>` |
| Staff trained | Annual security awareness completion | 100% | `<HR>` |
| Incidents handled | Incidents closed with lessons learned | 100% | `<ISMS manager>` |

## 6. Roles and responsibilities

| Role | Responsibility |
| --- | --- |
| Top management | Approves policy, resources, risk acceptance; chairs management review |
| ISMS manager `<name>` | Runs the ISMS, maintains documents, coordinates audits |
| Information/asset owners | Classify assets, approve access, accept risks for their assets |
| Workbench administrators | User lifecycle, access reviews, security event review, retention settings |
| Engineering | Secure development, vulnerability management |
| All staff | Follow policies, complete training, report events and weaknesses |

## 7. Compliance

Breaches of this policy may lead to disciplinary action (A.6.4). Exceptions need a documented risk assessment and
approval by `<role>`, recorded in the risk register with an expiry date.

## 8. Related documents

ISMS scope, risk assessment method, Statement of Applicability, access control policy, secure development policy,
backup policy, incident response plan, business continuity plan, supplier security checklist.

## 9. Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<CEO>` | | |
