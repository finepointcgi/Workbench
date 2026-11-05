# Built-in accounts (email, password and authenticator app)

Workbench has its own accounts for organizations without single sign-on, and as a fallback next to it. People sign in
with their email and a password, plus a code from an authenticator app when two-step verification is on or required.
Organization SSO ([Single sign-on with Microsoft Entra ID](sso-entra.md), [Single sign-on with Okta](sso-okta.md)) keeps working the same way, and an
organization can require it instead.

## Which sign-in people see

| Organization setup | `/account/signin` shows |
| --- | --- |
| No SSO configured | Email and password (the default). |
| SSO configured | Email and password, with **Sign in with *your organization*** next to it. |
| SSO configured and **Only allow sign-in through this provider** on | Straight to the identity provider. Password sign-in, invite passwords and forgot-password links are refused (`signin.failure`, reason `org_idp`). |
| Development, or the public demo | The user picker (`/account/dev-signin`) as before, with a link to email and password sign-in; the sign-in page links back to **Pick a demo user**. |

`Authentication:Passwords:Enabled=false` turns built-in accounts off for the whole server. The MultiTenant host admin
site never has them (it has no user table); it signs in with `Authentication:Oidc`.

## Getting an account

- **Invite** (Admin › Users and security › Invite): the email has a one-time link to `/account/accept-invite`. The
  person chooses a password and lands signed in; the sign-in gate accepts the invite and gives its roles. The link lasts
  as long as the invite (7 days) and works once. A new invite for the same email, or
  revoking it, makes the old link useless. The same email still mentions SSO for organizations that use it.
- **New tenant** (MultiTenant): the first administrator's invite email has the same link, on the tenant's own address.
- **Bootstrap administrators** (`Authentication:Admins`, SingleTenant) on a server without SSO: use **Forgot your
  password?** with that email to set a first password.
- **People who signed in with SSO** can add a password from **Password and sign-in** (an emailed link), when the
  organization doesn't require SSO.

## Passwords

- Hashed with ASP.NET Core's `PasswordHasher` (PBKDF2 with HMAC-SHA512, 100,000 iterations, random salt, versioned so
  hashes are upgraded on the next sign-in). Stored in `PasswordCredential`, keyed by email.
- At least 12 characters (`Authentication:Passwords:MinimumLength`), at most 128. No composition rules (NIST SP 800-63B).
  Refused: frequent passwords from breach lists, a common word or keyboard row with digits around it (`Password2026!`),
  runs (`123456789012`), five or fewer distinct characters, and passwords containing the email.
- Never logged, never in a URL, never in a security event. Forms post them to the server over HTTPS only.
- Changing the password (it needs the current one) or resetting it signs the person out everywhere else.

## Lockout and rate limits

- Five wrong passwords or codes in a row (`MaxFailedAttempts`) lock the account for 15 minutes (`LockoutDuration`):
  `signin.locked_out`. While locked, even the right password is refused without being checked. Wrong authenticator codes
  count toward the same limit, and a correct password doesn't reset it while a code is still owed.
- An unknown email gets the same answer as a wrong password, and costs the same hashing time.
- Every `/account` path, including these, is rate-limited per client IP (`RateLimiting:SignInPerMinute`, default 20).

## Forgot password

`/account/forgot-password` always answers "If *email* has a Workbench account, we've sent it a link", whether or not it
does. A link goes only to active users, people with a pending invite, and bootstrap administrators; the security log
(`user.password_reset_requested`) records which, with outcome `failure` when nothing was sent. The link lasts one hour
(`ResetLinkLifetime`), works once, and is replaced by a newer one. Only the SHA-256 hash of its token is stored. An
administrator can send the same link from Admin › Users (**Email a password reset link**).

## Two-step verification (authenticator app)

- **Set up** from **Password and sign-in** (user menu), or at sign-in when the organization requires it: scan the QR
  code (or type the setup key) into Microsoft Authenticator, Google Authenticator, 1Password, Okta Verify or similar,
  and enter its code. TOTP per RFC 6238: SHA-1, 6 digits, 30-second steps, one step of drift either way; a code can't be
  used twice. The shared secret is encrypted with the host's Data Protection keys (per tenant).
- **Recovery codes:** ten, shown once after setup; each signs in once instead of an app code. New ones can be made (the
  old ones stop working). Only hashes are stored.
- **Required:** Admin › Users and security › Sign-in › Multi-factor authentication (administrators or everyone) applies
  to built-in accounts too. Someone without an app is taken to set one up after their password, and isn't signed in
  until they have. They can't remove their app while it's required. The session records `wb_mfa=true` after a code.
- **Lost phone:** an administrator uses **Reset MFA** in Admin › Users. It removes the app and recovery codes and signs
  the person out everywhere; their next sign-in sets up a new app if MFA is required. Check who's asking first.

## Sessions

The auth cookie is the same as for SSO sign-ins (idle timeout, absolute lifetime, deactivation, "sign out
everywhere"), with sign-in method `password`. Between the password and the code the browser holds only a 10-minute
`.Workbench.Pending` cookie, scoped to `/account`, that no page or API accepts. Sensitive actions that need a recent
sign-in send password users back to the sign-in page to enter their password (and code) again. **Password and sign-in**
also has **Sign out everywhere** for the person's own sessions.

## Security events

| Event | When |
| --- | --- |
| `signin.success` | Detail `password; amr=pwd` or `password; amr=pwd,otp`. |
| `signin.failure` | Wrong password or code (with the count), unknown email, locked account, SSO required. |
| `signin.locked_out` | The limit was reached; detail says until when. |
| `signin.recovery_code` | A recovery code was used; detail says how many are left. |
| `signin.reauthenticated` | A password re-entry for a sensitive action. |
| `user.password_set` | From an invite, a reset link, or a change. |
| `user.password_reset_requested` | Forgot password or an administrator; outcome `failure` when nothing was sent. |
| `user.mfa_enabled`, `user.mfa_disabled`, `user.mfa_recovery_codes` | App set up or replaced; removed by the person or reset by an administrator (actor); new recovery codes. |
| `user.sessions_revoked` | After a password reset or change, an MFA reset, or "sign out everywhere". |

## For developers

`Application/Identity/Passwords/PasswordAccounts` holds the credential logic; `Web/Identity/PasswordSignIn` the pages
(plain HTML forms outside the Blazor circuit, because cookies need real responses). Once the password and any code
check out, the pages call `SignInGate.EvaluateAsync(email, null, "password", claims)`, which applies deactivation,
invites, the "SSO only" rule and the MFA requirement like every other sign-in. Tests:
`tests/Workbench.Tests/Identity/BuiltInAccountWebTests.cs`.
