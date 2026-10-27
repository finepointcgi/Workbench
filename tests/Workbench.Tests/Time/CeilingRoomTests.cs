using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Time;
using Workbench.Domain.Ceilings;
using Workbench.Ui.Time;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Time;

/// <summary>"Fill from my staffing" stops at an hours ceiling instead of filling hours that submit would refuse.</summary>
public sealed class CeilingRoomTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void The_fill_stops_at_the_room_left_earliest_days_first_and_names_the_code_it_cut()
    {
        var day = new DateOnly(2026, 10, 12);
        var stopped = new List<string>();
        var filled = TimeFill.CapAtCeilings(
            [(A, day, 8m), (A, day.AddDays(1), 8m), (A, day.AddDays(2), 8m), (B, day, 8m)],
            [new ChargeCeilingRoomDto(A, 13.75m)], stopped, id => id == A ? "1001.01.02" : "other");

        Assert.Equal([(A, day, 8m), (A, day.AddDays(1), 5.75m), (B, day, 8m)], filled.OrderBy(f => f.WbsElementId == B).ThenBy(f => f.Date)); // 13.75 h: 8 + 5.75, then none; B has no ceiling
        Assert.Equal(["1001.01.02"], stopped);
    }

    [Fact]
    public void Nothing_is_cut_without_a_ceiling_or_when_there_is_room()
    {
        var day = new DateOnly(2026, 10, 12);
        var stopped = new List<string>();
        var filled = TimeFill.CapAtCeilings([(A, day, 8m), (B, day, 4m)], [new ChargeCeilingRoomDto(A, 100m)], stopped, _ => "x");
        Assert.Equal(2, filled.Count);
        Assert.Empty(stopped);
        Assert.Empty(TimeFill.CapAtCeilings([(A, day, 8m)], [new ChargeCeilingRoomDto(A, 0m)], stopped, _ => "x")); // no room at all
    }

    [Fact]
    public async Task The_timesheet_says_how_many_hours_are_left_under_an_hours_ceiling_counting_everything_entered()
    {
        await using var host = await CreateAsync();
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<Ceiling>().Add(new Ceiling
            {
                Level = CeilingLevel.Wbs, ProjectId = host.Project1, WbsElementId = host.Design, HoursCeiling = 20m, Enforcement = CeilingEnforcement.Block,
                CreatedBy = Pm, CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await host.AddApprovedTimeAsync(host.DevId, (host.Design, host.Se2, new DateOnly(2026, 10, 13), 8m));

        var sheet = await host.As(Dev).Get<ITimesheetService>().GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 14));

        var room = Assert.Single(sheet.CeilingRoom!, r => r.WbsElementId == host.Design);
        Assert.Equal(12m, room.HoursLeft); // 20 h ceiling, 8 h already on it
        Assert.DoesNotContain(sheet.CeilingRoom!, r => r.WbsElementId == host.Build); // Build has no ceiling
    }
}
