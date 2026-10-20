# Supplier portal: sign-in and isolation

The supplier portal (`/supplier`) is for people at our vendors. They are never Workbench users: no `AppUser`, no
organization roles, no workspace memberships, and nothing in the internal app or API accepts them.

## Sign-in kind

- **Scheme:** `SupplierPortal`, a cookie scheme of its own (`Workbench.Web/Supplier/SupplierPortalAuthentication.cs`).
  Cookie `.Workbench.Supplier`, `Path=/supplier`, HttpOnly, SameSite=Lax, Secure outside Development. 30-minute idle
  timeout (sliding), 12-hour absolute lifetime.
- **Selector:** the `Workbench` policy scheme forwards every `/supplier` request to `SupplierPortal` and nothing else
  to it. An internal user's cookie isn't read on portal paths, and the portal cookie is never sent (or accepted)
  elsewhere.
- **Claims:** `wb_supplier_contact`, `wb_supplier_vendor`, `wb_supplier_name`, `wb_supplier_session`, sign-in time and
  (MultiTenant) `wb_tenant`. No email, name or role claim types; `ClaimsUser.EmailOf` returns null for this identity, so
  `ICurrentUser` and `AccessControl` see nobody.
- **Ways in:** an emailed single-use link (`/supplier/signin/link`, 15 minutes, SHA-256 of the token stored) or a
  password the contact sets under Account (PBKDF2; 12+ characters; 5 failures lock 15 minutes). Sign-in endpoints sit in
  the sign-in rate limit partition.
- **Session checks on every request:** the contact is active, its vendor isn't inactive, the session version matches
  (deactivation and password changes bump it), and the tenant matches.

## Authorization

`ISupplierPortalService` never takes a vendor id. `VendorScopedSupplierPortalService` (the permission decorator,
registered in `AddWorkbenchIdentity`) resolves the contact from `ISupplierPrincipal` on every call, requires an RFQ to
have been sent to the vendor and a purchase order to be the vendor's and issued, and otherwise answers not found. The
implementation filters by the caller's vendor again. Writes go through `SupplierDocuments`, the same rules EDI uses.

## Audit

Every portal call is a `SupplierPortalEvent` (contact, vendor, action, record, outcome, IP), including reads,
refusals and sign-in attempts; buyers see it on Purchasing › Supplier portal › Activity. Changes (quotes,
acknowledgements, ship notices, invoices) are also `AuditEvent` rows attributed to "Name <email> (supplier portal)".

## Managing access

Finance and Admin add contacts (one vendor per email; an internal user's email is refused), resend links, and end or
restore access on Purchasing › Supplier portal. Ending access takes effect on the contact's next request.
