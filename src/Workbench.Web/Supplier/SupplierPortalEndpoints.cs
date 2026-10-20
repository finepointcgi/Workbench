using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Web.Identity;
using Workbench.Web.Supplier.Pages;

namespace Workbench.Web.Supplier;

/// <summary>
/// The supplier portal's pages under <c>/supplier</c>: plain server-rendered HTML forms (no Blazor circuit), each POST
/// checked for its antiforgery token, then redirected (post/redirect/get) or re-rendered with the problem. Everything
/// goes through <see cref="ISupplierPortalService"/>, whose vendor-scoped decorator decides what the contact may see.
/// </summary>
public static class SupplierPortalEndpoints
{
    private static readonly Dictionary<string, string> Done = new(StringComparer.Ordinal)
    {
        ["quote"] = "Thanks: your quote was sent.",
        ["declined"] = "You declined this request for quote.",
        ["ack"] = "Thanks: your acknowledgement was sent.",
        ["shipped"] = "Thanks: your ship notice was sent.",
        ["invoice"] = "Thanks: your invoice was sent to accounts payable.",
        ["password"] = "Your password is saved.",
    };

    public static IEndpointRouteBuilder MapWorkbenchSupplierPortal(this IEndpointRouteBuilder app)
    {
        var anon = app.MapGroup(SupplierPortalPaths.Base).AllowAnonymous().ExcludeFromDescription().AddEndpointFilter(BadToken);
        var portal = app.MapGroup(SupplierPortalPaths.Base).RequireAuthorization(SupplierPortalAuthentication.Policy).ExcludeFromDescription().AddEndpointFilter(BadToken);

        // ---- Sign-in (anonymous) ----

        anon.MapGet("/signin", (HttpContext http, IAntiforgery af, string? returnUrl) =>
            Page<SignInPage>(new { View = View(http, af, null, "signin"), ReturnUrl = SafeReturn(returnUrl) }));

        anon.MapPost("/signin/request", async (HttpContext http, IAntiforgery af, SupplierSignIn signIn) =>
        {
            var form = await FormAsync(http, af);
            await signIn.RequestLinkAsync(form["email"], $"{http.Request.Scheme}://{http.Request.Host}", Ip(http), http.RequestAborted);
            return Page<SignInPage>(new
            {
                View = View(http, af, null, "signin", "If that email belongs to an invited contact, a sign-in link is on its way. It works once, for 15 minutes."),
                ReturnUrl = SafeReturn(form["returnUrl"]),
            });
        });

        // The emailed link opens a page with a button; only the POST uses the token (mail scanners follow GET links).
        anon.MapGet("/signin/link", (HttpContext http, IAntiforgery af, string? token) =>
            Page<SignInPage>(new { View = View(http, af, null, "signin"), LinkToken = token ?? "" }));

        anon.MapPost("/signin/link", async (HttpContext http, IAntiforgery af, SupplierSignIn signIn, TimeProvider clock) =>
        {
            var form = await FormAsync(http, af);
            var result = await signIn.RedeemLinkAsync(form["token"], Ip(http), http.RequestAborted);
            if (result is null)
            {
                return Page<SignInPage>(new { View = View(http, af, null, "signin", error: "That sign-in link has expired or was already used. Ask for a new one below.") }, 400);
            }

            await http.SignInAsync(SupplierPortalAuthentication.Scheme, SupplierPortalAuthentication.Principal(result, clock.GetUtcNow()));
            return Results.Redirect(SupplierPortalPaths.Base);
        });

        anon.MapPost("/signin/password", async (HttpContext http, IAntiforgery af, SupplierSignIn signIn, TimeProvider clock) =>
        {
            var form = await FormAsync(http, af);
            var result = await signIn.PasswordSignInAsync(form["email"], form["password"], Ip(http), http.RequestAborted);
            if (result is null)
            {
                return Page<SignInPage>(new
                {
                    View = View(http, af, null, "signin", error: "That email and password didn't work. After five wrong tries the account waits 15 minutes. You can use an emailed link instead."),
                    ReturnUrl = SafeReturn(form["returnUrl"]),
                    Email = form["email"].ToString(),
                }, 400);
            }

            await http.SignInAsync(SupplierPortalAuthentication.Scheme, SupplierPortalAuthentication.Principal(result, clock.GetUtcNow()));
            return Results.Redirect(SafeReturn(form["returnUrl"]) ?? SupplierPortalPaths.Base);
        });

        // ---- Signed in ----

        portal.MapPost("/signout", async (HttpContext http, IAntiforgery af, SupplierSignIn signIn) =>
        {
            await FormAsync(http, af);
            if (SupplierPortalAuthentication.ContactId(http.User) is { } id)
            {
                await signIn.RecordSignOutAsync(id, Ip(http));
            }

            await http.SignOutAsync(SupplierPortalAuthentication.Scheme);
            return Results.Redirect(SupplierPortalPaths.SignIn);
        });

        portal.MapGet("", (HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            RenderAsync(http, af, portal, "home", async me => Page<HomePage>(new { View = View(http, af, me, "home"), Home = await portal.GetHomeAsync(http.RequestAborted) })));

        portal.MapGet("/rfqs", (HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            RenderAsync(http, af, portal, "rfqs", async me => Page<RfqsPage>(new { View = View(http, af, me, "rfqs"), Rfqs = await portal.ListRfqsAsync(http.RequestAborted) })));

        portal.MapGet("/rfqs/{id:guid}", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal, string? done) =>
            RenderAsync(http, af, portal, "rfqs", async me => Page<RfqPage>(new { View = View(http, af, me, "rfqs", Notice(done)), Rfq = await portal.GetRfqAsync(id, http.RequestAborted) })));

        portal.MapPost("/rfqs/{id:guid}/quote", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            SubmitAsync(http, af, portal, "rfqs",
                async form =>
                {
                    var rfq = await portal.GetRfqAsync(id, http.RequestAborted);
                    await portal.SubmitQuoteAsync(id, QuoteFrom(form, rfq), http.RequestAborted);
                    return $"/supplier/rfqs/{id}?done=quote";
                },
                async (me, error) => Page<RfqPage>(new { View = View(http, af, me, "rfqs", error: error), Rfq = await portal.GetRfqAsync(id, http.RequestAborted) }, 400)));

        portal.MapPost("/rfqs/{id:guid}/decline", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            SubmitAsync(http, af, portal, "rfqs",
                async form =>
                {
                    await portal.DeclineRfqAsync(id, form["reason"], http.RequestAborted);
                    return $"/supplier/rfqs/{id}?done=declined";
                },
                async (me, error) => Page<RfqPage>(new { View = View(http, af, me, "rfqs", error: error), Rfq = await portal.GetRfqAsync(id, http.RequestAborted) }, 400)));

        portal.MapGet("/orders", (HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            RenderAsync(http, af, portal, "orders", async me => Page<OrdersPage>(new { View = View(http, af, me, "orders"), Orders = await portal.ListOrdersAsync(http.RequestAborted) })));

        portal.MapGet("/orders/{id:guid}", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal, string? done) =>
            RenderAsync(http, af, portal, "orders", async me => Page<OrderPage>(new { View = View(http, af, me, "orders", Notice(done)), Order = await portal.GetOrderAsync(id, http.RequestAborted) })));

        portal.MapPost("/orders/{id:guid}/acknowledge", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            OrderFormAsync(id, http, af, portal, "ack", async (form, order) =>
                await portal.AcknowledgeOrderAsync(id, new AcknowledgeOrderRequest(
                    Enum.TryParse<AcknowledgementState>(form["status"], out var s) ? s : throw new ArgumentException("Choose your answer."),
                    order.Lines.Select(l => new AcknowledgeLineRequest(l.Id, DateOrNull(form, $"promised_{l.Id:N}"), DecimalOrNull(form, $"qty_{l.Id:N}"), DecimalOrNull(form, $"price_{l.Id:N}")))
                        .Where(l => l.PromisedDate != null || l.Quantity != null || l.UnitPrice != null).ToList(),
                    form["note"]), http.RequestAborted)));

        portal.MapPost("/orders/{id:guid}/ship", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            OrderFormAsync(id, http, af, portal, "shipped", async (form, order) =>
                await portal.SubmitShipNoticeAsync(id, new ShipNoticeRequest(
                    DateOrNull(form, "shipDate") ?? throw new ArgumentException("Enter the ship date."),
                    order.Lines.Select(l => new ShipNoticeLineRequest(l.Id, DecimalOrNull(form, $"ship_{l.Id:N}") ?? 0)).Where(l => l.Quantity != 0).ToList(),
                    DateOrNull(form, "expectedDelivery"), form["carrier"], form["tracking"], form["reference"]), http.RequestAborted)));

        portal.MapPost("/orders/{id:guid}/invoice", (Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            OrderFormAsync(id, http, af, portal, "invoice", async (form, order) =>
            {
                var file = form.Files.GetFile("file") ?? throw new ArgumentException("Attach the invoice as a PDF file.");
                await using var content = file.OpenReadStream();
                await portal.SubmitInvoiceAsync(new SupplierInvoiceRequest(id, form["invoiceNumber"].ToString(),
                    DateOrNull(form, "invoiceDate") ?? throw new ArgumentException("Enter the invoice date."),
                    DecimalOrNull(form, "total") ?? throw new ArgumentException("Enter the invoice total."),
                    order.Lines.Select(l => new SupplierInvoiceLineRequest(l.Id, DecimalOrNull(form, $"inv_{l.Id:N}") ?? 0)).Where(l => l.Quantity != 0).ToList()),
                    file.FileName, content, http.RequestAborted);
                return order;
            }));

        portal.MapGet("/invoices", (HttpContext http, IAntiforgery af, ISupplierPortalService portal) =>
            RenderAsync(http, af, portal, "invoices", async me => Page<InvoicesPage>(new { View = View(http, af, me, "invoices"), Invoices = await portal.ListInvoicesAsync(http.RequestAborted) })));

        portal.MapGet("/account", (HttpContext http, IAntiforgery af, ISupplierPortalService portal, string? done) =>
            RenderAsync(http, af, portal, "account", me => Task.FromResult(Page<AccountPage>(new { View = View(http, af, me, "account", Notice(done)) }))));

        portal.MapPost("/account/password", (HttpContext http, IAntiforgery af, ISupplierPortalService portal, SupplierSignIn signIn, TimeProvider clock) =>
            SubmitAsync(http, af, portal, "account",
                async form =>
                {
                    var me = await portal.GetSessionAsync(http.RequestAborted);
                    var result = await signIn.SetPasswordAsync(me.ContactId, form["currentPassword"], form["newPassword"], Ip(http), http.RequestAborted);
                    await http.SignInAsync(SupplierPortalAuthentication.Scheme, SupplierPortalAuthentication.Principal(result, clock.GetUtcNow()));
                    return "/supplier/account?done=password";
                },
                (me, error) => Task.FromResult(Page<AccountPage>(new { View = View(http, af, me, "account", error: error) }, 400))));

        return app;
    }

    // ---- Rendering and error handling ----

    /// <summary>A form posted without a valid token (an old tab, or a forged post): say so plainly instead of failing.</summary>
    private static async ValueTask<object?> BadToken(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AntiforgeryValidationException)
        {
            var http = context.HttpContext;
            return Page<MessagePage>(new
            {
                View = View(http, http.RequestServices.GetRequiredService<IAntiforgery>(), null, "signin"),
                Title = "Please try again",
                Text = "That form had expired. Go back, reload the page and send it again.",
            }, 400);
        }
    }

    private static IResult Page<T>(object parameters, int status = 200) where T : Microsoft.AspNetCore.Components.IComponent =>
        new RazorComponentResult<T>(parameters) { StatusCode = status };

    private static PortalView View(HttpContext http, IAntiforgery af, SupplierSessionDto? me, string section, string? notice = null, string? error = null) =>
        new(me, af.GetAndStoreTokens(http), section, notice, error);

    private static string? Notice(string? done) => done is not null && Done.TryGetValue(done, out var text) ? text : null;

    /// <summary>A GET page: not this vendor's (or gone) → a not-found page; access ended → signed out.</summary>
    private static async Task<IResult> RenderAsync(HttpContext http, IAntiforgery af, ISupplierPortalService portal, string section, Func<SupplierSessionDto, Task<IResult>> render)
    {
        SupplierSessionDto me;
        try
        {
            me = await portal.GetSessionAsync(http.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            await http.SignOutAsync(SupplierPortalAuthentication.Scheme);
            return Page<MessagePage>(new { View = View(http, af, null, "signin"), Title = "Access ended", Text = "Your supplier portal access has ended. Contact your buyer if you need it again." }, 403);
        }

        try
        {
            return await render(me);
        }
        catch (WorkbenchNotFoundException)
        {
            return Page<MessagePage>(new { View = View(http, af, me, section), Title = "Not found", Text = "We couldn't find that. It may not be shared with your company." }, 404);
        }
    }

    /// <summary>A form POST: token checked, then the action; a problem re-renders the page with it, success redirects.</summary>
    private static async Task<IResult> SubmitAsync(HttpContext http, IAntiforgery af, ISupplierPortalService portal, string section,
        Func<IFormCollection, Task<string>> act, Func<SupplierSessionDto, string, Task<IResult>> failed)
    {
        var form = await FormAsync(http, af);
        return await RenderAsync(http, af, portal, section, async me =>
        {
            try
            {
                return Results.Redirect(await act(form));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return await failed(me, ex.Message);
            }
        });
    }

    private static Task<IResult> OrderFormAsync(Guid id, HttpContext http, IAntiforgery af, ISupplierPortalService portal, string done,
        Func<IFormCollection, SupplierOrderDto, Task<SupplierOrderDto>> act) =>
        SubmitAsync(http, af, portal, "orders",
            async form =>
            {
                await act(form, await portal.GetOrderAsync(id, http.RequestAborted));
                return $"/supplier/orders/{id}?done={done}";
            },
            async (me, error) => Page<OrderPage>(new { View = View(http, af, me, "orders", error: error), Order = await portal.GetOrderAsync(id, http.RequestAborted) }, 400));

    /// <summary>Reads the form after checking its antiforgery token (a bad token is a 400 from the framework).</summary>
    private static async Task<IFormCollection> FormAsync(HttpContext http, IAntiforgery af)
    {
        await af.ValidateRequestAsync(http);
        return await http.Request.ReadFormAsync(http.RequestAborted);
    }

    // ---- Form parsing ----

    private static SupplierQuoteRequest QuoteFrom(IFormCollection form, SupplierRfqDto rfq)
    {
        var lines = rfq.Lines.Select(l =>
        {
            var id = l.Id.ToString("N");
            var price = DecimalOrNull(form, $"price_{id}");
            var noBid = form[$"nobid_{id}"] == "true" || price is null;
            var breaks = Enumerable.Range(0, 3)
                .Select(i => (Q: DecimalOrNull(form, $"bq_{id}_{i}"), P: DecimalOrNull(form, $"bp_{id}_{i}")))
                .Where(b => b.Q is not null || b.P is not null)
                .Select(b => new PriceBreakDto(b.Q ?? throw new ArgumentException($"Line {l.LineNumber}: a price break needs a quantity."),
                    b.P ?? throw new ArgumentException($"Line {l.LineNumber}: a price break needs a price.")))
                .ToList();
            return new SaveQuoteLineRequest(l.Id, noBid ? 0 : price!.Value, breaks, IntOrNull(form, $"lead_{id}"), noBid, form[$"note_{id}"]);
        }).ToList();
        return new SupplierQuoteRequest(IntOrNull(form, "leadTimeDays") ?? throw new ArgumentException("Enter your lead time in days."),
            lines, DateOrNull(form, "validUntil"), form["reference"], form["terms"], form["notes"]);
    }

    private static decimal? DecimalOrNull(IFormCollection form, string key)
    {
        var text = form[key].ToString().Trim().Replace(",", "", StringComparison.Ordinal).TrimStart('$');
        return text.Length == 0 ? null
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d
            : throw new ArgumentException($"\"{form[key]}\" isn't a number.");
    }

    private static int? IntOrNull(IFormCollection form, string key)
    {
        var text = form[key].ToString().Trim();
        return text.Length == 0 ? null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : throw new ArgumentException($"\"{text}\" isn't a whole number of days.");
    }

    private static DateOnly? DateOrNull(IFormCollection form, string key)
    {
        var text = form[key].ToString().Trim();
        return text.Length == 0 ? null
            : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw new ArgumentException($"\"{text}\" isn't a date.");
    }

    private static string? SafeReturn(string? returnUrl)
    {
        var safe = AccountEndpoints.SafeReturnUrl(returnUrl);
        return safe != "/" && SupplierPortalAuthentication.IsPortalPath(new PathString(safe.Split('?')[0])) ? safe : null;
    }

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
