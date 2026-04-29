using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightmareV2.Application.HighValue;
using NightmareV2.Infrastructure;
using NightmareV2.Infrastructure.Data;
using NightmareV2.Infrastructure.Messaging;
using NightmareV2.Workers.HighValue;
using NightmareV2.Workers.HighValue.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddNightmareInfrastructure(builder.Configuration);

var patternPath = Path.Combine(AppContext.BaseDirectory, "Resources", "RegexPatterns", "high_value_targets.txt");
var definitions = HighValuePatternCatalog.LoadFromFile(patternPath);
builder.Services.AddSingleton(new HighValueRegexMatcher(definitions));

var wordlistDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Wordlists", "high_value");
builder.Services.AddSingleton(new HighValueWordlistBootstrap(HighValueWordlistCatalog.LoadFromDirectory(wordlistDir)));

builder.Services.AddNightmareRabbitMq(
    builder.Configuration,
    x =>
    {
        x.AddConsumer<HighValueRegexConsumer>();
        x.AddConsumer<HighValuePathGuessConsumer>();
    });

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NightmareDbContext>();
    await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    await NightmareDbSchemaPatches.ApplyAfterEnsureCreatedAsync(db).ConfigureAwait(false);
    await NightmareDbSeeder.SeedWorkerSwitchesAsync(db).ConfigureAwait(false);
}

await host.RunAsync().ConfigureAwait(false);
