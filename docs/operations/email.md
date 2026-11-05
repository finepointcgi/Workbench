# Email

Workbench sends notification emails (approvals, reminders, the daily digest, Limitation of Funds notices) and
scheduled reports. Pick a provider with `Email:Mode`; every mode sends a plain-text and an HTML part, and attachments
for scheduled reports. Deliveries are counted (`workbench.email.sent`) and a recent failure turns the readiness check's
`email` entry *Degraded* ([Monitoring](monitoring.md)). Addresses are never logged.

## Providers

### SMTP relay with TLS (`Email:Mode=Smtp`)

Microsoft 365, Google Workspace, Amazon SES SMTP, SendGrid SMTP, Postfix: anything with authenticated submission.

```
Email__Mode=Smtp
Email__From=workbench@example.com
Email__Smtp__Host=smtp.office365.com
Email__Smtp__Port=587
Email__Smtp__Security=Auto           # 587 → STARTTLS (required), 465 → implicit TLS
Email__Smtp__UserName=workbench@example.com
Email__Smtp__Password=<secret>
```

TLS is mandatory: with STARTTLS the connection fails instead of falling back to plain text, and the server
certificate is validated. `Email:Smtp:Security=None` exists for a local relay on the same host and is refused in
Production. (MailKit is used for SMTP; `System.Net.Mail` couldn't do implicit TLS.)

### SendGrid (`Email:Mode=SendGrid`)

```
Email__Mode=SendGrid
Email__From=workbench@example.com      # a verified sender or an authenticated domain
Email__SendGrid__ApiKey=<secret>       # a restricted key with only "Mail Send"
```

Uses the v3 Mail Send API over HTTPS. Click and open tracking are turned off per message so links in notifications
aren't rewritten through SendGrid.

### Amazon SES (`Email:Mode=Ses`)

```
Email__Mode=Ses
Email__From=workbench@example.com      # a verified identity (domain) in SES
Email__Ses__Region=us-gov-west-1
Email__Ses__ConfigurationSet=workbench # for bounce/complaint events (below)
```

Credentials come from the default AWS chain (the task or instance role, needing `ses:SendRawEmail` on the identity
and configuration set). Static keys (`Email:Ses:AccessKeyId`/`SecretAccessKey`) work but prefer the role. Move the
SES account out of the sandbox before go-live. Messages are sent as raw MIME through the SES v2 API.

### Pickup folder (`Email:Mode=Pickup`)

Development: writes `.eml` files to `Email:PickupFolder`. In Production it makes readiness *Degraded* so it can't go
unnoticed.

## DNS for the sender domain

- **SPF**: include the provider (`include:spf.protection.outlook.com`, `include:sendgrid.net`, `include:amazonses.com`).
- **DKIM**: publish the provider's DKIM records (SendGrid domain authentication, SES Easy DKIM, M365 DKIM).
- **DMARC**: start with `p=none; rua=mailto:dmarc@example.com`, move to `quarantine`/`reject` once reports are clean.
- Use a subdomain (`notify.example.com`) for application mail so its reputation is separate from people's mail.

## Bounces and complaints

Workbench receives the provider's bounce and complaint events and keeps a list of addresses mail can't reach, in each
tenant's database:

- **What it does with them.** A hard bounce or a spam complaint marks the address. Workbench then sends it only
  essential mail (password resets, invitations, sign-in links, "your organization is ready"); notifications, digests,
  automation emails and scheduled reports to it are held back (counted as `outcome=suppressed` in
  `workbench.email.sent`). Soft bounces and temporary refusals are recorded but don't stop mail.
- **Who sees it.** Admins (*Administer users* or *Manage sign-in and security*) see "Emails to this address are
  bouncing" next to the user on **Admin › Users and security › Users**, and every bouncing address (users and
  contacts) with the provider's reason on the **Security events** tab, where they can clear it. The person sees a
  notice on **My profile** and can press **My mailbox works now**. Clearing resumes mail; a new bounce marks it again,
  while an event older than the clear (a late or replayed webhook) is ignored.
- **Tenants.** In MultiTenant mode every message carries the tenant (`wb_tenant`: a SendGrid custom argument, an SES
  message tag), and each event is recorded in that tenant's database. Point the provider at the base domain.

### SendGrid: signed Event Webhook

1. *Settings › Mail Settings › Event Webhook*: post URL `https://<host>/webhooks/email/sendgrid` (MultiTenant: the
   base domain), events **Bounced**, **Dropped** and **Spam Reports** (others are ignored).
2. Turn on **Signed Event Webhook** and copy the verification key into `Email__SendGrid__WebhookVerificationKey`.
   Every request must carry a valid ECDSA signature over its timestamp and body; others get 401. Without the key the
   endpoint answers 404.

### Amazon SES: events through SNS

1. Create an SNS topic (e.g. `workbench-ses-events`) and add an event destination to the configuration set
   (`Email:Ses:ConfigurationSet`) publishing **Bounce** and **Complaint** to it. (Identity notifications to the same
   topic work too.)
2. Set `Email__Ses__BounceTopicArns=arn:aws:sns:REGION:ACCOUNT:workbench-ses-events` (several: comma-separated, or
   `__0`, `__1`). Messages from any other topic get 403: anyone can create an SNS topic, and SNS signs those too.
3. Subscribe `https://<host>/webhooks/email/ses` to the topic (protocol HTTPS). Workbench verifies the signature and
   confirms the subscription by itself.

Signatures are checked as AWS documents: the signing certificate must come over HTTPS from an
`sns.<region>.amazonaws.com` host, be in date, and verify the message (SignatureVersion 1 or 2).

### SMTP relays

SMTP has no webhook. When the relay refuses a recipient during the send (`550 user unknown`), Workbench records it:
a 5xx refusal counts as a bounce, a 4xx as a failed send. Bounces the relay accepts first and returns later go to the
envelope sender (`Email:From`): make it a monitored mailbox and clear or fix addresses by hand.

### Also

- SendGrid and SES keep their own suppression lists and silently drop mail to listed addresses; with the webhooks on,
  those drops show up in Workbench too ("dropped: Bounced Address"). Keep the SES complaint rate under 0.1% and the
  bounce rate under 5%, or SES pauses sending.
- A bouncing address usually means someone left (deactivate them; SCIM or your IdP should already) or a typo in an
  invite or contact record.
- People choose whether they get notification email right away, in the daily digest, or not at all in their
  notification preferences; that is the "unsubscribe" for app mail.
