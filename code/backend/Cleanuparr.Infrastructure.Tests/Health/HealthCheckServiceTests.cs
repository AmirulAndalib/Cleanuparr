using Cleanuparr.Domain.Enums;
using Cleanuparr.Infrastructure.Features.DownloadClient;
using Cleanuparr.Infrastructure.Health;
using Cleanuparr.Infrastructure.Tests.TestHelpers;
using Cleanuparr.Persistence;
using Cleanuparr.Persistence.Models.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Cleanuparr.Infrastructure.Tests.Health;

/// <summary>
/// A client a newer version wrote is hidden from the UI.
/// The background loop has no business probing it either.
/// </summary>
public sealed class HealthCheckServiceTests : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = SqliteTestDatabase.Create("health-check");
    private readonly IDownloadServiceFactory _downloadServiceFactory = Substitute.For<IDownloadServiceFactory>();
    private HealthCheckService _service = null!;

    public async Task InitializeAsync()
    {
        await using DataContext context = CreateContext();
        await context.Database.MigrateAsync();

        ServiceProvider provider = new ServiceCollection()
            .AddScoped(_ => CreateContext())
            .AddSingleton(_downloadServiceFactory)
            .BuildServiceProvider();

        _service = new HealthCheckService(
            Substitute.For<ILogger<HealthCheckService>>(),
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    public Task DisposeAsync()
    {
        _database.Dispose();

        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("type_name")]
    [InlineData("type")]
    public async Task CheckAllClientsHealthAsync_SkipsAClientOfAnUnknownKind(string column)
    {
        Guid unknownId = await AddEnabledClientAsync("from the future");
        await PoisonAsync(column, unknownId);
        Guid knownId = await AddEnabledClientAsync("supported");

        IDictionary<Guid, HealthStatus> results = await _service.CheckAllClientsHealthAsync();

        results.Keys.ShouldBe([knownId]);
        _downloadServiceFactory.DidNotReceive().GetDownloadService(Arg.Is<DownloadClientConfig>(c => c.Id == unknownId));
    }

    private async Task<Guid> AddEnabledClientAsync(string name)
    {
        await using DataContext context = CreateContext();

        DownloadClientConfig client = new()
        {
            Name = name,
            TypeName = DownloadClientTypeName.qBittorrent,
            Type = DownloadClientType.Torrent,
            Host = new Uri("http://client.local"),
            Enabled = true,
        };

        context.DownloadClients.Add(client);
        await context.SaveChangesAsync();

        return client.Id;
    }

    private async Task PoisonAsync(string column, Guid id)
    {
        await using DataContext context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            $"UPDATE download_clients SET {column} = 'fromthefuture' WHERE id = {{0}}",
            id);
    }

    private DataContext CreateContext() => _database.CreateContext<DataContext>();
}
