using Workbench.Contracts.Accounting;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Planning;

public sealed class RecurringJournalTests
{
    private static RecurringJournalTemplate Template(RecurrenceFrequency frequency, DateOnly start, RecurringPostingDay day, DateOnly? end = null, int? occurrences = null) =>
        new() { Name = "T", Description = "T", CreatedBy = "x", Frequency = frequency, StartDate = start, PostingDay = day, EndDate = end, Occurrences = occurrences };

    [Fact]
    public void Schedules_follow_the_frequency_the_posting_day_and_the_limits()
    {
        var monthly = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 10, 15), RecurringPostingDay.LastDay, occurrences: 3);
        Assert.Equal([new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 31)], monthly.Schedule(DateOnly.MaxValue).Select(o => o.Date));

        // Starting after the 1st, the first "first day" occurrence is the next month.
        var rent = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 10, 15), RecurringPostingDay.FirstDay);
        Assert.Equal([(1, new DateOnly(2026, 11, 1)), (2, new DateOnly(2026, 12, 1))], rent.Schedule(new DateOnly(2026, 12, 31)));

        var quarterly = Template(RecurrenceFrequency.Quarterly, new DateOnly(2026, 10, 1), RecurringPostingDay.FirstDay, end: new DateOnly(2027, 6, 30));
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2027, 1, 1), new DateOnly(2027, 4, 1)], quarterly.Schedule(DateOnly.MaxValue).Select(o => o.Date));

        var annual = Template(RecurrenceFrequency.Annually, new DateOnly(2026, 9, 1), RecurringPostingDay.LastDay, occurrences: 2);
        Assert.Equal([new DateOnly(2026, 9, 30), new DateOnly(2027, 9, 30)], annual.Schedule(DateOnly.MaxValue).Select(o => o.Date));
    }

    [Fact]
    public void Percentage_lines_split_the_base_amount_to_the_cent()
    {
        var t = Template(RecurrenceFrequency.Monthly, new DateOnly(2026, 10, 1), RecurringPostingDay.LastDay);
        t.AmountMode = RecurringAmountMode.Percentage;
        t.BaseAmount = 1000m;
        t.Lines.AddRange([
            new RecurringJournalTemplateLine { LineNumber = 1, Side = EntrySide.Debit, Percent = 33.3333m },
            new RecurringJournalTemplateLine { LineNumber = 2, Side = EntrySide.Debit, Percent = 33.3333m },
            new RecurringJournalTemplateLine { LineNumber = 3, Side = EntrySide.Debit, Percent = 33.3334m },
            new RecurringJournalTemplateLine { LineNumber = 4, Side = EntrySide.Credit, Percent = 100m },
        ]);
        var amounts = t.Amounts();
        Assert.Equal([333.33m, 333.33m, 333.34m, 0m], amounts.Select(a => a.Debit));
        Assert.Equal(1000m, amounts.Sum(a => a.Debit));
        Assert.Equal(1000m, amounts.Sum(a => a.Credit));
        Assert.Equal([33.34m, 33.33m, 33.33m], Allocation.Split(100m, [1, 1, 1]));
    }

    private static SaveRecurringTemplateRequest Accrual(PlanningKit kit, bool post, bool reverse, decimal amount = 500m) =>
        new("Accrual", "Accrued depreciation", RecurrenceKind.Monthly, new DateOnly(2026, 10, 1),
            [new RecurringLineInput(kit["6140"], LineSide.Debit, amount, OrgUnitId: kit.Ops), new RecurringLineInput(kit["2200"], LineSide.Credit, amount)],
            RecurringDay.LastDay, PostAutomatically: post, AutoReverse: reverse);

    [Fact]
    public async Task Posted_entries_reverse_on_the_first_day_of_the_next_period_and_generating_again_adds_nothing()
    {
        var kit = await PlanningKit.CreateAsync();
        var template = await kit.Recurring.CreateTemplateAsync(Accrual(kit, post: true, reverse: true));

        // Today is Nov 15: only October's month-end entry is due.
        var result = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal((1, 1, 0), (result.Posted, result.Reversed, result.Skipped));
        var run = Assert.Single(result.Runs);
        Assert.Equal(new DateOnly(2026, 10, 31), run.ScheduledDate);
        Assert.NotNull(run.ReversalEntryId);

        var entries = await kit.Gl.EntriesAsync();
        var entry = Assert.Single(entries, e => e.Source == JournalSource.Recurring);
        Assert.Equal((template.Id, 1, JournalStatus.Reversed), (entry.SourceId!.Value, entry.SourceRevision, entry.Status));
        var reversal = Assert.Single(entries, e => e.ReversalOfId == entry.Id);
        Assert.Equal(new DateOnly(2026, 11, 1), reversal.Date);
        Assert.Equal(0m, await kit.Gl.BalanceAsync("2200"));

        var again = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal((0, 0, 1), (again.Posted + again.Drafted, again.Reversed, again.AlreadyDone));
        Assert.Equal(2, (await kit.Gl.EntriesAsync()).Count);

        // A month later November's comes due.
        kit.Clock.Now = new DateTimeOffset(2026, 12, 2, 12, 0, 0, TimeSpan.Zero);
        var december = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal(1, december.Posted);
        Assert.Equal(2, (await kit.Recurring.GetTemplateAsync(template.Id)).Generated);
    }

    [Fact]
    public async Task Drafts_wait_for_review_and_are_reversed_once_posted()
    {
        var kit = await PlanningKit.CreateAsync();
        await kit.Recurring.CreateTemplateAsync(Accrual(kit, post: false, reverse: true));
        var first = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        var run = Assert.Single(first.Runs);
        Assert.Equal((1, RecurringRunState.Draft), (first.Drafted, run.Status));
        var draft = await kit.Gl.Journal.GetAsync(run.JournalEntryId!.Value);
        Assert.Equal(JournalEntryState.Draft, draft.Entry.Status);
        Assert.Equal(JournalEntrySource.Recurring, draft.Entry.Source);

        await kit.Gl.Journal.PostAsync(draft.Entry.Id);
        var second = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal((0, 1), (second.Drafted + second.Posted, second.Reversed));
        Assert.Equal(JournalEntryState.Reversed, (await kit.Gl.Journal.GetAsync(draft.Entry.Id)).Entry.Status);
    }

    [Fact]
    public async Task Closed_periods_are_skipped_with_a_message_and_retried_once_open()
    {
        var kit = await PlanningKit.CreateAsync();
        await kit.Recurring.CreateTemplateAsync(Accrual(kit, post: true, reverse: false));
        await kit.SetPeriodStatusAsync("2026-10", PeriodStatus.Closed);

        var skipped = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal((0, 1), (skipped.Posted, skipped.Skipped));
        Assert.Contains(skipped.Messages, m => m.Contains("2026-10 is closed"));
        var history = Assert.Single(await kit.Recurring.ListHistoryAsync());
        Assert.Equal(RecurringRunState.Skipped, history.Status);
        Assert.Empty(await kit.Gl.EntriesAsync());

        await kit.SetPeriodStatusAsync("2026-10", PeriodStatus.Open);
        var retried = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal(1, retried.Posted);
        Assert.Equal(RecurringRunState.Posted, Assert.Single(await kit.Recurring.ListHistoryAsync()).Status);
    }

    [Fact]
    public async Task Auto_reversal_waits_for_the_next_period_to_open()
    {
        var kit = await PlanningKit.CreateAsync();
        await kit.Recurring.CreateTemplateAsync(Accrual(kit, post: true, reverse: true));
        await kit.SetPeriodStatusAsync("2026-11", PeriodStatus.Closed);
        var first = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest());
        Assert.Equal((1, 0), (first.Posted, first.Reversed));
        Assert.Contains(first.Messages, m => m.Contains("2026-11 is open"));

        await kit.SetPeriodStatusAsync("2026-11", PeriodStatus.Open);
        Assert.Equal(1, (await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest())).Reversed);
    }

    [Fact]
    public async Task Generating_for_a_period_creates_only_that_periods_occurrences()
    {
        var kit = await PlanningKit.CreateAsync();
        await kit.Recurring.CreateTemplateAsync(Accrual(kit, post: true, reverse: false));
        var december = await kit.Recurring.GenerateDueAsync(new GenerateRecurringRequest(kit.Periods["2026-12"].Id));
        Assert.Equal(new DateOnly(2026, 12, 31), Assert.Single(december.Runs).ScheduledDate);

        var schedule = await kit.Recurring.ListTemplatesAsync();
        Assert.Equal(new DateOnly(2026, 10, 31), Assert.Single(schedule).NextDate);
    }

    [Fact]
    public async Task Templates_must_balance_and_percentages_must_add_up()
    {
        var kit = await PlanningKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Recurring.CreateTemplateAsync(Accrual(kit, true, false) with
        {
            Lines = [new RecurringLineInput(kit["6140"], LineSide.Debit, 500m), new RecurringLineInput(kit["2200"], LineSide.Credit, 400m)],
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Recurring.CreateTemplateAsync(Accrual(kit, true, false) with
        {
            AmountMode = RecurringAmounts.Percentage, BaseAmount = 900m,
            Lines = [new RecurringLineInput(kit["6140"], LineSide.Debit, Percent: 60), new RecurringLineInput(kit["2200"], LineSide.Credit, Percent: 100)],
        }));

        // Summary accounts take no postings.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Recurring.CreateTemplateAsync(Accrual(kit, true, false) with
        {
            Lines = [new RecurringLineInput(kit["6100"], LineSide.Debit, 500m), new RecurringLineInput(kit["2200"], LineSide.Credit, 500m)],
        }));
    }

    [Fact]
    public async Task The_background_job_runs_only_when_turned_on()
    {
        var kit = await PlanningKit.CreateAsync();
        await kit.Recurring.CreateTemplateAsync(Accrual(kit, post: true, reverse: false));
        Assert.Null(await kit.Recurring.RunScheduledAsync(CancellationToken.None));
        Assert.Empty(await kit.Gl.EntriesAsync());

        await kit.Recurring.SaveSettingsAsync(new SaveRecurringSettingsRequest(true));
        Assert.Equal(1, (await kit.Recurring.RunScheduledAsync(CancellationToken.None))!.Posted);
        var settings = await kit.Recurring.GetSettingsAsync();
        Assert.True(settings.AutoGenerate);
        Assert.StartsWith("1 posted", settings.LastAutoRunSummary);
    }
}
