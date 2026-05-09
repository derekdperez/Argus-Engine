using MassTransit;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ArgusEngine.Infrastructure.Messaging;

public static class MassTransitRabbitExtensions
{
    public static IServiceCollection AddArgusRabbitMq(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator> configureConsumers)
    {
        var rabbitSection = configuration.GetSection("RabbitMq");

        services.AddOptions<RabbitMqOptions>()
            .Configure(options =>
            {
                options.Host = GetString(rabbitSection, nameof(RabbitMqOptions.Host), options.Host);
                options.Port = GetInt(rabbitSection, nameof(RabbitMqOptions.Port), options.Port);
                options.Username = GetString(rabbitSection, nameof(RabbitMqOptions.Username), options.Username);
                options.Password = GetString(rabbitSection, nameof(RabbitMqOptions.Password), options.Password);
                options.VirtualHost = GetString(rabbitSection, nameof(RabbitMqOptions.VirtualHost), options.VirtualHost);
                options.UseTls = GetBool(rabbitSection, nameof(RabbitMqOptions.UseTls), options.UseTls);
                options.WaitUntilStarted = GetBool(
                    rabbitSection,
                    nameof(RabbitMqOptions.WaitUntilStarted),
                    options.WaitUntilStarted);
                options.StartTimeoutSeconds = GetInt(
                    rabbitSection,
                    nameof(RabbitMqOptions.StartTimeoutSeconds),
                    options.StartTimeoutSeconds);
                options.StopTimeoutSeconds = GetInt(
                    rabbitSection,
                    nameof(RabbitMqOptions.StopTimeoutSeconds),
                    options.StopTimeoutSeconds);
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "RabbitMq:Host is required.")
            .Validate(o => o.Port is > 0 and <= 65535, "RabbitMq:Port must be a valid TCP port.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Username), "RabbitMq:Username is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Password), "RabbitMq:Password is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.VirtualHost), "RabbitMq:VirtualHost is required.")
            .Validate(
                o => o.StartTimeoutSeconds is >= 1 and <= 300,
                "RabbitMq:StartTimeoutSeconds must be between 1 and 300.")
            .Validate(
                o => o.StopTimeoutSeconds is >= 1 and <= 300,
                "RabbitMq:StopTimeoutSeconds must be between 1 and 300.")
            .Validate<IHostEnvironment>(
                (options, environment) => environment.IsDevelopment() || !UsesDevelopmentDefaults(options),
                "RabbitMQ development defaults (localhost or guest/guest credentials) are not allowed outside Development.")
            .ValidateOnStart();

        services.TryAddSingleton<BusJournalPublishObserver>();
        services.TryAddSingleton<BusJournalConsumeObserver>();

        services.AddOptions<MassTransitHostOptions>()
            .Configure<IOptions<RabbitMqOptions>>((options, rabbitOptions) =>
            {
                var rabbit = rabbitOptions.Value;

                // Keeping this false avoids the restart storm shown in logs when
                // RabbitMQ is not reachable during process startup. Services still
                // start, expose health endpoints, and MassTransit handles broker
                // availability asynchronously.
                options.WaitUntilStarted = rabbit.WaitUntilStarted;
                options.StartTimeout = TimeSpan.FromSeconds(rabbit.StartTimeoutSeconds);
                options.StopTimeout = TimeSpan.FromSeconds(rabbit.StopTimeoutSeconds);
            });

        services.AddMassTransit(x =>
        {
            configureConsumers(x);

            x.SetKebabCaseEndpointNameFormatter();

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbit = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                var vhost = string.IsNullOrWhiteSpace(rabbit.VirtualHost) ? "/" : rabbit.VirtualHost;

                cfg.Host(
                    rabbit.Host,
                    (ushort)rabbit.Port,
                    vhost,
                    h =>
                    {
                        h.Username(rabbit.Username);
                        h.Password(rabbit.Password);

                        if (rabbit.UseTls)
                        {
                            h.UseSsl(_ => { });
                        }
                    });

                cfg.ConnectPublishObserver(context.GetRequiredService<BusJournalPublishObserver>());
                cfg.ConnectConsumeObserver(context.GetRequiredService<BusJournalConsumeObserver>());
                cfg.UseConsumeFilter(typeof(WorkerCancellationFilter<>), context);
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    private static bool UsesDevelopmentDefaults(RabbitMqOptions options) =>
        string.Equals(options.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.Host, "::1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.Username, "guest", StringComparison.Ordinal)
        || string.Equals(options.Password, "guest", StringComparison.Ordinal);

    private static string GetString(IConfiguration section, string key, string fallback)
    {
        var value = section[key];

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetInt(IConfiguration section, string key, int fallback) =>
        int.TryParse(section[key], out var value) ? value : fallback;

    private static bool GetBool(IConfiguration section, string key, bool fallback) =>
        bool.TryParse(section[key], out var value) ? value : fallback;
}
