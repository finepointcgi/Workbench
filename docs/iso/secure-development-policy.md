# Secure development policy

| | |
| --- | --- |
| Owner | `<Engineering lead>` |
| Approved by | `<ISMS manager>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.8.4, A.8.8, A.8.25–A.8.29, A.8.31–A.8.33 |

## 1. Scope

All code, configuration and pipelines for Workbench (web host, API, worker, desktop client) and their dependencies,
by employees and contractors (A.8.30: contractors follow this policy too).

## 2. Requirements and design (A.8.26, A.8.27)

- New features state their security requirements: who may do it (org role, workspace role), what is logged, what
  data is sensitive (cost rates, PII, CUI).
- Permission checks run on the server, in the permission decorators registered by `AddWorkbenchIdentity`; the UI only
  hides affordances. Something a user can't see is 404; something they can't change is 403.
- Changes to external interfaces (new endpoints, integrations, hosting) update `../security/threat-model.md`.
- Follow `../CONVENTIONS.md` (layering: UI never references EF Core; money as `decimal`; append-only audit).

## 3. Secure coding (A.8.28)

- Validate input at the service boundary (`ArgumentException` → 400); use EF Core (parameterized) for data access;
  never build SQL from strings.
- Encode output (Razor does this); no `MarkupString` with user data; no inline scripts (the CSP forbids them).
- Never log secrets, tokens, passwords or cost/bill rates. Security events carry short details only.
- Secrets come from configuration (vault or environment variables), never from code or committed files.
- Sensitive actions (role changes, deactivation, exports, settings) write a security event.
- Treat analyzer warnings as errors where the project does (MudBlazor `MUD0002`).

## 4. Source code and branches (A.8.4, A.8.32)

- Code lives in `<GitHub org/repo>`; access is by role and reviewed with the access reviews.
- `main` is protected: pull requests only, at least one approving review, **CODEOWNERS review required** for
  security-sensitive paths (`.github/CODEOWNERS`), passing CI required, no force pushes.
- Signed commits: `<required / recommended>`.

## 5. Pipeline and testing (A.8.29, A.8.8)

The CI workflow (`.github/workflows/ci.yml`) runs on every pull request and push to `main`:

| Check | Fails the build when |
| --- | --- |
| Build and tests (`dotnet test Workbench.Server.slnf`) | Any test fails |
| Vulnerable packages (`dotnet list package --vulnerable --include-transitive`) | Any High or Critical advisory |
| Secret scanning (gitleaks, full history) | A secret is found |
| SBOM (CycloneDX) | Generation fails; the SBOM is kept as a build artifact for 90 days |

Security behaviour has automated tests (permission enforcement, deactivation, SCIM authentication, security headers,
rate limiting, startup configuration checks, retention). A bug fix for a security issue adds a regression test.

An independent penetration test runs `<yearly and before major releases>`; findings go into the risk register.

## 6. Vulnerability management (A.8.8)

| Severity | Fix deadline |
| --- | --- |
| Critical | 14 days (or mitigate within 72 hours) |
| High | 30 days |
| Medium | 90 days |
| Low | Next planned release |

Sources: CI advisories, GitHub Dependabot `<enable>`, `SECURITY.md` reports, pen tests. Dependencies are updated at
least monthly.

## 7. Environments and test data (A.8.31, A.8.33)

- Development, test and production are separate, with separate credentials and databases.
- The dev sign-in page exists only in Development; the web host refuses to start elsewhere with weak configuration.
- Tests use synthetic seeded data. Production data isn't copied to other environments unless masked and approved by
  `<data owner>`.

## 8. Release and deployment (A.8.32, A.8.19)

- Only artifacts built by CI from `main` are deployed. Releases have notes and a rollback plan.
- Database schema changes are reviewed; back up before deploying (`../operations/backup-restore.md`).
- Emergency changes are reviewed retrospectively within `<2>` business days.

## 9. Training

Developers complete secure coding training `<yearly>` (OWASP Top 10, ASP.NET Core security, this policy).
