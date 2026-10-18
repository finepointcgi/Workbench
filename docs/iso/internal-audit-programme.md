# Internal audit programme

| | |
| --- | --- |
| Owner | `<ISMS manager>` |
| Approved by | `<top management>` |
| Programme period | `<year>` |
| Requirement | ISO 27001 clause 9.2 |

*The internal audit checks that the ISMS meets the standard and your own requirements, and is effectively
implemented and maintained. Every clause (4–10) and every applicable Annex A control must be covered over the
certification cycle, with higher-risk areas audited more often. Complete at least one full internal audit before the
Stage 2 certification audit.*

## 1. Auditors and independence

Auditors must be objective and impartial: they don't audit their own work. `<Names, qualifications (e.g. ISO 27001
Lead Auditor training), or external provider>`.

## 2. Programme

| Audit | Scope (clauses / controls) | Method | Auditor | Planned | Done | Report |
| --- | --- | --- | --- | --- | --- | --- |
| IA-01 | Clauses 4–10: context, leadership, risk assessment and treatment, SoA, objectives, documented information, management review, corrective action | Document review, interviews | `<>` | `<Q1>` | | |
| IA-02 | Access control: A.5.15–5.18, A.8.2, A.8.3, A.8.5, A.6.5 | Sample 10 joiners/leavers against HR records and Workbench security events; check last 2 access reviews in Workbench | `<>` | `<Q1>` | | |
| IA-03 | Logging and monitoring: A.8.15, A.8.16, A.8.17 | Security event log export; evidence of weekly review; SIEM alerts | `<>` | `<Q2>` | | |
| IA-04 | Secure development and change: A.8.4, A.8.8, A.8.25–8.33 | Sample 10 merged PRs: review, CI green, CODEOWNERS; vulnerability SLA records; SBOMs | `<>` | `<Q2>` | | |
| IA-05 | Backup, continuity, records: A.8.13, A.8.14, A.5.29, A.5.30, A.5.33, A.8.10 | Restore test records; retention report; legal holds | `<>` | `<Q3>` | | |
| IA-06 | Incidents: A.5.24–5.28, A.6.8 | Incident log, PIRs, tabletop exercise | `<>` | `<Q3>` | | |
| IA-07 | Suppliers and cloud: A.5.19–5.23 | Supplier checklists, contracts | `<>` | `<Q3>` | | |
| IA-08 | People and physical: A.6.1–6.8, A.7.x | HR records sample, training completion, site walk | `<>` | `<Q4>` | | |
| IA-09 | Remaining organizational and technological controls | | `<>` | `<Q4>` | | |

## 3. Audit procedure

1. Plan: scope, criteria, sample, auditee, dates; send the plan a week ahead.
2. Opening meeting; collect evidence (records, system screenshots or exports, interviews, observation).
3. Classify findings: **Major nonconformity** (a requirement not met, or a control not operating), **Minor
   nonconformity** (an isolated lapse), **Observation / opportunity for improvement**.
4. Closing meeting; report within `<5>` business days.
5. Auditee raises corrective actions (root cause, correction, corrective action, owner, due date); the auditor
   verifies closure.

## 4. Report template

| | |
| --- | --- |
| Audit ID / date | |
| Auditor(s) / auditee(s) | |
| Scope and criteria | |
| Evidence sampled | |
| Summary | |

| # | Clause/control | Finding | Type | Evidence | Corrective action | Owner | Due | Closed |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | | | | | | | | |

## 5. Corrective action log (clause 10.2)

| ID | Source (audit, incident, review) | Nonconformity | Root cause | Action | Owner | Due | Verified effective |
| --- | --- | --- | --- | --- | --- | --- | --- |
| CA-001 | | | | | | | |
