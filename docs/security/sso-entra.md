# Single sign-on with Microsoft Entra ID

Step-by-step setup for an administrator. About 20 minutes. You need a Workbench **Admin** and, in Entra ID, the
**Application Administrator** (or Cloud Application Administrator) role; **Conditional Access** needs Entra ID P1.

Workbench signs people in with OpenID Connect (authorization code flow with PKCE). It never sees passwords or second
factors: Entra ID does that and tells Workbench, in the ID token, who the person is and how they authenticated.

There are two places to configure an identity provider:

| Deployment | Where | Use when |
| --- | --- | --- |
| **SaaS (MultiTenant)** or self-hosted, per organization | **Admin › Users and security › Sign-in** (this guide) | Each customer organization brings its own Entra tenant. Secrets are stored encrypted in the organization's database. |
| Self-hosted, server-wide | `Authentication:Oidc:*` configuration ([configuration.md](configuration.md)) | One Entra tenant for the whole server, plus bearer tokens for the desktop app. |

Both can be on at once (the server-wide one is how a new SaaS tenant's first admin gets in); the organization's
provider wins for the **Sign in** button, and you can refuse the server-wide one (step 6).

> SAML isn't supported. Entra ID's OpenID Connect app registration does everything Workbench needs.

## 1. Find your redirect URIs

In Workbench, open **Admin › Users and security › Sign-in**. The *Identity provider* box shows:

- **Sign-in redirect URI**: `https://<your Workbench address>/signin-sso` (SaaS: `https://acme.workbench.example/signin-sso`)
- **Sign-out redirect URI**: `https://<your Workbench address>/signout-callback-sso`

![Screenshot placeholder: Workbench Sign-in tab, redirect URIs highlighted](images/sso-entra-01-redirect-uris.png)

## 2. Register Workbench in Entra ID

1. Go to **Microsoft Entra admin center › Identity › Applications › App registrations › New registration**.
2. **Name**: `Workbench`. **Supported account types**: *Accounts in this organizational directory only (single tenant)*.
3. **Redirect URI**: platform **Web**, value = the sign-in redirect URI from step 1. Select **Register**.
4. On the app's **Overview**, copy the **Application (client) ID** and the **Directory (tenant) ID**.
5. **Authentication**: add the sign-out redirect URI as **Front-channel logout URL**. Leave *ID tokens* and *Access
   tokens* (implicit grant) **unchecked**: Workbench uses the code flow.
6. **Certificates & secrets › New client secret**: description `Workbench`, expiry **12 months** (or your policy). Copy the
   secret **Value** now (it's shown once). Put a reminder in your calendar to rotate it before it expires.
7. **Token configuration › Add optional claim › ID**: add `email` and `auth_time` (and accept the *Microsoft Graph email
   permission* prompt). `amr` and `acr`/`acrs` are sent by default when they apply.

![Screenshot placeholder: Entra app registration Overview with client and tenant IDs](images/sso-entra-02-app-registration.png)

## 3. Choose who can sign in

1. **Enterprise applications › Workbench › Properties**: set **Assignment required?** to **Yes**.
2. **Users and groups › Add user/group**: assign the groups whose members use Workbench.

People who aren't assigned are stopped by Entra ID before they reach Workbench. Workbench applies its own rules on top
(invites or allowed domains, step 5).

## 4. Enter the provider in Workbench

In **Admin › Users and security › Sign-in › Identity provider**:

| Field | Value |
| --- | --- |
| Provider | Microsoft Entra ID |
| Button label | e.g. `Acme Microsoft account` |
| Issuer URL | `https://login.microsoftonline.com/<Directory (tenant) ID>/v2.0` |
| Client (application) id | the Application (client) ID |
| Client secret | the secret **Value** (stored encrypted with the server's Data Protection keys; never shown again) |
| Extra scopes | leave empty |

Turn on **Sign in with this provider** and **Save provider**. Saving needs a recent sign-in: if Workbench asks you to
confirm it's you, sign in again and repeat.

Test it: open a private browser window, go to Workbench, choose **Sign in**, and sign in with an assigned account. In
**Security events**, the `signin.success` row shows `sso; amr=...`: the authentication methods Entra ID reported.

![Screenshot placeholder: Workbench Security events showing signin.success with amr values](images/sso-entra-03-signin-event.png)

## 5. Provisioning: invites, allowed domains, groups

*Just-in-time provisioning* decides who gets a Workbench account on first sign-in:

- **Only invited people** (default): invite people in **Invites**; their first sign-in accepts the invite and its roles.
- **Also anyone from these email domains**: e.g. `acme.com`. New people get the *roles for people created on first
  sign-in* (never Admin) plus any mapped group roles.

**Groups that grant roles**: in Entra ID, **Token configuration › Add groups claim › Groups assigned to the application**
(this avoids the 200-group overage, where Entra ID leaves groups out of the token). Entra ID sends group **object
IDs**: map each one (copy it from **Groups › <group> › Object ID**) to Workbench roles, e.g. `Workbench Finance` → Finance.

**The provider manages roles** (optional): every sign-in then sets each person's organization roles from their groups.
Workbench never removes the last active administrator this way; it records a failed `user.roles_changed` event instead.

**Leavers**: with SCIM provisioning (Users tab and [configuration.md](configuration.md), self-hosted), Entra ID deactivates
people in Workbench when they're unassigned or disabled; otherwise deactivate them in **Users**. Deactivated people
can't sign in and their open sessions end at once.

## 6. Require MFA

Workbench enforces that the sign-in *used* MFA; Entra ID performs it.

1. In Entra ID, create a **Conditional Access** policy: *Users*: the Workbench groups; *Target resources*: Workbench;
   *Grant*: **Require multifactor authentication** (or *Require authentication strength › Phishing-resistant MFA*). Turn it on.
2. Sign out of Workbench and sign in again: the `signin.success` event should show `amr=pwd,mfa` (or `rsa`, `fido`, ...).
3. In **Sign-in › Multi-factor authentication**, choose **Administrators** or **Everyone** and **Save MFA**.

Workbench refuses to save a stricter MFA rule unless *your own* current sign-in reported MFA, so you can't lock
administrators out by mistake. Afterwards, a sign-in without MFA is refused with: *"Your organization requires
multi-factor authentication, and your identity provider reported that this sign-in didn't use it."* and a
`signin.mfa_required` security event.

**Accepted values.** By default `amr` values `mfa hwk swk otp fido sms tel face fpt pop` prove MFA. If your tokens don't
carry `amr`, use a Conditional Access **authentication context** instead:

1. **Protection › Conditional Access › Authentication contexts › New**: id `c1`, name `Workbench MFA`, publish to apps.
2. A Conditional Access policy targeting authentication context `c1` that requires MFA.
3. In Workbench: *acr values that prove MFA* = `c1`, *Ask the provider for* = `c1`. Workbench then sends a `claims`
   request for `acrs=c1` on every sign-in and Entra ID returns `acrs: ["c1"]` after enforcing the policy.

![Screenshot placeholder: Conditional Access policy requiring MFA for Workbench](images/sso-entra-04-conditional-access.png)

**Only this provider.** Once your provider works, turn on **Only allow sign-in through this provider** to refuse the
server-wide sign-in for your organization. Workbench lets you turn it on only while you're signed in through your
provider.

## 7. Sessions and re-authentication

**Sessions and re-authentication** shows the server's limits (30 minutes idle, 12 hours absolute by default). You can
set a *shorter* session limit for your organization.

Some actions need a sign-in within the **re-authentication window** (15 minutes by default): showing pay rates,
showing or changing bank account details, showing a vendor's tax id, changing approval workflows, and changing these
sign-in settings. When the sign-in is older, Workbench shows **Confirm it's you**; **Sign in again** sends the person to
Entra ID with `prompt=login` (Entra ID asks for credentials again, and Conditional Access applies) and back to the same
page. If Entra ID returns an old `auth_time` anyway, the re-authentication doesn't count. Refusals are
`access.reauth_required` events; successes `signin.reauthenticated`.

## 8. Evidence for auditors

| Control | Evidence in Workbench |
| --- | --- |
| ISO 27001 A.5.17, A.8.5; NIST 800-171 3.5.3 (MFA) | `signin.success` events with `amr`; `signin.mfa_required` refusals; the `settings.sso` event that turned MFA on |
| A.5.16, A.5.18; 3.1.1, 3.5.1 (identities) | Users list, invites, `user.created` (with "Just-in-time"), SCIM events, access reviews |
| A.8.2; 3.1.5 (privileged access) | Group mappings, `user.roles_changed` by `sso` |
| A.8.5; 3.1.11, 3.5.x (sessions) | Session policy, `session.rejected`, `access.reauth_required`, `signin.reauthenticated` |

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `AADSTS50011` redirect URI mismatch | The redirect URI must match step 1 exactly, including `https` and no trailing slash. |
| "Your sign-in wasn't accepted" | No pending invite and the domain isn't allowed, or the user is deactivated. See **Security events** (`signin.failure`). |
| MFA refusal although MFA was used | Check the `amr` in the `signin.mfa_required` event detail; add that value to *amr values*, or use an authentication context. |
| `AADSTS7000215` invalid client secret | The secret expired or the *Secret ID* was copied instead of the *Value*. Enter a new one. |
| Roles from groups don't apply | Groups claim missing (overage): use *Groups assigned to the application* and assign the groups to the app. |

The desktop app signs in with the server-wide provider only (`Authentication:Oidc`); a per-organization provider applies
to browsers. Sensitive actions in the desktop app need its token to carry `auth_time`; otherwise do them in the browser.
