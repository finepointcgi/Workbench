# API keys

An API key lets a program (a data warehouse load, a script, another system) call the Workbench REST API (`/api/v1`) without a
person signing in. Administrators create and revoke keys under **Admin › Integrations › API keys**.

## How a key is checked

1. **It is its own user.** Creating a key creates a service user (`key-…@api-keys.workbench.invalid`, shown in the user list as
   "API key: name") with the organization roles you chose. Every request made with the key runs as that user, so the same
   permission checks apply as for a person with those roles: an Employee-role key sees only what an employee may, Finance-only
   data needs the Finance role, and so on. Nothing about a key bypasses the permission decorators.
2. **Scopes narrow it further.** A key may only call the API areas you allow, each as **read** (`GET`) or **read and write**
   (every method). A request outside them gets `403` before it reaches any endpoint. Areas are listed in
   `Contracts/Integrations/ApiKeyScopes.cs` (sheets, projects, time, billing, purchasing, accounting, people, crm, materials,
   reports, setup, and mcp for [AI assistants](mcp.md)). Some reports run with `POST`; give the area write access for those.
3. **Never for administration.** No scope covers user and sign-in administration (`/admin`, `/users`), the host site,
   integrations themselves, imports, the go-live tools, the assistant, notifications or support. Even an Admin-role key with
   every scope can't create keys or change webhooks.
4. **API only.** Keys work only under `/api/`. They don't sign anyone in to the web pages, the live-update hubs or SCIM.

The key's roles are stored on the key: if someone changes its service user's roles in the user list, the next request puts
them back. To change what a key may do, create a new key and revoke the old one.

## Using a key

```bash
curl -H "Authorization: Bearer wbk_…" https://workbench.example.com/api/v1/time/timesheets
```

`X-Api-Key: wbk_…` works too. Keys are shown **once**, when created; Workbench keeps only a SHA-256 hash. Store the key in your
vault or the calling system's secret store. Requests are rate limited per key like any other API user.

## Lifetime, revocation and audit

- Keys can expire on a date (up to three years); the default is no expiry.
- **Revoke** stops the key at once and deactivates its service user. Revoked keys can't be restored.
- Last use (time and client address, updated at most once a minute) shows in the key list.
- The security event log (**Admin › Users and security › Security events**) records `apikey.created` (scopes, roles; never
  the key), `apikey.revoked`, and `apikey.rejected` for unknown, revoked or expired keys and for requests outside a key's scopes, and `mcp.tool_call` for each
  tool an [AI assistant](mcp.md) calls with a key.
  An access review lists key service users like other users.
- Keys belong to one tenant (its database): a key from one organization is unknown at every other.
