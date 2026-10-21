using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Workbench.Application;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Tests;

public enum TestDatabase
{
    InMemory,
    Sqlite,
    PostgreSql,
}

/// <summary>
/// An isolated database per test. <c>WORKBENCH_TEST_DB</c> picks the provider for the whole suite: <c>InMemory</c>
/// (the default, fastest), <c>Sqlite</c> (a migrated file per test) or <c>PostgreSql</c> (a database per test, copied from a
/// migrated template, on the server in <c>WORKBENCH_TEST_POSTGRES</c>). CI runs the suite on SQLite and PostgreSQL.
/// </summary>
internal static class TestDb
{
    public static TestDatabase Provider { get; } = Enum.TryParse<TestDatabase>(Environment.GetEnvironmentVariable("WORKBENCH_TEST_DB"), true, out var p)
        ? p
        : TestDatabase.InMemory;

    public static IWorkbenchDbContextFactory CreateFactory() => CreateFactory(Provider);

    public static IWorkbenchDbContextFactory CreateFactory(TestDatabase provider) => provider switch
    {
        TestDatabase.Sqlite => new Factory(Disposable(DatabaseProvider.Sqlite, SqliteTemplates.NewDatabase())),
        TestDatabase.PostgreSql => new Factory(Disposable(DatabaseProvider.PostgreSql, PostgresTemplates.NewDatabase())),
        _ => InMemory(),
    };

    /// <summary>Options for a new test database that is dropped once nothing can reach them (see <see cref="DatabaseReaper"/>).</summary>
    private static DbContextOptions<WorkbenchDbContext> Disposable(DatabaseProvider provider, string connectionString)
    {
        var options = Options(provider, connectionString);
        DatabaseReaper.DropWhenUnreachable(options, provider, connectionString);
        return options;
    }

    /// <summary>A connection string to a new, migrated, empty database for <paramref name="provider"/> (not InMemory).</summary>
    public static string NewConnectionString(DatabaseProvider provider) =>
        provider == DatabaseProvider.Sqlite ? SqliteTemplates.NewDatabase() : PostgresTemplates.NewDatabase();

    public static DbContextOptions<WorkbenchDbContext> Options(DatabaseProvider provider, string connectionString)
    {
        var builder = new DbContextOptionsBuilder<WorkbenchDbContext>();
        builder.Use(provider, connectionString);
        return builder.Options;
    }

    private static IWorkbenchDbContextFactory InMemory()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContextFactory<WorkbenchDbContext>(o => o.UseInMemoryDatabase(databaseName));
        return new ContextFactory(services.BuildServiceProvider().GetRequiredService<IDbContextFactory<WorkbenchDbContext>>());
    }

    private sealed class Factory(DbContextOptions<WorkbenchDbContext> options) : IWorkbenchDbContextFactory
    {
        public Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IWorkbenchDbContext>(new WorkbenchDbContext(options));
    }

    private sealed class ContextFactory(IDbContextFactory<WorkbenchDbContext> inner) : IWorkbenchDbContextFactory
    {
        public async Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            await inner.CreateDbContextAsync(cancellationToken);
    }
}

/// <summary>
/// Drops test databases the suite can no longer use. A PostgreSQL test database takes about 25 MB and a SQLite one about
/// 4 MB, so keeping a few thousand until the run ends fills a CI runner's disk. A database is dropped once the options
/// that point at it (held by its factory and every context made from it) have been garbage collected, which means the
/// test that made it is done; web hosts drop theirs when disposed.
/// </summary>
internal static class DatabaseReaper
{
    private const int CollectEvery = 25;
    private static readonly ConditionalWeakTable<object, Sentinel> Owners = new();
    private static readonly ConcurrentQueue<(DatabaseProvider Provider, string ConnectionString)> Unreachable = new();
    private static int _tracked;

    public static void DropWhenUnreachable(object owner, DatabaseProvider provider, string connectionString)
    {
        Owners.Add(owner, new Sentinel(provider, connectionString));
        if (Interlocked.Increment(ref _tracked) % CollectEvery == 0)
        {
            GC.Collect(); // finalizers queue what became unreachable; the next call drops it
        }

        DropUnreachable();
    }

    public static void DropUnreachable()
    {
        while (Unreachable.TryDequeue(out var database))
        {
            Drop(database.Provider, database.ConnectionString);
        }
    }

    public static void Drop(DatabaseProvider provider, string connectionString)
    {
        try
        {
            if (provider == DatabaseProvider.PostgreSql)
            {
                var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
                PostgresTemplates.Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
            }
            else
            {
                var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
                foreach (var file in new[] { path, $"{path}-journal", $"{path}-wal", $"{path}-shm" })
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or UnauthorizedAccessException)
        {
            // Best effort: the run's leftovers are removed at exit (and PostgreSQL ones on the next run's start).
        }
    }

    private sealed class Sentinel(DatabaseProvider provider, string connectionString)
    {
        ~Sentinel() => Unreachable.Enqueue((provider, connectionString));
    }
}

/// <summary>Migrates one SQLite file per test run, then copies it for every test (much faster than migrating each time).</summary>
internal static class SqliteTemplates
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), $"workbench-tests-{Environment.ProcessId}");
    private static readonly Lazy<string> Template = new(CreateTemplate);

    public static string NewDatabase() => NewDatabase(Template.Value);

    /// <summary>A copy of the SQLite file at <paramref name="template"/>.</summary>
    public static string NewDatabase(string template)
    {
        var path = Path.Combine(Folder, $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Folder);
        File.Copy(template, path);
        return $"Data Source={path};Pooling=False";
    }

    private static string CreateTemplate()
    {
        Directory.CreateDirectory(Folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
        };

        var path = Path.Combine(Folder, "template.db");
        using (var db = new WorkbenchDbContext(TestDb.Options(DatabaseProvider.Sqlite, $"Data Source={path};Pooling=False")))
        {
            db.Database.Migrate();
        }

        return path;
    }
}

/// <summary>
/// PostgreSQL test databases: <c>WORKBENCH_TEST_POSTGRES</c> (server connection string without a database), defaulting
/// to the docker-compose server. One migrated template per run; each test gets <c>CREATE DATABASE ... TEMPLATE</c>.
/// Databases from this run are dropped at exit, and leftovers of earlier runs on start.
/// </summary>
internal static class PostgresTemplates
{
    public const string DefaultServer = "Host=localhost;Port=5432;Username=workbench;Password=workbench";
    private const string Prefix = "wbtest_";
    public static readonly string RunId = $"{Prefix}{Environment.ProcessId}_{DateTime.UtcNow:HHmmss}";
    private static readonly Lazy<string> Template = new(CreateTemplate);
    private static readonly Lazy<bool> Available = new(Probe);
    private static int _counter;

    public static string Server => Environment.GetEnvironmentVariable("WORKBENCH_TEST_POSTGRES") is { Length: > 0 } s ? s : DefaultServer;

    /// <summary>Whether a PostgreSQL server answers (checked once, with a short timeout).</summary>
    public static bool IsAvailable => Available.Value;

    public static string NewDatabase() => NewDatabase(Template.Value);

    /// <summary>A copy of the database <paramref name="template"/> (which must have no open connections).</summary>
    public static string NewDatabase(string template)
    {
        var name = $"{RunId}_{Interlocked.Increment(ref _counter)}";
        Execute($"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"");
        return ConnectionString(name);
    }

    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Server) { Database = database, Pooling = false, IncludeErrorDetail = true }.ConnectionString;

    /// <summary>Runs a command on the server's <c>postgres</c> database.</summary>
    public static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static string CreateTemplate()
    {
        DropDatabases($"datname LIKE '{Prefix}%' AND datname NOT LIKE '{RunId}%'");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                NpgsqlConnection.ClearAllPools();
                DropDatabases($"datname LIKE '{RunId}%'");
            }
            catch (NpgsqlException)
            {
            }
        };

        var template = $"{RunId}_template";
        using (var db = new WorkbenchDbContext(TestDb.Options(DatabaseProvider.PostgreSql, ConnectionString(template))))
        {
            db.Database.Migrate();
        }

        // CREATE DATABASE ... TEMPLATE needs the template to have no open connections.
        NpgsqlConnection.ClearAllPools();
        return template;
    }

    private static void DropDatabases(string filter)
    {
        using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        connection.Open();
        var names = new List<string>();
        using (var list = new NpgsqlCommand($"SELECT datname FROM pg_database WHERE {filter}", connection))
        using (var reader = list.ExecuteReader())
        {
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var name in names)
        {
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            drop.ExecuteNonQuery();
        }
    }

    private static bool Probe()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString("postgres")) { Timeout = 2, Pooling = false };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// A test that needs PostgreSQL: runs when a server answers (CI's service container, or <c>docker compose up</c> locally) and is
/// skipped with a reason otherwise. Filter with <c>--filter Category=PostgreSql</c>.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTemplates.IsAvailable)
        {
            Skip = $"No PostgreSQL at {Redacted()} (start one with `docker compose up -d` or set WORKBENCH_TEST_POSTGRES).";
        }
    }

    internal static string Redacted() => new NpgsqlConnectionStringBuilder(PostgresTemplates.Server) { Password = null }.ConnectionString;
}
