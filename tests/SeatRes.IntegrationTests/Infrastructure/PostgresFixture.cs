using Testcontainers.PostgreSql;

namespace SeatRes.IntegrationTests.Infrastructure;

/// <summary>
/// One real Postgres for the whole "db" collection, plus lazily-built API hosts on top of it.
/// Tests isolate themselves by creating their own show; nothing is truncated between tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = NewContainer();
    private DbApiFactory? _api;
    private DbApiFactory? _fastApi;

    public static PostgreSqlContainer NewContainer() => new PostgreSqlBuilder("postgres:16-alpine")
        .WithCommand("-c", "max_connections=200")
        .Build();

    public string ConnectionString =>
        _container.GetConnectionString() + ";Maximum Pool Size=30;Timeout=30;Command Timeout=15";

    /// <summary>API host with the in-memory fast path disabled: tests prove the database guarantee alone.</summary>
    public DbApiFactory Api => _api ??= new DbApiFactory(ConnectionString, fastPath: false);

    /// <summary>API host with the taken-seat cache and per-seat gate enabled.</summary>
    public DbApiFactory FastApi => _fastApi ??= new DbApiFactory(ConnectionString, fastPath: true);

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
        if (_fastApi is not null) await _fastApi.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>;
