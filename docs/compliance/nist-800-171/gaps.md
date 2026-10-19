# Gaps: Workbench and the hosted SaaS for CUI

An honest list of what stands between Workbench and a CUI environment that passes a CMMC Level 2 assessment. Read it
before putting CUI in Workbench. Self-hosting in a compliant enclave closes most hosting gaps; the hosted SaaS has more.

## A. The hosted (MultiTenant) Workbench service

| # | Gap | Why it matters | What would close it | Until then |
| --- | --- | --- | --- | --- |
| A1 | **Not FedRAMP Moderate authorized or equivalent.** DFARS 252.204-7012(b)(2)(ii)(D) requires a cloud service that stores CUI to meet the FedRAMP Moderate baseline (or equivalent, per the DoD CIO equivalency memo of Dec 2023: a 3PAO assessment with 100% of controls met, no open POA&M). | Using the SaaS for CUI would breach the clause. | Host the service in a FedRAMP-authorized region (Azure Government, AWS GovCloud (US)), then a FedRAMP Moderate authorization or a 3PAO-assessed equivalency package for the Workbench service itself. Inheriting the cloud region's authorization covers only the infrastructure layers. | **Don't store CUI in the hosted service.** Use it for non-CUI accounting data, or self-host. |
| A2 | No US-persons-only operations or data residency commitment in contract | ITAR/EAR data and some contracts require it | Operations staff screening and residency terms | Self-host for export-controlled data |
| A3 | No incident reporting agreement for DFARS 72-hour reporting and 90-day image preservation | The customer must report within 72 hours; it needs the provider's cooperation | Contract terms; provider IR runbook with DIBNet reporting support | Customer's own IR plan only |
| A4 | Shared multi-tenant infrastructure (database per tenant, shared web hosts and key ring with per-tenant purposes) | Some customers' assessors expect dedicated enclaves | Dedicated deployment per customer (single-tenant hosting) | Document the isolation (`../../security/configuration.md`) |
| A5 | No SOC 2 Type II / ISO 27001 certificate for the service yet | Supplier assurance (3.12, A.5.19) | Certify the operator's ISMS (this repository's ISO kit applies to the operator too) | Questionnaire + this documentation |
| A6 | SCIM is not available per tenant in MultiTenant mode (one server-wide token) | Automated deprovisioning (3.9.2) | Per-tenant SCIM tokens | Deactivate in Users; IdP assignment removal still blocks sign-in |
| A7 | Host administrators' access to the infrastructure is the operator's process | Privileged access (3.1.5, 3.1.15) | Documented, MFA-protected, logged operator access with customer notification | Ask the operator for its access controls |

## B. The Workbench product (any hosting)

| # | Gap | Requirement | Workaround / plan |
| --- | --- | --- | --- |
| B1 | No built-in FIPS 140 mode switch. .NET uses the OS cryptographic provider: on Windows with FIPS policy or a FIPS-enabled Linux (e.g. RHEL in FIPS mode with OpenSSL's validated provider) the algorithms Workbench uses (AES, HMAC-SHA256, RSA, SHA-256) run through validated modules. | 3.13.11 | Deploy on a FIPS-enabled OS and terminate TLS on a FIPS-validated load balancer; record CMVP certificate numbers in the SSP. |
| B2 | No CUI marking or banner inside Workbench (sheets, attachments, exports aren't labelled) | 3.8.4, 3.1.9 | Show the notice at the IdP; label files manually; consider a CUI flag on sheets/attachments with export markings (future). |
| B3 | No account lockout in Workbench (lockout is the IdP's); sign-in endpoints are rate-limited | 3.1.8 | Configure IdP lockout (Entra smart lockout / Okta). |
| B4 | Security events are kept in the application database; there's no built-in SIEM forwarder | 3.3.8, 3.3.5 | Export CSV regularly or read the table from a replica into the SIEM; alert on logging failures. |
| B5 | Desktop app: per-organization IdPs aren't supported (bearer tokens only from the server-wide IdP, so an organization that requires its own IdP can't use the desktop app); MFA is checked from the access token's `amr`, which some IdPs don't include; sensitive actions need `auth_time` in the token | 3.5.3 | Use the browser for sensitive actions; configure the IdP to include `amr`/`auth_time` in access tokens, or require MFA through Conditional Access for the desktop client. |
| B6 | Session lock (pattern-hiding) isn't a Workbench feature; the idle timeout ends the session instead | 3.1.10 | OS screen lock via MDM. |
| B7 | Malware scanning of uploads isn't built in (type allow-list and size limit only) | 3.14.2, 3.14.5 | Scan the blob store with the platform's anti-malware (e.g. Defender for Storage). |
| B8 | No automatic disablement of dormant accounts (they're flagged in access reviews) | 3.5.6 | IdP inactivity policy; quarterly review. |
| B9 | SAML isn't supported | — | Entra ID, Okta and most IdPs offer OIDC. |
| B10 | Re-authentication is enforced for pay rates, bank details, vendor TINs, approval workflows and sign-in settings; other administrative actions (role changes, deactivation) rely on MFA at sign-in | 3.1.15 | Short re-authentication window and session limit; review role changes in the security log. |

## C. Always the customer's

Physical protection (3.10), personnel screening (3.9.1), awareness training (3.2), media protection (3.8), endpoint and
mobile device controls (3.1.16–3.1.21), network architecture when self-hosting (3.13.5–3.13.7), incident reporting
(3.6.2), risk assessment and POA&M (3.11, 3.12). See the "Customer / hosting part" column of
[control-mapping.md](control-mapping.md).
