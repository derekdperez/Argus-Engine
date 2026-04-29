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
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection("RabbitMq"))
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

        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = false;
            var rabbit = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new RabbitMqOptions();
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
}
