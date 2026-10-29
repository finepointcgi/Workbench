# Connectors

Prebuilt connections under **Admin › Integrations › Connectors**. Each is a configured instance of a connector kind
(`Application/Integrations/Connectors/IConnector`) with its own settings and a run log. Secret settings (incoming webhook URLs,
SFTP passwords and keys) are encrypted at rest and never shown again; leave them blank when editing to keep them.

| Kind | Does | Runs |
| --- | --- | --- |
| Microsoft Teams | Posts the events you choose as an adaptive card to a channel. | When the event happens (through the webhook queue: retries and backoff, and each post in the run log). "Post a test message" checks the setup. |
| Slack | Posts the events you choose to a channel. | Same as Teams. |
| General ledger export (QuickBooks) | A company's posted journal lines for a date range as a QuickBooks Desktop `.iif` (GENERAL JOURNAL transactions, debits positive, credits negative, the project code as the class) or a CSV. Optional account mapping `1010=Checking; 4000=Services revenue`. | Daily, weekly, monthly, or when you choose Run now. |
| Report export (CSV or SFTP) | Saved reports as CSV or Excel files. | Same as the GL export. |

## Teams and Slack

- **Teams:** in the channel, add the Workflows app's "Post to a channel when a webhook request is received" and paste its URL
  (legacy Office 365 connector URLs work too).
- **Slack:** create a Slack app with Incoming Webhooks turned on, add a webhook for the channel and paste its URL.
- Messages show only what the webhook payload has (title, a few facts, a link to the record). They link to
  `Notifications:PublicBaseUrl` when it's set.
- The URLs are checked like webhook endpoints: `https://`, public addresses only. The delivery and run logs show the URL's host,
  never its secret path.

(Posting people's own notifications to a channel is separate: see [chat.md](chat.md).)

## File exports

- **Destination:** Workbench always keeps a copy, downloadable from the run log for 30 days (`Integrations:RetentionDays`).
  Choose **An SFTP server** to also upload it: host, port, user name, password or OpenSSH private key, folder, and the server's
  SHA256 host key fingerprint (from `ssh-keyscan host | ssh-keygen -lf -` or your provider). Workbench refuses any server whose key
  doesn't match, and connects only to public addresses.
- **Dates:** the previous day, previous week (Monday to Sunday), previous month or this month so far, counted from the run (UTC).
- **Who it runs as:** scheduled runs act as the administrator who last saved the connector, with their access (report exports
  show only what they may see); they stop with a message if that account is deactivated. Run now runs as you.
- **Failures** are retried with backoff (30 s doubling) before the next scheduled slot, then wait for that slot.
- Downloading a run's file records an `export.report` security event; the report export also records one per report, as the
  reports page does.

## Adding a connector kind

Implement `IConnector` (settings form, validation, `RunAsync`), make outbound calls only through `IOutboundTransport` (HTTP, SSRF
guarded) or `ISftpUploader`, and register it with `services.AddScoped<IConnector, YourConnector>()` in `IntegrationRegistration`.
Tests replace both with fakes, so no test ever calls a third party.
