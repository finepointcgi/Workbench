# Access control policy

| | |
| --- | --- |
| Owner | `<IT lead>` |
| Approved by | `<ISMS manager / top management>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.5.15, A.5.16, A.5.17, A.5.18, A.6.5, A.8.2, A.8.3, A.8.5 |

## 1. Principles

- **Least privilege, need to know.** People get the access their role needs and no more.
- **One identity per person.** No shared accounts. Service accounts have a named owner.
- **Single sign-on with MFA.** Workbench access goes through the company identity provider `<Entra ID / Okta>` with
  multi-factor authentication `<phishing-resistant for administrators>`.
- **Grant by role, record everything.** Role changes, invites, deactivations and access reviews are recorded in
  Workbench's security event log.

## 2. Roles in Workbench

| Role | Grants | Who may hold it | Approved by |
| --- | --- | --- | --- |
| Employee (everyone) | Own time and expenses; sheets of workspaces they belong to | All staff | Line manager (via HR onboarding) |
| Timekeeper | View everyone's timesheets | Payroll/timekeeping staff | `<Controller>` |
| Finance | Rates, contracts, budgets, project setup, fiscal close, financial exports | Finance staff | `<CFO / Controller>` |
| Admin | Everything, including users, retention and the security log | At most `<3>` named people | `<CEO / ISMS manager>` |
| Workspace Viewer / Commenter / Editor / Owner | Sheets in a workspace | As needed per project | Workspace owner |

Rules: nobody approves their own timesheet; nobody can remove their own Admin role or deactivate themselves; there
is always at least one active Admin (Workbench enforces these). Admin rights aren't used for routine work where a
lesser role suffices.

## 3. Joiners, movers, leavers (A.5.16, A.6.5)

| Event | Action | Who | Deadline |
| --- | --- | --- | --- |
| Joiner | HR request → account in the IdP → SCIM provisions Workbench (or an Admin sends an invite, valid 7 days) → Admin assigns org roles; workspace owners add workspace access | HR, IT, Admin | Start date |
| Mover | Remove roles no longer needed, add new ones; workspace owners adjust membership | Manager, Admin | 5 business days |
| Leaver | Disable in the IdP (SCIM deactivates in Workbench and ends sessions); otherwise an Admin deactivates in **Users and security** | HR, IT | Same day; at latest 1 business day |
| Suspected compromise | Admin uses **Sign out everywhere** or **Deactivate**; reset credentials in the IdP | Admin, IT | Immediately |

Deactivated users are never deleted: their timesheets, approvals and audit records keep their author.
Outside Development, only invited, SCIM-provisioned or bootstrap-admin people can sign in
(`Authentication:RequireInvite`).

## 4. Authentication and sessions (A.5.17, A.8.5)

- Passwords and MFA are managed by the IdP under `<IdP password and MFA standard>`. Setup:
  [../security/sso-entra.md](../security/sso-entra.md) or [../security/sso-okta.md](../security/sso-okta.md).
- **MFA is enforced by Workbench** as well (**Users and security › Sign-in › Multi-factor authentication**): set to
  `<Everyone / Administrators>`. A sign-in whose token doesn't report MFA (`amr`/`acr`) is refused and logged
  (`signin.mfa_required`).
- **Only the organization's IdP**: `<on/off>` ("Only allow sign-in through this provider").
- **Just-in-time provisioning**: `<invite only / allowed domains: ...>`; roles from IdP groups `<list of groups → roles>`;
  `<the IdP manages roles: yes/no>`.
- Workbench sessions expire after 30 minutes idle and 12 hours after sign-in (server configuration), or after
  `<n>` hours (organization limit). Don't lengthen without a risk assessment. Open browser sessions re-check every
  minute and on reconnect that the user is still active.
- **Re-authentication**: showing pay rates, bank account numbers or vendor tax ids, changing bank accounts, approval
  workflows or sign-in settings needs a sign-in within `<15>` minutes; Workbench sends the person back to the IdP with
  `prompt=login`.
- API tokens for the desktop app come from the IdP and stop working when the user is deactivated or signed out
  everywhere.
- Secrets (OIDC client secrets, SCIM token, database password) live in `<vault>` or encrypted in Workbench, are rotated
  yearly and on staff change, and are never committed (CI scans for them). See [cryptography-policy.md](cryptography-policy.md).

## 5. Access reviews (A.5.18)

- **Quarterly**, an Admin runs an access review in **Users and security → Access reviews**. Workbench snapshots every
  active user's org roles, workspace roles, last sign-in and linked employee, and flags anyone with no sign-in in 90
  days.
- Each user is marked Keep or Revoke with a comment; Revoke deactivates them at once. The reviewer consults
  `<managers / HR leaver list>` for roles they can't judge.
- The completed review, with every decision, the reviewer and time, is kept as evidence (download the CSV for the
  audit file).
- Privileged roles (Admin, Finance) are also reviewed by `<CEO / CFO>` at each review.
- Infrastructure access (cloud console, database, GitHub) is reviewed on the same cadence: `<where recorded>`.

## 6. Privileged and infrastructure access (A.8.2)

- Separate administrator accounts for cloud, database and server administration; MFA required; no daily use.
- Production database access is limited to `<role>`; direct data changes need a change ticket.
- Break-glass account: `<description, storage, alerting on use>`.

## 7. Monitoring

Admins review the security event log `<weekly>` for sign-in failures, `access.denied`, `session.rejected`,
`scim.request` failures, role changes and exports, and raise incidents as needed.

## 8. Exceptions

Exceptions need a risk entry in `risk-register.md`, the owner's approval and an expiry date.
