using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Tests.Scheduling;

/// <summary>An in-memory SQLite database, so foreign-key cascades run like they do in development.</summary>
internal sealed class SqliteTestDb : IWorkbenchDbContextFactory, IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<WorkbenchDbContext> _options;

    public SqliteTestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(_connection, o => o.MigrationsAssembly(DatabaseProviders.SqliteMigrationsAssembly)).Options;
        using var db = new WorkbenchDbContext(_options);
        db.Database.Migrate();
    }

    public Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IWorkbenchDbContext>(new WorkbenchDbContext(_options));

    public void Dispose() => _connection.Dispose();
}
