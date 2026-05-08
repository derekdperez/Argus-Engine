namespace ArgusEngine.CommandCenter.Updates.Api.Services;

public static class ComponentUpdateServiceRegistration
{
    public static IServiceCollection AddComponentUpdateServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ComponentUpdaterOptions>(
            configuration.GetSection("Argus:ComponentUpdater"));

        services.AddSingleton<IComponentUpdateService, ComponentUpdateService>();

        return services;
    }
}

