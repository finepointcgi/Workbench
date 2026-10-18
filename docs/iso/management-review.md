# Management review

*ISO 27001 clause 9.3. Top management reviews the ISMS at planned intervals (at least yearly; quarterly is common in
the first year) and before the Stage 2 audit. Copy this template for each review and keep the minutes as evidence.*

| | |
| --- | --- |
| Review no. / date | `<MR-yyyy-n>` / `<date>` |
| Chair | `<CEO>` |
| Attendees | `<names, roles>` |
| Period covered | `<from – to>` |

## 1. Inputs (9.3.2)

| # | Input | Summary / data | Source |
| --- | --- | --- | --- |
| a | Status of actions from previous reviews | `<>` | Previous minutes |
| b | Changes in external and internal issues | `<new contracts (CMMC level), hosting changes, threats>` | `isms-scope.md` |
| c | Changes in interested parties' needs | `<customer security requirements>` | |
| d1 | Nonconformities and corrective actions | `<open/closed>` | Corrective action log |
| d2 | Monitoring and measurement results | Objectives table below | |
| d3 | Audit results | `<internal audits, external audits>` | Audit reports |
| d4 | Fulfilment of information security objectives | `<>` | Security policy section 5 |
| e | Feedback from interested parties | `<customer questionnaires, complaints>` | |
| f | Risk assessment results and treatment plan status | `<count by level, overdue actions>` | `risk-register.md` |
| g | Opportunities for continual improvement | `<>` | |

### Measurements from Workbench and operations

| Measure | Target | This period | Evidence |
| --- | --- | --- | --- |
| Access reviews completed (all users decided) | 1 per quarter | `<>` | Users and security → Access reviews |
| Users revoked in reviews / dormant users found | Track | `<>` | Access review CSV |
| Leavers deactivated within 1 business day | 100% | `<>` | HR leaver list vs `user.deactivated` events |
| Sign-in failures / access denials (trend) | Track | `<>` | Security events export |
| Sensitive exports (count, by person) | Track; investigate outliers | `<>` | `export.*` events |
| Critical/High vulnerabilities past SLA | 0 | `<>` | CI / issue tracker |
| Restore tests passed | 1 per quarter | `<>` | Backup policy log |
| Incidents (by severity), PIRs completed | 100% PIR | `<>` | Incident log |
| Security training completion | 100% | `<>` | HR |

## 2. Discussion

`<Notes on each input, decisions and their reasoning.>`

## 3. Outputs and decisions (9.3.3)

| # | Decision / action (improvements, ISMS changes, resources) | Owner | Due |
| --- | --- | --- | --- |
| 1 | | | |

- Is the ISMS still suitable, adequate and effective? `<Yes / No, because ...>`
- Changes to scope, policy, objectives or risk acceptance criteria: `<>`
- Resources approved: `<>`
- Residual risks accepted (High needs top management): `<risk IDs>`

## 4. Sign-off

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<CEO>` | | |
