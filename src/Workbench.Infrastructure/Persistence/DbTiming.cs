using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Database time and command count for one unit of work (an HTTP request), for the <c>Server-Timing</c> header and the
/// load tests. <see cref="Begin"/> starts a measurement for the current async flow; every Workbench context's commands
/// in that flow add to it (reader time runs until the reader is closed, so it includes reading the rows).
/// </summary>
public sealed class DbTiming
{
    private static readonly AsyncLocal<DbTiming?> Ambient = new();
    private long _ticks;
    private int _commands;

    public static DbTiming? Current => Ambient.Value;

    public int Commands => Volatile.Read(ref _commands);

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public static DbTiming Begin() => Ambient.Value = new DbTiming();

    internal void Add(TimeSpan duration, bool command)
    {
        Interlocked.Add(ref _ticks, duration.Ticks);
        if (command)
        {
            Interlocked.Increment(ref _commands);
        }
    }
}

/// <summary>Feeds <see cref="DbTiming.Current"/>; a no-op outside a measurement.</summary>
internal sealed class DbTimingInterceptor : DbCommandInterceptor
{
    public static readonly DbTimingInterceptor Instance = new();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        DbTiming.Current?.Add(TimeSpan.Zero, command: true);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        DbTiming.Current?.Add(TimeSpan.Zero, command: true);
        return ValueTask.FromResult(result);
    }

    // The reader's duration covers executing the command and reading every row.
    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        DbTiming.Current?.Add(eventData.Duration, command: false);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        DbTiming.Current?.Add(eventData.Duration, command: true);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        DbTiming.Current?.Add(eventData.Duration, command: true);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        DbTiming.Current?.Add(eventData.Duration, command: true);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        DbTiming.Current?.Add(eventData.Duration, command: true);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => DbTiming.Current?.Add(eventData.Duration, command: true);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DbTiming.Current?.Add(eventData.Duration, command: true);
        return Task.CompletedTask;
    }
}
