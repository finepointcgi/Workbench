using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Data;
using Workbench.LicenseServer.Licensing;
using Workbench.LicenseServer.Mail;

namespace Workbench.LicenseServer.Stripe;

/// <summary>
/// Turns Stripe events into entitlement changes: checkout.session.completed, invoice.paid, invoice.payment_failed,
/// customer.subscription.updated and customer.subscription.deleted. Handled events are remembered by id, so a redelivery does nothing.
/// When the entitlement changes (new subscription, other modules or seats, a renewal) a new key is issued and emailed.
/// </summary>
public sealed class StripeWebhookHandler(LicenseDb db, LicenseService licenses, IStripeApi stripe, IMailer mailer,
    IOptions<StripeOptions> options, IOptions<PortalOptions> portal, TimeProvider clock, ILogger<StripeWebhookHandler> log)
{
    public async Task<string> HandleAsync(string payload, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(payload);
        var evt = doc.RootElement;
        var id = StripeSubscription.Str(evt, "id") ?? throw new ArgumentException("The event has no id.");
        var type = StripeSubscription.Str(evt, "type") ?? "";
        if (await db.StripeEvents.AnyAsync(e => e.Id == id, ct)) return "duplicate";

        var obj = evt.GetProperty("data").GetProperty("object");
        var outcome = type switch
        {
            "checkout.session.completed" => await CheckoutCompletedAsync(obj, ct),
            "invoice.paid" => await InvoiceAsync(obj, paid: true, ct),
            "invoice.payment_failed" => await InvoiceAsync(obj, paid: false, ct),
            "customer.subscription.updated" or "customer.subscription.created" => await SyncAsync(StripeSubscription.FromJson(obj), null, ct),
            "customer.subscription.deleted" => await SubscriptionDeletedAsync(StripeSubscription.FromJson(obj), ct),
            _ => "ignored",
        };
        db.StripeEvents.Add(new StripeEvent { Id = id, Type = type, Outcome = outcome, ReceivedUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Stripe {Type} {Id}: {Outcome}", type, id, outcome);
        return outcome;
    }

    private async Task<string> CheckoutCompletedAsync(JsonElement session, CancellationToken ct)
    {
        if (StripeSubscription.Str(session, "mode") != "subscription") return "ignored (not a subscription checkout)";
        var subId = StripeSubscription.Str(session, "subscription");
        if (subId is null) return "ignored (no subscription)";
        var sub = await stripe.GetSubscriptionAsync(subId, ct);
        if (sub is null) return "ignored (subscription not found at Stripe)";
        var email = session.TryGetProperty("customer_details", out var cd) ? StripeSubscription.Str(cd, "email") : null;
        email ??= StripeSubscription.Str(session, "customer_email");
        var name = session.TryGetProperty("customer_details", out var cd2) ? StripeSubscription.Str(cd2, "name") : null;
        return await SyncAsync(sub, (email, name), ct);
    }

    private async Task<string> InvoiceAsync(JsonElement invoice, bool paid, CancellationToken ct)
    {
        var subId = StripeSubscription.Str(invoice, "subscription")
            ?? (invoice.TryGetProperty("parent", out var p) && p.TryGetProperty("subscription_details", out var sd) ? StripeSubscription.Str(sd, "subscription") : null);
        if (subId is null) return "ignored (not a subscription invoice)";
        if (paid)
        {
            var sub = await stripe.GetSubscriptionAsync(subId, ct);
            return sub is null ? "ignored (subscription not found at Stripe)" : await SyncAsync(sub, null, ct);
        }

        var local = await db.Subscriptions.FirstOrDefaultAsync(s => s.StripeSubscriptionId == subId, ct);
        if (local is null) return "ignored (unknown subscription)";
        if (local.Status != SubscriptionStatus.Canceled) local.Status = SubscriptionStatus.PastDue;
        local.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return "marked past due (key keeps working until the grace period ends)";
    }

    private async Task<string> SubscriptionDeletedAsync(StripeSubscription s, CancellationToken ct)
    {
        var local = await db.Subscriptions.FirstOrDefaultAsync(x => x.StripeSubscriptionId == s.Id, ct);
        if (local is null) return "ignored (unknown subscription)";
        local.Status = SubscriptionStatus.Canceled;
        local.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return "canceled";
    }

    /// <summary>Creates or updates the local subscription from Stripe's, and issues + emails a key when the entitlement changed.</summary>
    private async Task<string> SyncAsync(StripeSubscription s, (string? Email, string? Name)? who, CancellationToken ct)
    {
        var mapped = Map(s.Items);
        if (mapped is not { } grant) return $"ignored (none of the prices on {s.Id} are in Stripe:Prices)";
        var status = s.Status switch
        {
            "active" => SubscriptionStatus.Active,
            "trialing" => SubscriptionStatus.Trialing,
            "past_due" or "unpaid" => SubscriptionStatus.PastDue,
            "canceled" or "incomplete_expired" => SubscriptionStatus.Canceled,
            _ => (SubscriptionStatus?)null,
        };
        if (status is null) return $"ignored (status {s.Status})";

        var now = clock.GetUtcNow().UtcDateTime;
        var sub = await db.Subscriptions.FirstOrDefaultAsync(x => x.StripeSubscriptionId == s.Id, ct);
        var isNew = sub is null;
        if (sub is null)
        {
            var customer = await FindOrCreateCustomerAsync(s.CustomerId, who, ct);
            if (customer is null) return $"ignored (no email for Stripe customer {s.CustomerId})";
            sub = new Subscription { CustomerId = customer.Id, Source = "Stripe", StripeSubscriptionId = s.Id, StripeCustomerId = s.CustomerId, CreatedUtc = now };
            db.Subscriptions.Add(sub);
        }

        var before = (sub.Modules, sub.Seats, sub.LightSeats, sub.TermEndUtc, sub.Status);
        sub.Modules = Modules.Join(grant.Modules);
        sub.Seats = grant.Seats;
        sub.LightSeats = grant.LightSeats;
        sub.Status = status.Value;
        sub.IsTrial = status == SubscriptionStatus.Trialing;
        sub.CancelAtPeriodEnd = s.CancelAtPeriodEnd;
        sub.TermStartUtc = s.PeriodStart ?? (isNew ? now : sub.TermStartUtc);
        sub.TermEndUtc = s.PeriodEnd ?? (isNew ? now.AddMonths(1) : sub.TermEndUtc);
        sub.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);

        var license = await licenses.EnsureSubscriptionLicenseAsync(sub, ct);
        if (!isNew && before == (sub.Modules, sub.Seats, sub.LightSeats, sub.TermEndUtc, sub.Status)) return "no entitlement change";
        if (sub.Status == SubscriptionStatus.Canceled) return "canceled";

        var key = await licenses.IssueAsync(license, isNew ? "Stripe: new subscription" : "Stripe: subscription changed", "stripe", ct);
        var customerRow = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == sub.CustomerId, ct);
        var portalUrl = (portal.Value.PublicBaseUrl ?? "").TrimEnd('/') + "/portal";
        await mailer.SendAsync(customerRow.Email, "Your Workbench license key",
            $"Your Workbench license is ready: modules {key.Modules.Replace(",", ", ")}, {key.Seats} seats, {key.LightSeats} light seats, valid to {key.ExpiresOn:yyyy-MM-dd}.\n\n"
            + $"{key.Key}\n\nYou can always download the current key, or change seats, at {portalUrl}.", ct);
        return isNew ? "subscription created, key issued" : "subscription updated, key issued";
    }

    private async Task<Customer?> FindOrCreateCustomerAsync(string stripeCustomerId, (string? Email, string? Name)? who, CancellationToken ct)
    {
        var c = await db.Customers.FirstOrDefaultAsync(x => x.StripeCustomerId == stripeCustomerId, ct);
        if (c != null) return c;
        var email = who?.Email ?? await stripe.GetCustomerEmailAsync(stripeCustomerId, ct);
        if (string.IsNullOrWhiteSpace(email)) return null;
        email = email.Trim().ToLowerInvariant();
        // A customer an admin created by hand (same email) is linked rather than duplicated.
        c = (await db.Customers.Where(x => x.Email == email).ToListAsync(ct)).FirstOrDefault(x => x.StripeCustomerId == null);
        if (c is null)
        {
            c = new Customer { Name = string.IsNullOrWhiteSpace(who?.Name) ? email : who!.Value.Name!.Trim(), Email = email, CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Customers.Add(c);
        }
        c.StripeCustomerId = stripeCustomerId;
        await db.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>Adds up what the subscription's prices grant; null when none of them are configured.</summary>
    private (string[] Modules, int Seats, int LightSeats)? Map(IReadOnlyList<StripeItem> items)
    {
        var modules = new List<string>();
        int seats = 0, light = 0;
        var any = false;
        foreach (var it in items)
        {
            if (!options.Value.Prices.TryGetValue(it.PriceId, out var m)) { log.LogWarning("Stripe price {Price} is not in Stripe:Prices", it.PriceId); continue; }
            any = true;
            switch (m.Role.ToLowerInvariant())
            {
                case "seats": seats += it.Quantity * m.SeatsPerUnit; break;
                case "lightseats": light += it.Quantity * m.SeatsPerUnit; break;
                default: modules.AddRange(m.Modules); seats += m.IncludedSeats; light += m.IncludedLightSeats; break;
            }
        }
        return any ? (Modules.Normalize(modules), seats, light) : null;
    }
}
