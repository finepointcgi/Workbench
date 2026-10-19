using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Collaboration;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Travel;

namespace Workbench.Application.Travel;

public static class TravelRegistration
{
    /// <summary>
    /// Travel authorizations (approval handler for <c>travel-authorization</c>), corporate card programs, statement feeds
    /// (CSV, OFX) and card charges, the guard that keeps card lines locked on expense reports, and the home to-dos. Binds
    /// <see cref="TravelOptions"/> from config <c>Travel</c>. After AddWorkbenchOdcs (it decorates IExpenseReportService),
    /// above AddWorkbenchIdentity, whose <c>AddWorkbenchTravelPermissions</c> gates the services.
    /// </summary>
    public static IServiceCollection AddWorkbenchTravel(this IServiceCollection services)
    {
        services.AddOptions<TravelOptions>().BindConfiguration(TravelOptions.Section);
        services.AddSingleton<ICardFeed, OfxCardFeed>();
        services.AddSingleton<ICardFeed, CsvCardFeed>();
        services.AddScoped<ITravelAuthorizationService, TravelAuthorizationService>();
        services.AddScoped<ICardProgramService, CardProgramService>();
        services.AddScoped<ICardChargeService, CardChargeService>();
        services.Decorate<IExpenseReportService, TravelCardExpenseReportGuard>();
        services.AddScoped<IApprovalDocumentHandler, TravelAuthorizationApprovalHandler>();
        services.AddScoped<IMyTodoProvider, CardChargeTodoProvider>();
        services.AddScoped<IMyTodoProvider, TravelTodoProvider>();
        return services;
    }
}

public sealed class TravelAuthorizationApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.TravelAuthorization;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<ITravelAuthorizationService>().ApproveAsync(documentId, new TravelDecisionRequest(comment), ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<ITravelAuthorizationService>().RejectAsync(documentId, new TravelDecisionRequest(reason), ct);
}

/// <summary>Card charges the cardholder hasn't put on a submitted report: overdue ones first, then the rest.</summary>
public sealed class CardChargeTodoProvider : IMyTodoProvider
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public string Name => "Card charges";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.User.EmployeeId is null || context.Service<ICardChargeService>() is not { } cards)
        {
            return [];
        }

        var open = (await cards.ListMyChargesAsync(includeAssigned: true, cancellationToken))
            .Where(c => c.Amount > 0 && (c.State is CardChargeState.Unassigned or CardChargeState.OnDraftReport)).ToList();
        var items = new List<MyTodoItem>();
        var overdue = open.Where(c => c.IsOverdue).ToList();
        if (overdue.Count > 0)
        {
            var oldest = overdue.Max(c => c.AgeDays);
            items.Add(new MyTodoItem("cards:overdue", TodoCategories.Expense,
                $"Submit {MyTodoRules.Plural(overdue.Count, "overdue card charge")}",
                $"{overdue.Sum(c => c.Amount).ToString("C", Us)} not on a submitted expense report; the oldest is {oldest} days old.",
                TodoUrgency.Overdue, null, "expenses?tab=cards", "Open card charges", Priority: 14));
        }

        var waiting = open.Where(c => !c.IsOverdue && c.State == CardChargeState.Unassigned).ToList();
        if (waiting.Count > 0)
        {
            items.Add(new MyTodoItem("cards:unassigned", TodoCategories.Expense,
                $"Add {MyTodoRules.Plural(waiting.Count, "card charge")} to an expense report",
                $"{waiting.Sum(c => c.Amount).ToString("C", Us)} from your corporate card.",
                TodoUrgency.Later, null, "expenses?tab=cards", "Open card charges", Priority: 4));
        }

        return items;
    }
}

/// <summary>Trips sent back for changes, and approved trips that are over with no expense report yet.</summary>
public sealed class TravelTodoProvider : IMyTodoProvider
{
    public string Name => "Travel";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.User.EmployeeId is null || context.Service<ITravelAuthorizationService>() is not { } travel)
        {
            return [];
        }

        var items = new List<MyTodoItem>();
        foreach (var trip in await travel.ListMineAsync(cancellationToken))
        {
            var link = $"travel/{trip.Id}";
            if (trip.Status == TravelAuthorizationState.Rejected)
            {
                items.Add(new MyTodoItem($"travel:rejected:{trip.Id}", TodoCategories.Expense, $"Fix and resubmit your trip \"{trip.Title}\"",
                    $"Sent back{(string.IsNullOrWhiteSpace(trip.RejectionReason) ? "." : $": \"{trip.RejectionReason.Trim()}\"")}",
                    TodoUrgency.Today, context.Today, link, "Open trip", Priority: 13));
            }
            else if (trip.Status == TravelAuthorizationState.Approved && trip.ExpenseReportId is null && trip.TripEnd < context.Today)
            {
                items.Add(new MyTodoItem($"travel:report:{trip.Id}", TodoCategories.Expense, $"Start the expense report for \"{trip.Title}\"",
                    $"{MyTodoRules.Range(trip.TripStart, trip.TripEnd)} · {trip.Destinations}. The trip is over; claim it so you're reimbursed.",
                    TodoUrgency.ThisWeek, null, link, "Open trip", Priority: 6));
            }
        }

        return items;
    }
}
