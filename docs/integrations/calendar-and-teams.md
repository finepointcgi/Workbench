# Calendar feed and Teams notifications

## Calendar feed (Outlook, Google, Apple)

Every person can switch on a private, read-only calendar link under **Leave › My leave › Add to my calendar**. It shows:

- approved leave (pending and rejected leave is not shown) and company holidays,
- tasks assigned to them that have a due date and aren't done, and milestones they own (assigned to them, or on a project they manage),
- timesheet due dates: the end of the current period (unless it is already submitted) and of the next one.

Entries cover the last 30 days and the next year. They are all-day entries, so they never make someone look busy. Approval reminders are not included.

- **Address:** `{PublicBaseUrl}/calendar/{token}.ics`. The token is 256 random bits. The database stores its SHA-256 (to find the feed) and an encrypted copy (so the owner can copy the link again). The link needs no sign-in, because calendar apps can't sign in; the token is the credential.
- **Rotate or turn off:** *Get a new link* replaces the token and the old one answers 404 at once; *Turn off* deletes it. A deactivated person's feed answers 404.
- **Unknown, revoked and malformed tokens** all get the same plain 404 (the not-found page redirect doesn't apply to `.ics`).
- **Refresh:** the feed asks calendars to refresh hourly; Google ignores that and checks every few hours.
- **Not included yet (follow-up):** two-way sync (writing approved leave back to a calendar) would need Microsoft Graph / Google Calendar OAuth per person. It is deliberately not part of this feature.
- Code: `Application/Calendar/` (`CalendarFeedService`, `CalendarFeedBuilder`, `IcsWriter`), endpoint `Web/Calendar/CalendarFeedEndpoints.cs`, UI `Ui/Calendar/CalendarSubscribeCard.razor`.

## Teams notifications

An administrator (**Manage integrations**) connects one Teams channel under **Admin › Integrations › Teams alerts** by pasting the address of a Workflows "Post to a channel when a webhook request is received" (or an older connector address). Each person then chooses, on **Notifications › Also post to Teams**, which groups of notification also go there (all off by default):

| Group | Kinds (by wording) |
| --- | --- |
| Waiting for my approval | `approval.*`, `*approval`, `*.requested`, `*.submitted` |
| Decisions on my requests | `*approved`, `*rejected`, `*returned`, `*decided`, `*cancelled` |
| Assigned to me and @mentions | `mention`, `*assign*`, `*.task` |
| Timesheet reminders | `timesheet.missing`, `timesheet.reopened` |
| Everything else | the rest |

The post is an adaptive card with the title, who it is for, the details and an **Open in Workbench** button. Approval-waiting posts link to the document page itself (where the approve / return / reject bar is) when the approval can be identified, otherwise to the inbox. Everyone in the channel sees the post, which says so next to the switches.

- **Secrets:** the address is encrypted at rest (Data Protection, its own purpose string), shown back only as its host, and never logged: logs and errors name the host only.
- **Allowed hosts:** `*.webhook.office.com`, `*.logic.azure.com`, `*.powerplatform.com`, `outlook.office.com`/`outlook.office365.com` and other `*.office.com` / `*.office365.com` / `*.microsoft.com` hosts. Anything else is refused when saving and again when posting. The usual SSRF guard applies on top (https, public addresses only, checked again when connecting).
- **Fails soft:** `Notifier` hands the saved notifications to `ChatNotificationSender`, which never throws and posts in the background, so a slow or down Teams never delays or fails the action. The channel records the last error, shown on the admin tab.
- **Rate limit:** at most 20 posts per minute per channel (per tenant); more are skipped (they are still in the bell and email).
- **Not retried:** unlike the event connector under Connectors, notification posts are fire-and-forget. A missed post is still in the bell.
- **Adding Slack:** implement `IChatChannelFormat` (kind, allowed hosts, card format) and register it in `IntegrationRegistration`. The `ChatChannel` and `ChatNotificationChoice` tables are keyed by kind already; add a connect screen for it.
- Code: `Application/Notifications/ChatNotifications.cs`, `TeamsNotificationService.cs`, `Approvals/ApprovalChatLinks.cs`; UI `Ui/Integrations/TeamsTab.razor`, `Ui/Notifications/TeamsChoicesCard.razor`.
