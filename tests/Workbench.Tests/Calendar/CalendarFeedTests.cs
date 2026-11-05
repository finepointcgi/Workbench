using System.Net;
using Workbench.ApiClient;
using Workbench.Application.Calendar;
using Workbench.Contracts.Leave;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Calendar;

public sealed class CalendarFeedTests
{
    [Fact]
    public void The_feed_text_is_escaped_and_long_lines_fold_without_splitting_characters()
    {
        var ics = IcsWriter.Write("Workbench", [new CalendarEntry("a@workbench", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3),
            "Out: PTO, half; \"day\"" + new string('é', 60), "line one\nline two", "https://example.com/leave")], new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("DTSTART;VALUE=DATE:20261102\r\n", ics);
        Assert.Contains("DTEND;VALUE=DATE:20261104\r\n", ics); // all-day ends are exclusive
        Assert.Contains("DESCRIPTION:line one\\nline two\r\n", ics);
        Assert.Contains("Out: PTO\\, half\\; ", ics.Replace("\r\n ", ""));
        foreach (var line in ics.Split("\r\n"))
        {
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line);
        }

        Assert.DoesNotContain("�", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public async Task A_person_gets_a_private_feed_with_their_leave_that_can_be_renewed_and_switched_off()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        using var employee = web.CreateClientAs("analyst@example.com");
        using var anonymous = web.CreateClientAs(null);
        var feed = new HttpCalendarFeedService(employee);

        Assert.False((await feed.GetMineAsync()).Enabled);
        var on = await feed.EnableAsync();
        Assert.True(on.Enabled);
        Assert.Matches(@"^calendar/[A-Za-z0-9_-]{40,}\.ics$", on.Url);
        Assert.Equal(on.Url, (await feed.EnableAsync()).Url); // turning it on again keeps the link

        // A day off, asked for and approved.
        var day = DateOnly.FromDateTime(DateTime.Now).AddDays(21);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
        }

        var pto = (await new HttpLeaveBalanceService(employee).GetMyBalancesAsync()).First(b => b.Type == LeaveTypeKind.Pto);
        // Skip holidays too: the request is refused for a day that isn't a working day.
        LeaveRequestDto? asked = null;
        var leaveDay = day;
        for (var tries = 0; asked is null && tries < 14; tries++, day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            try
            {
                asked = await new HttpLeaveRequestService(employee).CreateAsync(new CreateLeaveRequest(pto.PolicyId, day, day, null, "Long weekend"));
                leaveDay = day;
            }
            catch (ArgumentException)
            {
            }
        }

        Assert.NotNull(asked);
        var before = await anonymous.GetAsync("/" + on.Url);
        Assert.DoesNotContain("SUMMARY:Out: ", await before.Content.ReadAsStringAsync()); // pending leave isn't on the calendar
        await new HttpLeaveRequestService(finance).ApproveAsync(asked.Id, new DecideLeaveRequest("Enjoy"));

        // No sign-in needed: the link is the credential.
        var response = await anonymous.GetAsync("/" + on.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/calendar", response.Content.Headers.ContentType!.ToString());
        var ics = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains($"DTSTART;VALUE=DATE:{leaveDay:yyyyMMdd}", ics);
        Assert.Contains("SUMMARY:Out: ", ics);
        Assert.Contains("SUMMARY:Timesheet due", ics);
        Assert.DoesNotContain("alex", ics, StringComparison.OrdinalIgnoreCase); // only the person's own entries; no addresses in it

        // Someone else's feed doesn't carry this leave.
        var other = await new HttpCalendarFeedService(finance).EnableAsync();
        Assert.DoesNotContain("SUMMARY:Out: ", await (await anonymous.GetAsync("/" + other.Url)).Content.ReadAsStringAsync());

        // A new link: the old one stops at once.
        var rotated = await feed.RotateAsync();
        Assert.NotEqual(on.Url, rotated.Url);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/" + on.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/" + rotated.Url)).StatusCode);

        // Off: the link is dead, and a guess is a plain 404.
        await feed.RevokeAsync();
        Assert.False((await feed.GetMineAsync()).Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/" + rotated.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/calendar/not-a-real-token-not-a-real-token.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/calendar/x.ics")).StatusCode);
    }

    [Fact]
    public async Task The_feed_link_is_for_signed_in_people_only()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var anonymous = web.CreateClientAs(null);
        var response = await anonymous.GetAsync("/api/v1/me/calendar-feed");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect, response.StatusCode.ToString());
    }
}
