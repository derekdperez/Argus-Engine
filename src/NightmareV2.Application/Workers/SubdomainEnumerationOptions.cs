namespace NightmareV2.Application.Workers;

public sealed class SubdomainEnumerationOptions
{
    public bool Enabled { get; init; } = true;
    public string WorkingDirectory { get; init; } = "enum-output";
    public int MaxSubdomainsPerJob { get; init; } = 100_000;
    public bool QueueProvidersOnTargetCreated { get; init; } = true;
    public string[] DefaultProviders { get; init; } = ["subfinder", "amass"];
    public SubfinderOptions Subfinder { get; init; } = new();
    public AmassOptions Amass { get; init; } = new();
}

public sealed class SubfinderOptions
{
    public bool Enabled { get; init; } = true;
    public string BinaryPath { get; init; } = "subfinder";
    public int TimeoutSeconds { get; init; } = 180;
}

public sealed class AmassOptions
{
    public bool Enabled { get; init; } = true;
    public string BinaryPath { get; init; } = "amass";
    public int TimeoutMinutes { get; init; } = 30;
    public bool Active { get; init; } = true;
    public bool BruteForce { get; init; } = true;
    public int DnsQueriesPerSecond { get; init; } = 50;
    public int MaxDepth { get; init; } = 3;
    public int MinForRecursive { get; init; } = 2;
    public string WordlistPath { get; init; } = "data/wordlists/subdomains-high-value.txt";
}
