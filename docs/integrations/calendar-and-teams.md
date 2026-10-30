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

Teams (and Slack) chat notifications now live in [chat.md](chat.md), which covers both products, setup steps, security and limits.
