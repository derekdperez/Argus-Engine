using ArgusEngine.Application.TechnologyIdentification.Fingerprints;
using Microsoft.Extensions.DependencyInjection;

namespace ArgusEngine.Infrastructure.TechnologyIdentification;

public static class TechnologyFingerprintCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddArgusTechnologyFingerprintCatalog(this IServiceCollection services)
    {
        services.AddSingleton<ITechnologyFingerprintCatalog, ResourceTechnologyFingerprintCatalog>();
        services.AddHostedService<TechnologyFingerprintCatalogAuditHostedService>();

        return services;
    }
}
