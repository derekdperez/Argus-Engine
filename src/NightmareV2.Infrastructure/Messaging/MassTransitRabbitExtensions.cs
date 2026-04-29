using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NightmareV2.Infrastructure.Messaging;

/// <summary>
/// Dev: RabbitMQ. Production: swap for MassTransit.AmazonSQS (design §3).
/// </summary>
public static class MassTransitRabbitExtensions
{
    public static IServiceCollection AddNightmareRabbitMq(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator> configureConsumers)
    {
        var rabbitSection = configuration.GetSection("RabbitMq");

        services.AddOptions<RabbitMqOptions>()
            .Configure(options =>
            {
                options.Host = GetString(rabbitSection, nameof(RabbitMqOptions.Host), options.Host);
                options.Username = GetString(rabbitSection, nameof(RabbitMqOptions.Username), options.Username);
                options.Password = GetString(rabbitSection, nameof(RabbitMqOptions.Password), options.Password);
                options.VirtualHost = GetString(rabbitSection, nameof(RabbitMqOptions.VirtualHost), options.VirtualHost);
                options.StartTimeoutSeconds = GetInt(rabbitSection, nameof(RabbitMqOptions.StartTimeoutSeconds), options.StartTimeoutSeconds);
                options.StopTimeoutSeconds = GetInt(rabbitSection, nameof(RabbitMqOptions.StopTimeoutSeconds), options.StopTimeoutSeconds);
            })
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.Host)
                     && !string.IsNullOrWhiteSpace(o.Username)
                     && !string.IsNullOrWhiteSpace(o.Password),
                "RabbitMq Host/Username/Password are required.")
            .Validate(o => o.StartTimeoutSeconds is >= 1 and <= 120, "RabbitMq StartTimeoutSeconds must be in [1,120].")
            .Validate(o => o.StopTimeoutSeconds is >= 1 and <= 120, "RabbitMq StopTimeoutSeconds must be in [1,120].")
            .ValidateOnStart();

        services.TryAddSingleton<BusJournalPublishObserver>();
        services.TryAddSingleton<BusJournalConsumeObserver>();

        services.AddOptions<MassTransitHostOptions>()
            .Configure<IOptions<RabbitMqOptions>>(
                (options, rabbitOptions) =>
                {
                    var rabbit = rabbitOptions.Value;
                    options.WaitUntilStarted = false;
                    options.StartTimeout = TimeSpan.FromSeconds(Math.Clamp(rabbit.StartTimeoutSeconds, 1, 120));
                    options.StopTimeout = TimeSpan.FromSeconds(Math.Clamp(rabbit.StopTimeoutSeconds, 1, 120));
                });

        services.AddMassTransit(x =>
        {
            configureConsumers(x);
            x.SetKebabCaseEndpointNameFormatter();
            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbit = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                var vhost = string.IsNullOrWhiteSpace(rabbit.VirtualHost) ? "/" : rabbit.VirtualHost;
                cfg.Host(rabbit.Host, vhost, h =>
                {
                    h.Username(rabbit.Username);
                    h.Password(rabbit.Password);
                });
                cfg.ConnectPublishObserver(context.GetRequiredService<BusJournalPublishObserver>());
                cfg.ConnectConsumeObserver(context.GetRequiredService<BusJournalConsumeObserver>());
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    private static string GetString(IConfiguration section, string key, string fallback)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetInt(IConfiguration section, string key, int fallback)
    {
        var value = section[key];
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
