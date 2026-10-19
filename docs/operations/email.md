# Email

Workbench sends notification emails (approvals, reminders, the daily digest, Limitation of Funds notices) and
scheduled reports. Pick a provider with `Email:Mode`; every mode sends a plain-text and an HTML part, and attachments
for scheduled reports. Deliveries are counted (`workbench.email.sent`) and a recent failure turns the readiness check's
`email` entry *Degraded* ([monitoring.md](monitoring.md)). Addresses are never logged.

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

Workbench doesn't process bounces itself yet: it hands each message to the provider and records only whether the
provider accepted it. A hard bounce happens later and never reaches the app. Handle them at the provider:

- **Suppression**: SendGrid and SES keep suppression lists automatically (hard bounces, spam complaints, unsubscribes);
  they then silently drop mail to those addresses. Review the lists weekly: a suppressed address is usually a person
  who left (deactivate them in Workbench, which SCIM/your IdP should already do) or a typo in an invite.
- **SES**: attach an SNS topic for `Bounce` and `Complaint` events to the configuration set (`Email:Ses:ConfigurationSet`)
  and subscribe your ops mailbox or ticketing system. Keep the complaint rate under 0.1% and bounce rate under 5%, or
  SES pauses sending.
- **SendGrid**: enable the Event Webhook (bounce, dropped, spamreport) to your ticketing system, or check
  *Suppressions* in the dashboard.
- **SMTP relays**: bounces return to the envelope sender (`Email:From`). Make it a monitored mailbox, or set it to a
  mailbox your mail admin reviews.
- People choose whether they get notification email right away, in the daily digest, or not at all in their
  notification preferences; that is the "unsubscribe" for app mail.

A follow-up could receive the SES/SendGrid webhooks and show undeliverable addresses on the user's admin page.
