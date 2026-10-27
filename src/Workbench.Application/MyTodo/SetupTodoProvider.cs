using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.MyTodo;

/// <summary>
/// For administrators of a company still being set up: one to-do on Home with how far along it is and the next step, so the
/// getting-started checklist (Setup) isn't something they have to find. It's gone once every step is done.
/// </summary>
public sealed class SetupTodoProvider : IMyTodoProvider
{
    public string Name => "Setup";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (!context.User.Can(Permission.AdminUsers) || context.Service<ISetupAdminService>() is not { } setup)
        {
            return [];
        }

        IReadOnlyList<SetupStep> steps;
        try
        {
            steps = SetupSteps.For(await setup.GetChecklistAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is WorkbenchForbiddenException or UnauthorizedAccessException)
        {
            return [];
        }

        var done = steps.Count(s => s.Done);
        if (steps.FirstOrDefault(s => !s.Done) is not { } next)
        {
            return [];
        }

        return
        [
            new MyTodoItem("setup:next", TodoCategories.Finance, $"Finish setting up Workbench: {done} of {steps.Count} steps done",
                $"Next: {next.Title}. {next.Hint}", TodoUrgency.ThisWeek, null, next.Href, "Continue setup", Priority: 3),
        ];
    }
}
