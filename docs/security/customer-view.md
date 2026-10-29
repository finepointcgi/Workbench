# Customer view: the link a contracting officer opens

A contract's **Customer link** tab (Finance only) makes a link, `/c/{token}`, that anyone holding it can open **without an
account**. It shows one contract: its value, funding by line item, what has been billed and paid, and the sent and paid
invoices. It is read only.

## What it never shows
Costs, bill or cost rates, indirect rates, people, plans, margins, draft or approved-but-unsent invoices, internal notes.
`CustomerViewDto` has no fields for them, so they can't leak by accident.

## How the link is protected
- **256 random bits**, base64url. Only the SHA-256 hash is stored (`CustomerShareLink.TokenHash`), so a leaked database
  has no working links. The address is shown once, when it is made.
- **Expires** (90 days by default, 365 at most) and **can be ended** by Finance at any time, effective on the next request.
- A wrong, ended or expired link answers the same page with a 404: nothing says which it was.
- The response is `Cache-Control: no-store`, `X-Robots-Tag: noindex`, and the page has `noindex` and `referrer` meta tags.
- Requests to `/c` are rate limited per client IP, like sign-in.
- Plain server-rendered HTML: no circuit, no scripts, nothing the visitor can post.
- Finance sees each link's label, who made it, when, the view count and the last view.
- Making and ending a link are audit events (`CustomerShareLink`, `Created` / `Revoked`).

## Who can do what
Only people who manage billing (Finance, Admin) can list, make or end links
(`PermissionCheckingCustomerShareLinkService`). Anyone else gets "ask Finance".
