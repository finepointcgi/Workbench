using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;

namespace Workbench.Application.Planning;

internal static class PlanningMapping
{
    public static RecurrenceKind ToContract(this RecurrenceFrequency v) => (RecurrenceKind)(int)v;
    public static RecurrenceFrequency ToDomain(this RecurrenceKind v) => (RecurrenceFrequency)(int)v;
    public static RecurringDay ToContract(this RecurringPostingDay v) => (RecurringDay)(int)v;
    public static RecurringPostingDay ToDomain(this RecurringDay v) => (RecurringPostingDay)(int)v;
    public static RecurringAmounts ToContract(this RecurringAmountMode v) => (RecurringAmounts)(int)v;
    public static RecurringAmountMode ToDomain(this RecurringAmounts v) => (RecurringAmountMode)(int)v;
    public static LineSide ToContract(this EntrySide v) => (LineSide)(int)v;
    public static EntrySide ToDomain(this LineSide v) => (EntrySide)(int)v;
    public static RecurringRunState ToContract(this RecurringRunStatus v) => (RecurringRunState)(int)v;
    public static BudgetVersionKind ToContract(this GlBudgetKind v) => (BudgetVersionKind)(int)v;
    public static GlBudgetKind ToDomain(this BudgetVersionKind v) => (GlBudgetKind)(int)v;
    public static BudgetVersionState ToContract(this GlBudgetStatus v) => (BudgetVersionState)(int)v;
    public static PlanningCycleState ToContract(this PlanningCycleStatus v) => (PlanningCycleState)(int)v;
    public static SubmissionState ToContract(this BudgetSubmissionStatus v) => (SubmissionState)(int)v;
    public static Contracts.Accounting.AccountKind ToContract(this AccountType v) => (Contracts.Accounting.AccountKind)(int)v;

    /// <summary>Debit-positive GL net in the account's natural sign (revenue and liabilities credit-positive).</summary>
    public static decimal Natural(this Account account, decimal debitPositive) =>
        account.NormalBalance == NormalBalance.Credit ? -debitPositive : debitPositive;

    public static string Clean(string? value, int max, string what, bool required = false)
    {
        var v = value?.Trim() ?? "";
        if (required && v.Length == 0)
        {
            throw new ArgumentException($"Enter a {what.ToLowerInvariant()}.");
        }

        return v.Length > max ? throw new ArgumentException($"{what} can be at most {max} characters.") : v;
    }

    public static void RequireCents(decimal amount, string what)
    {
        if (decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException($"{what}: amounts are in whole cents.");
        }

        if (Math.Abs(amount) > 999_999_999_999m)
        {
            throw new ArgumentException($"{what}: the amount is too large.");
        }
    }
}
