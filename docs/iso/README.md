# ISO/IEC 27001:2022 readiness kit

This folder holds templates for the information security management system (ISMS) documents an ISO/IEC 27001:2022
certification audit asks for, pre-filled where Workbench itself already answers a control. **Certification is of
your organization's ISMS, not of a software product.** Workbench gives you controls and evidence; your company has
to run the management system around them.

## What's here

| Document | Clause / control | Fill in |
| --- | --- | --- |
| [isms-scope.md](isms-scope.md) | 4.1–4.4 | Organization, boundaries, interested parties |
| [information-security-policy.md](information-security-policy.md) | 5.2, A.5.1 | Approve and sign |
| [risk-assessment-method.md](risk-assessment-method.md) | 6.1.2, 8.2 | Adjust scales and acceptance criteria |
| [risk-register.md](risk-register.md) | 6.1.2–6.1.3, 8.2–8.3 | Score the starter risks, add your own, assign owners |
| [statement-of-applicability.md](statement-of-applicability.md) | 6.1.3 d | All 93 Annex A controls: confirm, justify, set status |
| [access-control-policy.md](access-control-policy.md) | A.5.15–5.18, A.8.2–8.5 | Approve; set review cadence |
| [secure-development-policy.md](secure-development-policy.md) | A.8.25–8.33 | Approve; name owners |
| [backup-policy.md](backup-policy.md) | A.8.13 | Set RPO/RTO, retention, locations |
| [incident-response-plan.md](incident-response-plan.md) | A.5.24–5.28, A.6.8 | Contacts, reporting duties; includes a runbook |
| [supplier-security-checklist.md](supplier-security-checklist.md) | A.5.19–5.23 | Use for each supplier |
| [business-continuity-plan.md](business-continuity-plan.md) | A.5.29–5.30, A.8.14 | Set targets; test |
| [asset-inventory.md](asset-inventory.md) | A.5.9–5.12 | List your assets and owners |
| [internal-audit-programme.md](internal-audit-programme.md) | 9.2 | Plan audits; choose auditors |
| [management-review.md](management-review.md) | 9.3 | Use for each review |

Supporting technical documents: [../security/threat-model.md](../security/threat-model.md),
[../security/configuration.md](../security/configuration.md),
[../operations/backup-restore.md](../operations/backup-restore.md) and [../../SECURITY.md](../../SECURITY.md).

Placeholders look like `<this>`. Text in *italics* is guidance; delete it once the section is filled in.

## Where Workbench supplies evidence

| Evidence an auditor asks for | Where in Workbench |
| --- | --- |
| Who has access, and a periodic review of it (A.5.18) | **Users and security → Access reviews**: completed reviews with each decision, reviewer and time; CSV export |
| Joiners, movers, leavers (A.5.16, A.6.5) | Invites, SCIM provisioning and deactivation, role changes: all in the security event log |
| Event logging (A.8.15) | **Users and security → Security events**: filter and CSV export |
| Privileged access (A.8.2) | Admin and Finance roles; role changes logged; last-admin protection |
| Secure authentication and sessions (A.8.5) | OIDC/SSO (MFA at your IdP), idle and absolute session timeouts, sign out everywhere |
| Records retention and legal hold (A.5.33, A.8.10) | **Users and security → Retention**: policies, run reports, legal holds |
| Backups and restore tests (A.8.13) | `scripts/backup.sh`, `scripts/restore.sh`, restore test records |
| Secure development (A.8.25–8.29) | CI: build, tests, vulnerable-package check, secret scanning, SBOM; CODEOWNERS reviews |
| Audit trail of financial changes | **Setup → Audit log** (rates, setup, budgets, approvals) |

## What certification needs beyond these documents

Plainly, so nobody is surprised:

1. **Operate the ISMS first.** Auditors look for evidence that the system runs, not just that documents exist:
   risk assessments performed and treated, at least one full internal audit, at least one management review,
   access reviews completed, incidents handled, corrective actions tracked, training delivered. Most organizations
   operate the ISMS for **3 to 6 months** (sometimes longer) before the Stage 2 audit.
2. **Company-wide controls Workbench can't provide.** HR screening and agreements, security awareness training,
   physical security, endpoint management, network security, supplier management, legal and regulatory registers,
   business continuity. The Statement of Applicability marks each one "Company".
3. **Choose an accredited certification body.** Use one accredited by a recognized accreditation body (in the US,
   ANAB; others include UKAS and DAkkS; check the IAF CertSearch database). Unaccredited certificates often aren't
   accepted by customers.
4. **Stage 1 audit** (documentation and readiness): the auditor reviews scope, policies, risk assessment, SoA and
   whether you are ready for Stage 2. Expect findings to fix.
5. **Stage 2 audit** (certification): the auditor samples evidence that controls operate as described, interviews
   staff and checks records. Nonconformities must be corrected before the certificate is issued.
6. **Keep it running.** Certificates last 3 years, with **surveillance audits every year** and a recertification
   audit in year 3. Internal audits, management reviews and risk reviews continue throughout.

Other points:

- ISO 27001 doesn't replace US government requirements. Contractors handling CUI also need NIST SP 800-171 /
  CMMC; contracts with DFARS 252.204-7012 add incident reporting and cloud (FedRAMP Moderate equivalent) duties.
  Many controls overlap, and the risk register and SoA can reference both.
- Self-hosted Workbench is in your scope; vendor-hosted Workbench makes the vendor a supplier (A.5.19–5.23), and
  you should ask for its certificate or SOC 2 report.
- Nothing in this kit guarantees certification. A consultant or a gap assessment against these templates before
  Stage 1 is common and useful.
