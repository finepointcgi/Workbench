# Single sign-on with Okta

Step-by-step setup for an administrator. About 20 minutes. You need a Workbench **Admin** and an Okta **Super
Administrator** or **Application Administrator**.

Workbench signs people in with OpenID Connect (authorization code flow with PKCE). Okta authenticates the person,
including any second factor, and tells Workbench in the ID token who they are and how they signed in (`amr`, `acr`).
Where to configure it (organization vs server-wide) is the same as for Entra ID: see the table at the top of
[sso-entra.md](sso-entra.md). SAML isn't supported; Okta's OIDC web app integration covers everything Workbench needs.

## 1. Find your redirect URIs

In Workbench, open **Admin › Users and security › Sign-in**. Copy the **sign-in redirect URI**
(`https://<your Workbench address>/signin-sso`) and the **sign-out redirect URI** (`.../signout-callback-sso`).

![Screenshot placeholder: Workbench Sign-in tab, redirect URIs](images/sso-okta-01-redirect-uris.png)

## 2. Create the app integration in Okta

1. **Okta Admin Console › Applications › Applications › Create App Integration**.
2. **Sign-in method**: *OIDC - OpenID Connect*. **Application type**: *Web Application*. **Next**.
3. **App integration name**: `Workbench`. **Grant type**: *Authorization Code* only (leave *Refresh Token* and
   *Implicit* off).
4. **Sign-in redirect URIs**: the sign-in redirect URI. **Sign-out redirect URIs**: the sign-out redirect URI.
5. **Assignments**: *Limit access to selected groups* and pick the Workbench groups. **Save**.
6. On the **General** tab, copy the **Client ID** and a **Client secret**. Under *Client Credentials*, keep **Require
   PKCE as additional verification** on.

![Screenshot placeholder: Okta Workbench app General tab](images/sso-okta-02-app.png)

## 3. Pick the authorization server (issuer)

| Okta setup | Issuer URL to enter in Workbench |
| --- | --- |
| API Access Management (custom authorization server), recommended | `https://<your-org>.okta.com/oauth2/default` (or your server's issuer) |
| Org authorization server | `https://<your-org>.okta.com` |

Use the custom server if you want a groups claim and access policies. Check the issuer: open
`<issuer>/.well-known/openid-configuration`; its `issuer` value must match exactly.

**Groups claim** (optional, for role mapping): **Security › API › Authorization Servers › default › Claims › Add Claim**:
name `groups`, *Include in*: **ID Token** (*Always*), value type **Groups**, filter e.g. *Starts with* `Workbench`.
Okta sends group **names**.

## 4. Enter the provider in Workbench

In **Admin › Users and security › Sign-in › Identity provider**:

| Field | Value |
| --- | --- |
| Provider | Okta |
| Button label | e.g. `Acme Okta` |
| Issuer URL | from step 3 |
| Client (application) id | the Client ID |
| Client secret | the Client secret (stored encrypted, never shown again) |

Turn on **Sign in with this provider**, **Save provider** (confirm it's you if asked), then test in a private window. The
`signin.success` event in **Security events** shows the `amr` values Okta sent (e.g. `pwd,mfa,otp`).

## 5. Provisioning

- **Only invited people** (default), or **Also anyone from these email domains** with default roles (never Admin).
- **Groups that grant roles**: map Okta group names (e.g. `Workbench-Finance` → Finance, `Workbench-Admins` → Admin).
- **The provider manages roles**: every sign-in sets roles from groups (never removing the last active admin).
- **Leavers**: with SCIM (self-hosted; Okta **Provisioning** tab, SCIM 2.0 base URL `https://<workbench>/scim/v2`, bearer
  token = `Scim:BearerToken`), unassigning or deactivating someone in Okta deactivates them in Workbench at once.
  Otherwise deactivate them in **Users**.

## 6. Require MFA

1. In Okta, **Security › Authentication Policies**: give the Workbench app a policy that requires **Password + Another
   factor** (or *Any 2 factor types*; phishing-resistant if you can). For the custom authorization server, also check the
   **Global Session Policy** doesn't skip MFA.
2. Sign out of Workbench and in again. The `signin.success` event should show `amr=pwd,mfa,...`.
3. In Workbench **Sign-in › Multi-factor authentication**: **Administrators** or **Everyone**, **Save MFA**. (Workbench
   refuses unless your own current sign-in reported MFA, so administrators can't be locked out by mistake.)

To make Okta step up even when its session is older, ask for a level of assurance:

| Workbench field | Value |
| --- | --- |
| Ask the provider for | `urn:okta:loa:2fa:any` (or `phr` / `phrh` for phishing-resistant, Okta Identity Engine) |
| acr values that prove MFA | the same value |

Workbench sends it as `acr_values`; Okta returns it in the `acr` claim once satisfied. A sign-in without MFA is refused
with a clear message and a `signin.mfa_required` event.

![Screenshot placeholder: Okta authentication policy requiring two factors](images/sso-okta-03-policy.png)

## 7. Sessions and re-authentication

Same as [Entra ID, section 7](sso-entra.md#7-sessions-and-re-authentication): pay rates, bank details, vendor tax ids,
approval workflows and sign-in settings need a sign-in within the re-authentication window. **Sign in again** sends the
person to Okta with `prompt=login` and `max_age=0`; Okta asks for credentials (and MFA under its policy) and returns a
fresh `auth_time`. A response with an old `auth_time` is refused.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `400 Bad Request: redirect_uri` | Add the exact sign-in redirect URI to the app. |
| "The identity provider didn't send an email" | Add the `email` scope to the authorization server's scopes (it's requested), and make sure users have an email. |
| `Issuer mismatch` in the server log | The issuer URL must match `issuer` in the discovery document (org vs custom server). |
| MFA refusal | Read the `amr`/`acr` in the `signin.mfa_required` detail and add those values, or fix the Okta policy. |
| Groups don't map | The groups claim must be in the **ID token**, and names are case-sensitive. |

The evidence table in [sso-entra.md](sso-entra.md#8-evidence-for-auditors) applies unchanged.
