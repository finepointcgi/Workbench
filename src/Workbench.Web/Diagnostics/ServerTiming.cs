using System.Diagnostics;
using System.Globalization;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Web.Diagnostics;

/// <summary>
/// Opt-in request timing for load tests and profiling (<c>Diagnostics:ServerTiming</c>, off by default): each response
/// gets a <c>Server-Timing</c> header with the database time and command count measured so far (<c>db;dur=12.3;desc="7"</c>),
/// and each request logs its total and database time under <c>Workbench.Diagnostics.ServerTiming</c> once it ends.
/// </summary>
public static class ServerTiming
{
    public const string HeaderName = "Server-Timing";

    public static WebApplication UseWorkbenchServerTiming(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Diagnostics:ServerTiming", false))
        {
            return app;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Workbench.Diagnostics.ServerTiming");
        app.Use(async (context, next) =>
        {
            var timing = DbTiming.Begin();
            var started = Stopwatch.GetTimestamp();
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Append(HeaderName, Format(timing, Stopwatch.GetElapsedTime(started)));
                return Task.CompletedTask;
            });
            await next(context);
            logger.LogInformation("{Method} {Path} {Status} total {TotalMs:0.0} ms, db {DbMs:0.0} ms in {Commands} commands",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                timing.Elapsed.TotalMilliseconds, timing.Commands);
        });
        return app;
    }

    internal static string Format(DbTiming timing, TimeSpan total) => string.Create(CultureInfo.InvariantCulture,
        $"db;dur={timing.Elapsed.TotalMilliseconds:0.0};desc=\"{timing.Commands}\", app;dur={total.TotalMilliseconds:0.0}");
}
