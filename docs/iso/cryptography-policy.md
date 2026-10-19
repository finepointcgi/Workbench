# Cryptography and key management policy

| | |
| --- | --- |
| Owner | `<IT / operations lead>` |
| Approved by | `<ISMS manager>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.8.24, A.5.17, A.5.33, A.8.13 · NIST SP 800-171 3.13.8, 3.13.10, 3.13.11, 3.13.16 |

*Template: replace `<placeholders>`, delete guidance in italics. Rows marked **Workbench** describe what the product
already does; rows marked **Company** are yours to operate.*

## 1. Purpose

Protect the confidentiality and integrity of information (CUI, employee PII, cost and pay rates, bank details, security
logs) with cryptography that is approved, correctly configured, and whose keys are managed for their whole life.

## 2. Approved cryptography

| Use | Requirement | How it's met |
| --- | --- | --- |
| Data in transit (users ↔ Workbench) | TLS 1.2 or 1.3 only, modern cipher suites, HSTS | **Company**: TLS at `<load balancer / reverse proxy>` (`Security:TlsTerminatedByProxy`); **Workbench**: HSTS (`Security:HstsMaxAgeDays`), cookies `Secure`, refuses plain-HTTP production config |
| Data in transit (Workbench ↔ IdP, database, SMTP) | TLS; certificate validation on | **Workbench**: OIDC metadata over HTTPS only (startup check; per-organization IdPs must use `https`); **Company**: `SSL Mode=Require` / `VerifyFull` on PostgreSQL, SMTP STARTTLS |
| Data at rest (database, blob store, backups) | AES-256 (provider-managed or customer-managed keys) | **Company**: `<Azure SQL/Postgres TDE / AWS RDS encryption / LUKS>`; encrypted backup storage |
| Secrets inside the database | Authenticated encryption with keys outside the database | **Workbench**: ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256) encrypts bank account numbers, vendor TINs, the organization's IdP client secret and tenant connection strings; keys are isolated per tenant by purpose |
| Session cookies and anti-forgery tokens | Encrypted and signed | **Workbench**: Data Protection |
| Tokens from the identity provider | RS256/ES256 signatures validated against the IdP's published keys | **Workbench**: OIDC handler validates issuer, audience, signature, lifetime and nonce |
| FIPS 140 validation (CUI) | Validated modules where CUI is protected (NIST 3.13.11) | **Company**: run on a FIPS-enabled OS image / FIPS-validated TLS terminator; see `../compliance/nist-800-171/gaps.md` |

Not allowed: SSL, TLS 1.0/1.1, RC4, 3DES, MD5/SHA-1 for signatures, home-grown cryptography, secrets in source control.

## 3. Key management

| Key | Where it lives | Rotation | Backup | Owner |
| --- | --- | --- | --- | --- |
| Data Protection key ring | `DataProtectionKeys` table (catalog in MultiTenant), encrypted at rest with the PFX/certificate below | Automatic every 90 days (old keys kept to decrypt) | With the database | `<ops lead>` |
| Data Protection certificate (`DataProtection:CertificatePath` / `:CertificateThumbprint`) | `<Key Vault / HSM / cert store>` | `<2 years>` or on compromise | `<vault backup>`, separately from the database | `<ops lead>` |
| TLS certificates | `<load balancer / ACME>` | Before expiry (`<90 days>`) | n/a | `<ops lead>` |
| IdP client secrets (server-wide `Authentication:Oidc:ClientSecret`, per-organization in **Sign-in**) | Vault / encrypted in the database | ≤ 12 months, and when an administrator leaves | IdP can reissue | `<IdP admin>` |
| SCIM bearer token | Vault (`Scim:BearerToken`) | ≤ 12 months | n/a | `<IdP admin>` |
| Database credentials | Vault / managed identity | ≤ 12 months or managed | n/a | `<DBA>` |
| Backup encryption keys | `<KMS>` | Provider-managed / yearly | KMS | `<ops lead>` |

Rules:

1. Generate keys and secrets with a CSPRNG (`openssl rand -base64 48`); never reuse them across environments.
2. Store them in `<vault>`; never in `appsettings*.json`, tickets, chat or email ([../security/configuration.md](../security/configuration.md)).
3. Restrict access to named administrators; access is reviewed in the quarterly access review.
4. On suspected compromise: rotate at once, revoke sessions (Users › *Sign out everywhere*), open an incident
   ([incident-response-plan.md](incident-response-plan.md)).
5. Losing the Data Protection keys makes encrypted fields unreadable: back up the certificate and test restores
   ([backup-policy.md](backup-policy.md)).

## 4. Evidence

- Configuration records (vault entries with dates), TLS scan results (`<SSL Labs / testssl.sh>` yearly).
- Workbench: `settings.sso` events show when the IdP client secret was replaced (never the value); the startup check
  refuses weak production configuration (see [evidence-catalog.md](evidence-catalog.md)).

## Approval

| Name | Role | Signature | Date |
| --- | --- | --- | --- |
| `<name>` | `<role>` | | |
