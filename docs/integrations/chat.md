# Chat notifications: Teams and Slack

Workbench can also post people's notifications (approvals waiting, decisions, assignments, timesheet reminders) to a Microsoft Teams or Slack channel. It works the same way for both:

1. An administrator connects **one channel per product** by pasting an incoming-webhook address (**Admin › Integrations › Teams alerts** or **Slack alerts**).
2. Each person chooses which groups of notification also go there, under **Notifications › Also post to …**. Everything is off until they turn it on.
3. Timesheet and approval **reminders** use the same pipeline: switch on *Teams* and/or *Slack* under **Notifications › Reminders › Send reminders by**.

If only one product is connected, people see only that one. If neither is connected, the card isn't shown. If both are, each group has a Teams switch and a Slack switch, so a person can pick either or both.

This is separate from the event connectors under **Admin › Integrations › Connectors**, which post company events (invoice paid, period closed) and retry failures.

## Set up Teams (about 2 minutes)

1. In Teams, open the channel, choose **⋯ › Workflows**.
2. Add **Post to a channel when a webhook request is received** and finish the wizard.
3. Copy the web address it shows (it contains `logic.azure.com` or `powerplatform.com`; an older connector address with `webhook.office.com` works too).
4. In Workbench: **Admin › Integrations › Teams alerts**, paste the address, **Connect**, then **Post a test message**.

## Set up Slack (about 3 minutes)

1. Go to <https://api.slack.com/apps> and **Create New App › From scratch**. Name it "Workbench" and pick your workspace.
2. Open **Incoming Webhooks**, turn it **On**, choose **Add New Webhook to Workspace**, pick the channel and **Allow**.
3. Copy the webhook address (it starts with `https://hooks.slack.com/services/`).
4. In Workbench: **Admin › Integrations › Slack alerts**, paste the address, **Connect**, then **Post a test message**.

Each webhook posts to the one channel you picked. To use another channel, add another webhook in Slack and paste it over the old one (**Replace the channel address**).

## What gets posted

| Group | Kinds (by wording) |
| --- | --- |
| Waiting for my approval | `approval.*`, `*approval`, `*.requested`, `*.submitted` |
| Decisions on my requests | `*approved`, `*rejected`, `*returned`, `*decided`, `*cancelled` |
| Assigned to me and @mentions | `mention`, `*assign*`, `*.task` |
| Timesheet reminders | `timesheet.missing`, `timesheet.reopened`, `reminder.timesheet*` |
| Everything else | the rest |

Both products show the same content: the title, who it is for, the details and an **Open in Workbench** button. Approval-waiting posts link to the document page itself (where the approve / return / reject bar is) when the approval can be identified, otherwise to the inbox. Everyone in the channel sees the post, which the settings card says.

- Teams gets an adaptive card. Slack gets Block Kit: a `section` (bold title, then *Label: value* lines) and an `actions` block with the button; the plain `text` fallback carries the title for notifications and previews.
- **Slack text is escaped.** `&`, `<` and `>` in titles and details become `&amp;`, `&lt;`, `&gt;`, so a title can't make a link or a `<!channel>` / `<@user>` mention. Plain `@channel` text does not notify anyone in an incoming webhook.

## Security and behavior

- **Secrets:** the address is encrypted at rest (Data Protection, its own purpose string), shown back only as its host, and never logged: logs and errors name the host only.
- **Allowed hosts:** Slack accepts `hooks.slack.com` only. Teams accepts `*.webhook.office.com`, `*.logic.azure.com`, `*.powerplatform.com`, `outlook.office.com`/`outlook.office365.com` and other `*.office.com` / `*.office365.com` / `*.microsoft.com` hosts. Anything else is refused when saving and again when posting. The usual SSRF guard applies on top (https, public addresses only, checked again when connecting).
- **Fails soft:** `Notifier` hands the saved notifications to `ChatNotificationSender`, which never throws and posts in the background, so a slow or down Teams or Slack never delays or fails the action. Each channel records its last error and last-sent time, shown on its admin tab, and the **Post a test message** button checks the connection.
- **Rate limit:** at most 20 posts per minute per channel (per tenant and product); more are skipped (they are still in the bell and email).
- **Not retried:** notification posts are fire-and-forget. A missed post is still in the bell.
- **Permissions:** connecting, testing and disconnecting need *Manage integrations*. Anyone can choose their own groups.

## How it is built

- `IChatChannelFormat` (kind, allowed host, message format) has two implementations, `TeamsChannelFormat` and `SlackChannelFormat`, registered in `IntegrationRegistration`. Another product is one more implementation plus a connect tab.
- `ChatChannel` (one row per product) and `ChatNotificationChoice` (per person, product and group) are keyed by kind; the reminder preference has `ViaTeams` and `ViaSlack`.
- Code: `Application/Notifications/ChatNotifications.cs` (sender, formats, rate limit), `TeamsNotificationService.cs` (`ChatNotificationService` base plus Teams and Slack subclasses), `Integrations/Connectors/ChatConnectors.cs` (`SlackConnector.Message` builds the Block Kit JSON, shared with the event connector), `Approvals/ApprovalChatLinks.cs`. API: `/api/v1/integrations/{teams|slack}` (admin) and `/api/v1/me/{teams|slack}` (person). UI: `Ui/Integrations/ChatChannelTab.razor` (`TeamsTab`, `SlackTab`), `Ui/Notifications/ChatChoicesCard.razor`, `ReminderSettingsCard.razor`.

## Next step (not built): a Slack/Teams app

Posting is one-way through webhooks. Approving from inside chat, slash commands (`/workbench …`), or a bot that replies in threads need a hosted app registration the organization must create (a Slack app with OAuth and a signed request URL, and an Azure bot registration for Teams), plus a way to map a chat user to a Workbench user. That is deliberately not part of this feature.
