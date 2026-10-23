using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Automations;
using Workbench.Application.Identity;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Identity;
using WorkspaceRole = Workbench.Contracts.Identity.WorkspaceRole;

namespace Workbench.Application.Automation;

/// <summary>
/// Checks sheet automations' date and recurring-schedule triggers hourly, for each tenant. Each fires once per row and date
/// (or schedule day; see <see cref="SheetAutomationRuntime.RunDateTriggersAsync(IItemService, DateRuleAuthor?, CancellationToken)"/>).
/// <para>
/// Nobody is signed in here, so an automation acts for its author (whoever last published it): each author's run in a scope whose
/// permission checks are that author's (<see cref="RuleAuthorExecutionContext"/>), through the permission-checked
/// <see cref="IItemService"/>. Before a sheet's automations run the author must still be active and an Editor on its workspace;
/// otherwise they are skipped and the reason recorded. Changes are recorded as the automation, naming it and the author.
/// </para>
/// </summary>
public sealed class DateTriggerBackgroundService(IServiceScopeFactory scopes, ITenantJobRunner tenants, TimeProvider clock, ILogger<DateTriggerBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Date trigger run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every tenant's date rules. Returns how many rules fired.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        await tenants.ForEachTenantAsync(async ct =>
        {
            IReadOnlyList<string> authors;
            await using (var scope = scopes.CreateAsyncScope())
            {
                authors = await scope.ServiceProvider.GetRequiredService<SheetAutomationRuntime>().ListDateRuleAuthorsAsync(ct);
            }

            foreach (var author in authors)
            {
                try
                {
                    var fired = await RunForAuthorAsync(author, ct);
                    if (fired > 0)
                    {
                        logger.LogInformation("Date and schedule triggers fired {Count} sheet automations by {Author}", fired, author);
                    }

                    total += fired;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Sheet automations by {Author} failed", author);
                }
            }
        }, cancellationToken);
        return total;
    }

    private async Task<int> RunForAuthorAsync(string author, CancellationToken cancellationToken)
    {
        if (!RuleAuthorExecution.CanActFor(author))
        {
            // Nobody to act for: skip (and record) without any identity.
            await using var anonymous = scopes.CreateAsyncScope();
            return await anonymous.ServiceProvider.GetRequiredService<SheetAutomationRuntime>().RunDateTriggersAsync(
                anonymous.ServiceProvider.GetRequiredService<IItemService>(),
                new DateRuleAuthor(author, (_, _) => Task.FromResult<string?>("The automation has no author to act for.")),
                cancellationToken);
        }

        await using var scope = RuleAuthorExecution.CreateScope(scopes, author);
        var services = scope.ServiceProvider;
        var access = services.GetRequiredService<AccessControl>();
        var dbFactory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        return await services.GetRequiredService<SheetAutomationRuntime>().RunDateTriggersAsync(
            services.GetRequiredService<IItemService>(),
            new DateRuleAuthor(access.Email, (sheetId, ct) => WhyAuthorCantRunAsync(dbFactory, access, sheetId, ct)),
            cancellationToken);
    }

    /// <summary>Null when the author (the scope's <see cref="AccessControl"/> subject) may change the sheet, else why not.</summary>
    internal static async Task<string?> WhyAuthorCantRunAsync(IWorkbenchDbContextFactory dbFactory, AccessControl access, Guid sheetId, CancellationToken cancellationToken)
    {
        var author = access.Email;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            // Users are deactivated, never deleted; their workspace memberships stay, so check explicitly.
            if (await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.Email == author && !u.IsActive, cancellationToken))
            {
                return $"The automation's author, {author}, is deactivated.";
            }
        }

        return await access.GetSheetRoleAsync(sheetId, cancellationToken) is { } role && role >= WorkspaceRole.Editor
            ? null
            : $"The automation's author, {author}, no longer has edit access to this sheet.";
    }
}
