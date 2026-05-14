namespace ArgusEngine.CommandCenter.Updates.Api.Services;

public static class ComponentUpdateServiceRegistration
{
    public static IServiceCollection AddComponentUpdateServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ComponentUpdaterOptions>()
            .Bind(configuration.GetSection("Argus:ComponentUpdater"))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.RepositoryPath),
                "Argus:ComponentUpdater:RepositoryPath is required.")
            .Validate(
                options => Path.IsPathRooted(options.RepositoryPath),
                "Argus:ComponentUpdater:RepositoryPath must be an absolute path.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ComposeFilePath),
                "Argus:ComponentUpdater:ComposeFilePath is required.")
            .Validate(
                options => Path.IsPathRooted(options.ComposeFilePath),
                "Argus:ComponentUpdater:ComposeFilePath must be an absolute path.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.GitRemote),
                "Argus:ComponentUpdater:GitRemote is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.MainBranch),
                "Argus:ComponentUpdater:MainBranch is required.")
            .Validate(
                options => options.LogLimit is > 0 and <= 1000,
                "Argus:ComponentUpdater:LogLimit must be between 1 and 1000.")
            .Validate(
                options => options.CommandTimeoutSeconds is > 0 and <= 3600,
                "Argus:ComponentUpdater:CommandTimeoutSeconds must be between 1 and 3600.")
            .ValidateOnStart();

        services.AddSingleton<IComponentUpdateService, ComponentUpdateService>();

        return services;
    }
}
