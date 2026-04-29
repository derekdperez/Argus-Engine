namespace NightmareV2.Application.Workers;

public static class SubdomainEnumerationProviderSelection
{
    public static IReadOnlyList<string> ResolveEnabledProviders(SubdomainEnumerationOptions options)
    {
        var providers = new List<string>();
        foreach (var provider in options.DefaultProviders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (provider.Equals("subfinder", StringComparison.OrdinalIgnoreCase) && options.Subfinder.Enabled)
                providers.Add("subfinder");
            else if (provider.Equals("amass", StringComparison.OrdinalIgnoreCase) && options.Amass.Enabled)
                providers.Add("amass");
        }

        return providers;
    }
}
