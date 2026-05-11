using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace ArgusEngine.CommandCenter.Services.DeveloperAutomation;

public static class DeveloperAutomationServiceRegistration
{
    public static IServiceCollection AddDeveloperAutomationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<DeveloperAutomationOptions>()
            .Bind(configuration.GetSection("DeveloperAutomation"))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.GitHubToken),
                "DeveloperAutomation:GitHubToken is required when DeveloperAutomation:Enabled is true.");

        services.AddHttpClient(
            "developer-automation-github",
            (provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<DeveloperAutomationOptions>>().Value;
                client.BaseAddress = NormalizeBaseAddress(options.GitHubApiBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Argus-CommandCenter");
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            });

        services.AddScoped<GitHubDeveloperAutomationClient>();

        return services;
    }

    private static Uri NormalizeBaseAddress(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "https://api.github.com/" : value.Trim();
        if (!text.EndsWith("/", StringComparison.Ordinal))
        {
            text += "/";
        }

        return new Uri(text, UriKind.Absolute);
    }
}
