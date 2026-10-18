# Security policy

Workbench holds contract, labor and cost data for US government contractors. We take reports seriously and thank
everyone who reports responsibly.

## Reporting a vulnerability

**Don't open a public issue.** Report privately by one of:

- GitHub: **Security → Report a vulnerability** on this repository (private advisory), or
- Email: `security@<your-company-domain>` (replace with your security contact). PGP key: `<link or fingerprint>`.

Please include:

- what is affected (version or commit, component, configuration such as SQLite/PostgreSQL or OIDC provider);
- steps to reproduce, a proof of concept if you have one, and the impact you expect;
- whether the issue is already public or being exploited.

Don't access, change or keep data that isn't yours, don't degrade service for others, and give us reasonable time
to fix the issue before disclosure.

## What happens next

| Step | Target |
| --- | --- |
| Acknowledge the report | 2 business days |
| Triage and severity (CVSS v3.1/v4) | 5 business days |
| Fix or mitigation for Critical / High | 14 / 30 days |
| Fix for Medium / Low | Next scheduled release |
| Tell the reporter and customers; publish an advisory | With the fix |

Customers who host Workbench themselves get advisories through `<customer notification channel>` and should apply
security releases promptly. If the issue may involve Controlled Unclassified Information or covered defense
information, we also follow our incident response plan (`docs/iso/incident-response-plan.md`), including any
contractual reporting duties (for example DFARS 252.204-7012's 72-hour report to DoD).

## Supported versions

| Version | Security fixes |
| --- | --- |
| Latest release | Yes |
| Previous minor release | Critical and High only, for 6 months |
| Older | No: upgrade |

## Scope

In scope: this repository's code, the web host, the REST API (`/api/v1`), SCIM (`/scim/v2`), the SignalR hubs and the
desktop client. Out of scope: findings that need an already compromised administrator account or host, missing
headers on non-sensitive static files, self-XSS, volumetric denial of service, and issues in third-party services
(report those to the vendor).

## For developers

See `docs/security/threat-model.md`, `docs/security/configuration.md` and `docs/iso/secure-development-policy.md`.
Never commit secrets; CI runs gitleaks on every pull request.
