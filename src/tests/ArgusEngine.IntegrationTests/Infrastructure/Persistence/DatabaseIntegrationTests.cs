using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;
using FluentAssertions;
using ArgusEngine.Domain.Entities;

namespace ArgusEngine.IntegrationTests.Infrastructure.Persistence;

public class DatabaseIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgresContainer.DisposeAsync();
    }

    private ArgusDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ArgusDbContext>()
            .UseNpgsql(_postgresContainer.GetConnectionString())
            .Options;
        return new ArgusDbContext(options);
    }

    [Fact]
    public async Task Database_CanApplyPatches_AndPerformCrud()
    {
        // 1. Initialize DB with EnsureCreated
        using (var db = CreateContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        // 2. Apply Patches
        using (var db = CreateContext())
        {
            await ArgusDbSchemaPatches.ApplyAfterEnsureCreatedAsync(db, NullLogger.Instance);
        }

        // 3. Verify Crud on BusJournalEntry (which I just fixed mapping for)
        using (var db = CreateContext())
        {
            var entry = new BusJournalEntry
            {
                Direction = "Consume",
                MessageType = "TestMessage",
                PayloadJson = "{}",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                HostName = "test-host"
            };

            db.BusJournal.Add(entry);
            await db.SaveChangesAsync();

            var retrieved = await db.BusJournal.FirstAsync();
            retrieved.MessageType.Should().Be("TestMessage");
            retrieved.HostName.Should().Be("test-host");
        }
    }
}
