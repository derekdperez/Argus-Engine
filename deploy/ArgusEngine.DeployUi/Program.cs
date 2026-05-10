using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace ArgusEngine.DeployUi;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            var parsed = GlobalOptions.Parse(args);
            var context = DeploymentContext.Create(parsed.DryRun, parsed.Yes, parsed.BaseRef);
            EnvFileLoader.LoadKnownEnvironmentFiles(context);

            if (parsed.Arguments.Count == 0 || Is(parsed.Arguments[0], "menu"))
            {
                return await new DeploymentMenu(context).RunAsync();
            }

            return await new CommandDispatcher(context).RunAsync(parsed.Arguments);
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
            return 1;
        }
    }

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

internal sealed record GlobalOptions(bool DryRun, bool Yes, string? BaseRef, IReadOnlyList<string> Arguments)
{
    public static GlobalOptions Parse(string[] args)
    {
        var dryRun = false;
        var yes = false;
        string? baseRef = Environment.GetEnvironmentVariable("ARGUS_DEPLOY_BASE_REF");
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--dry-run" or "-n")
            {
                dryRun = true;
                continue;
            }

            if (arg is "--yes" or "-y")
            {
                yes = true;
                continue;
            }

            if (arg is "--base" or "--base-ref")
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{arg} requires a value.");
                }

                baseRef = args[++i];
                continue;
            }

            remaining.Add(arg);
        }

        return new GlobalOptions(dryRun, yes, baseRef, remaining);
    }
}

internal sealed class DeploymentContext
{
    private DeploymentContext(PathResolver paths, bool dryRun, bool assumeYes, string? baseRef)
    {
        Paths = paths;
        DryRun = dryRun;
        AssumeYes = assumeYes;
        BaseRef = baseRef;
        Runner = new ProcessRunner(paths.RepoRoot, dryRun);
        Services = ServiceCatalog.Load(paths);
        ChangeDetector = new ChangeDetector(paths, Services, baseRef);
        Scripts = new ScriptCatalog(paths);
    }

    public PathResolver Paths { get; }
    public bool DryRun { get; }
    public bool AssumeYes { get; }
    public string? BaseRef { get; }
    public ProcessRunner Runner { get; }
    public IReadOnlyList<ServiceDefinition> Services { get; }
    public ChangeDetector ChangeDetector { get; }
    public ScriptCatalog Scripts { get; }

    public static DeploymentContext Create(bool dryRun, bool assumeYes, string? baseRef)
    {
        var paths = PathResolver.Resolve();
        return new DeploymentContext(paths, dryRun, assumeYes, baseRef);
    }
}

internal sealed class PathResolver
{
    private PathResolver(string repoRoot)
    {
        RepoRoot = repoRoot;
        DeployDir = Path.Combine(repoRoot, "deploy");
        ComposeFile = Path.Combine(DeployDir, "docker-compose.yml");
        LocalDeployScript = Path.Combine(DeployDir, "deploy.sh");
        ServiceCatalogFile = Path.Combine(DeployDir, "service-catalog.tsv");
    }

    public string RepoRoot { get; }
    public string DeployDir { get; }
    public string ComposeFile { get; }
    public string LocalDeployScript { get; }
    public string ServiceCatalogFile { get; }

    public static PathResolver Resolve()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (current is not null)
        {
            var slnx = Path.Combine(current.FullName, "ArgusEngine.slnx");
            var deploy = Path.Combine(current.FullName, "deploy");

            if (File.Exists(slnx) && Directory.Exists(deploy))
            {
                return new PathResolver(current.FullName);
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the Argus repo root. Run this CLI from inside the argus-engine checkout.");
    }

    public string Rel(string path)
    {
        try
        {
            return Path.GetRelativePath(RepoRoot, path);
        }
        catch
        {
            return path;
        }
    }
}

internal sealed class DeploymentMenu
{
    private readonly DeploymentContext _context;
    private readonly CommandDispatcher _dispatcher;

    public DeploymentMenu(DeploymentContext context)
    {
        _context = context;
        _dispatcher = new CommandDispatcher(context);
    }

    public async Task<int> RunAsync()
    {
        while (true)
        {
            Ui.Header("Argus deployment console");
            Ui.Info($"Repo: {_context.Paths.RepoRoot}");
            if (_context.DryRun)
            {
                Ui.Warn("Dry-run mode: commands will be printed, not executed.");
            }

            var choice = Ui.Choose("Choose an action", new[]
            {
                "Deploy local changed services — fastest hot-swap path",
                "Deploy local changed services — image rebuild path",
                "Deploy selected local services precisely",
                "Check local Docker Compose status",
                "View or follow local logs",
                "Restart selected local services",
                "Run smoke tests",
                "AWS ECS / ECR deployment",
                "Azure Container Apps / ACR deployment",
                "Google Cloud deployment",
                "Deploy selected services to all cloud platforms",
                "Check status across all platforms",
                "Configure deployment environment",
                "Run cloud login checks / login helpers",
                "Show affected services from Git changes",
                "Run a custom command through this deployment shell",
                "Exit"
            });

            switch (choice)
            {
                case 0:
                    return await _dispatcher.RunAsync(new[] { "local", "hot" });
                case 1:
                    return await _dispatcher.RunAsync(new[] { "local", "image" });
                case 2:
                    return await RunSelectedLocalDeployAsync();
                case 3:
                    return await _dispatcher.RunAsync(new[] { "local", "status" });
                case 4:
                    return await RunLogsMenuAsync();
                case 5:
                    return await RunRestartMenuAsync();
                case 6:
                    return await _dispatcher.RunAsync(new[] { "local", "smoke" });
                case 7:
                    return await RunAwsMenuAsync();
                case 8:
                    return await RunAzureMenuAsync();
                case 9:
                    return await RunGoogleMenuAsync();
                case 10:
                    return await RunCloudReleaseAllAsync();
                case 11:
                    return await _dispatcher.RunAsync(new[] { "status", "all" });
                case 12:
                    return await _dispatcher.RunAsync(new[] { "config", "wizard" });
                case 13:
                    return await _dispatcher.RunAsync(new[] { "login", "all" });
                case 14:
                    ShowAffectedServices();
                    Ui.Pause();
                    break;
                case 15:
                    return await RunCustomCommandAsync();
                default:
                    return 0;
            }
        }
    }

    private async Task<int> RunSelectedLocalDeployAsync()
    {
        var services = SelectServices(preferChanged: true, ecrOnly: false);
        if (services.Count == 0)
        {
            Ui.Warn("No services selected.");
            return 0;
        }

        var mode = Ui.Choose("Selected-service local action", new[]
        {
            "Hot deploy selected services via deploy.sh",
            "Build selected Docker images and run only those services",
            "Recreate selected containers without rebuilding",
            "Restart selected containers only"
        });

        return mode switch
        {
            0 => await _dispatcher.RunAsync(new[] { "local", "hot" }.Concat(services).ToArray()),
            1 => await _dispatcher.RunAsync(new[] { "local", "selected-image" }.Concat(services).ToArray()),
            2 => await _dispatcher.RunAsync(new[] { "local", "selected-up" }.Concat(services).ToArray()),
            _ => await _dispatcher.RunAsync(new[] { "local", "restart" }.Concat(services).ToArray())
        };
    }

    private async Task<int> RunLogsMenuAsync()
    {
        var services = SelectServices(preferChanged: false, ecrOnly: false, allowEmpty: true);
        var follow = Ui.Confirm("Follow logs?", defaultValue: false, assumeYes: false);
        var args = new List<string> { "local", "logs" };
        if (follow)
        {
            args.Add("--follow");
        }

        args.AddRange(services);
        return await _dispatcher.RunAsync(args);
    }

    private async Task<int> RunRestartMenuAsync()
    {
        var services = SelectServices(preferChanged: true, ecrOnly: false, allowEmpty: false);
        return await _dispatcher.RunAsync(new[] { "local", "restart" }.Concat(services).ToArray());
    }

    private async Task<int> RunAwsMenuAsync()
    {
        var choice = Ui.Choose("AWS ECS / ECR", new[]
        {
            "EC2 hybrid release: local core stack + changed ECS workers",
            "Build and push selected ECR images",
            "Deploy selected ECS services",
            "Build, push, and deploy selected ECS services",
            "Replace selected ECS worker tasks",
            "Run ECS autoscale pass",
            "Show ECS / Command Center status",
            "Back"
        });

        if (choice == 7)
        {
            return 0;
        }

        var needsServices = choice is 1 or 2 or 3 or 4;
        var services = needsServices ? SelectServices(preferChanged: true, ecrOnly: true) : new List<string>();

        return choice switch
        {
            0 => await _dispatcher.RunAsync(new[] { "ecs", "hybrid" }),
            1 => await _dispatcher.RunAsync(new[] { "ecs", "build" }.Concat(services).ToArray()),
            2 => await _dispatcher.RunAsync(new[] { "ecs", "deploy" }.Concat(services).ToArray()),
            3 => await _dispatcher.RunAsync(new[] { "ecs", "release" }.Concat(services).ToArray()),
            4 => await _dispatcher.RunAsync(new[] { "ecs", "replace" }.Concat(services).ToArray()),
            5 => await _dispatcher.RunAsync(new[] { "ecs", "autoscale" }),
            _ => await _dispatcher.RunAsync(new[] { "ecs", "status" })
        };
    }

    private async Task<int> RunAzureMenuAsync()
    {
        var choice = Ui.Choose("Azure Container Apps / ACR", new[]
        {
            "Build and push selected ACR images",
            "Deploy selected Container Apps",
            "Build, push, and deploy selected services",
            "Show Container Apps status",
            "Back"
        });

        if (choice == 4)
        {
            return 0;
        }

        var services = choice is 0 or 1 or 2 ? SelectServices(preferChanged: true, ecrOnly: false) : new List<string>();

        return choice switch
        {
            0 => await _dispatcher.RunAsync(new[] { "azure", "build" }.Concat(services).ToArray()),
            1 => await _dispatcher.RunAsync(new[] { "azure", "deploy" }.Concat(services).ToArray()),
            2 => await _dispatcher.RunAsync(new[] { "azure", "release" }.Concat(services).ToArray()),
            _ => await _dispatcher.RunAsync(new[] { "azure", "status" })
        };
    }

    private async Task<int> RunGoogleMenuAsync()
    {
        var choice = Ui.Choose("Google Cloud", new[]
        {
            "Build and push selected Artifact Registry images",
            "Deploy selected Cloud Run services",
            "Build, push, and deploy selected services",
            "Show Cloud Run service status",
            "Back"
        });

        if (choice == 4)
        {
            return 0;
        }

        var services = choice is 0 or 1 or 2 ? SelectServices(preferChanged: true, ecrOnly: false) : new List<string>();

        return choice switch
        {
            0 => await _dispatcher.RunAsync(new[] { "gcp", "build" }.Concat(services).ToArray()),
            1 => await _dispatcher.RunAsync(new[] { "gcp", "deploy" }.Concat(services).ToArray()),
            2 => await _dispatcher.RunAsync(new[] { "gcp", "release" }.Concat(services).ToArray()),
            _ => await _dispatcher.RunAsync(new[] { "gcp", "status" })
        };
    }

    private async Task<int> RunCloudReleaseAllAsync()
    {
        var services = SelectServices(preferChanged: true, ecrOnly: false);
        if (services.Count == 0)
        {
            Ui.Warn("No services selected.");
            return 0;
        }

        return await _dispatcher.RunAsync(new[] { "cloud", "release-all" }.Concat(services).ToArray());
    }

    private async Task<int> RunCustomCommandAsync()
    {
        Ui.Info("Enter a command to run from the repository root. Example: docker compose -f deploy/docker-compose.yml ps");
        var command = Ui.Prompt("Command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return 0;
        }

        return await _context.Runner.RunShellAsync(command);
    }

    private List<string> SelectServices(bool preferChanged, bool ecrOnly, bool allowEmpty = false)
    {
        var candidates = _context.Services
            .Where(s => !ecrOnly || s.EcrEnabled)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            Ui.Warn("No services were found in deploy/service-catalog.tsv.");
            return new List<string>();
        }

        var changed = _context.ChangeDetector.GetAffectedServiceNames()
            .Where(name => candidates.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Ui.WriteLine();
        Ui.Info("Services:");
        for (var i = 0; i < candidates.Count; i++)
        {
            var marker = changed.Contains(candidates[i].Name, StringComparer.OrdinalIgnoreCase) ? "*" : " ";
            var cloud = candidates[i].EcrEnabled ? "cloud" : "local";
            Ui.WriteLine($"  {i + 1,2}. {marker} {candidates[i].Name,-42} {cloud}");
        }

        if (changed.Count > 0)
        {
            Ui.WriteLine();
            Ui.Info("* = affected by current Git changes.");
        }

        var defaultHint = preferChanged && changed.Count > 0 ? "changed" : allowEmpty ? "none" : "all";
        var input = Ui.Prompt(
            $"Select services by number/name, comma-separated. Use 'all', 'changed', or 'none' [default: {defaultHint}]",
            defaultHint);

        if (string.Equals(input, "none", StringComparison.OrdinalIgnoreCase))
        {
            return allowEmpty ? new List<string>() : changed;
        }

        if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase))
        {
            return candidates.Select(s => s.Name).ToList();
        }

        if (string.Equals(input, "changed", StringComparison.OrdinalIgnoreCase))
        {
            if (changed.Count > 0)
            {
                return changed;
            }

            Ui.Warn("No affected services were detected; falling back to all services.");
            return candidates.Select(s => s.Name).ToList();
        }

        var selected = new List<string>();
        var tokens = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (int.TryParse(token, out var index) && index >= 1 && index <= candidates.Count)
            {
                selected.Add(candidates[index - 1].Name);
                continue;
            }

            var match = candidates.FirstOrDefault(s => string.Equals(s.Name, token, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                selected.Add(match.Name);
                continue;
            }

            Ui.Warn($"Ignoring unknown service selection: {token}");
        }

        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ShowAffectedServices()
    {
        var changedFiles = _context.ChangeDetector.GetChangedFiles();
        var affected = _context.ChangeDetector.GetAffectedServiceNames();

        Ui.Header("Changed files");
        if (changedFiles.Count == 0)
        {
            Ui.Warn("No Git changes detected with the current base/ref settings.");
        }
        else
        {
            foreach (var file in changedFiles)
            {
                Ui.WriteLine($"  {file}");
            }
        }

        Ui.Header("Affected services");
        if (affected.Count == 0)
        {
            Ui.Warn("No affected services detected.");
        }
        else
        {
            foreach (var service in affected)
            {
                Ui.WriteLine($"  {service}");
            }
        }
    }
}

internal sealed class CommandDispatcher
{
    private readonly DeploymentContext _context;

    public CommandDispatcher(DeploymentContext context)
    {
        _context = context;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return await new DeploymentMenu(_context).RunAsync();
        }

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        return command switch
        {
            "help" or "-h" or "--help" => ShowHelp(),

            // Compatibility for deploy.sh's old Python-UI handoff.
            // deploy.sh used to exec deploy-ui.py with its own arguments before parsing them.
            "up" => await RunLocalAsync(new[] { "hot" }.Concat(rest).ToArray()),
            "--hot" or "-hot" => await RunLocalAsync(new[] { "hot" }.Concat(rest).ToArray()),
            "--image" or "-image" => await RunLocalAsync(new[] { "image" }.Concat(rest).ToArray()),
            "--fresh" or "-fresh" => await RunLocalAsync(new[] { "fresh" }.Concat(rest).ToArray()),
            "--ecs-workers" => await RunEcsAsync(new[] { "hybrid" }.Concat(rest).ToArray()),

            "local" => await RunLocalAsync(rest),
            "ecs" or "aws" => await RunEcsAsync(rest),
            "azure" or "az" => await RunAzureAsync(rest),
            "gcp" or "google" => await RunGoogleAsync(rest),
            "cloud" => await RunCloudAsync(rest),
            "status" => await RunStatusAsync(rest),
            "config" or "configure" => await RunConfigAsync(rest),
            "login" => await RunLoginAsync(rest),
            "changed" or "changes" or "affected" => ShowAffected(),
            "services" => ShowServices(),
            _ => Unknown(command)
        };
    }

    private int ShowHelp()
    {
        Ui.Header("Argus Deploy CLI");
        Ui.WriteLine("""
Usage:
  dotnet run --project deploy/ArgusEngine.DeployUi -- [--dry-run] [--yes] <command>

Interactive:
  menu                                      Start the menu UI.

Local Docker Compose:
  local hot [service...]                    Incremental hot deploy via deploy/deploy.sh.
  local image [service...]                  Image deploy; selected services use direct compose build/up.
  local fresh                               Full no-cache rebuild/recreate via deploy/deploy.sh.
  local selected-image <service...>         Build and up only the listed compose services.
  local selected-up <service...>            Up only listed compose services without rebuilding.
  local status [service...]                 docker compose ps.
  local logs [--follow] [service...]        docker compose logs.
  local restart [service...]                Restart listed services or all services.
  local smoke | down | clean                Run existing deployment actions.

AWS ECS / ECR:
  ecs hybrid                                EC2 hybrid mode: local core + changed ECS workers.
  ecs build [service...]                    Build/push selected ECR images.
  ecs deploy [service...]                   Create/update selected ECS services.
  ecs release [service...]                  ECR build/push then ECS deploy.
  ecs replace [service...]                  Replace selected worker tasks.
  ecs autoscale                             Run one ECS autoscale pass.
  ecs status                                Show ECS / Command Center status.

Azure:
  azure build [service...]                  Build/push selected ACR images.
  azure deploy [service...]                 Deploy selected Azure Container Apps.
  azure release [service...]                Build/push then deploy.
  azure status                              Show Container Apps status.

Google Cloud:
  gcp build [service...]                    Build/push selected Artifact Registry images.
  gcp deploy [service...]                   Deploy selected Cloud Run services.
  gcp release [service...]                  Build/push then deploy.
  gcp status                                Show Cloud Run services.

Analysis:
  changed [--base <ref>]                    Show files and services affected by Git changes.
  services                                  List known deployable services.
  status all                                Show local/AWS/Azure/GCP status.
  login [all|aws|azure|gcp]                Run cloud login checks and optional login.
  config wizard                             Configure deployment env vars in deploy/.env.local.
  cloud release-all [service...]            Build/deploy services across AWS + Azure + GCP.

Global:
  --dry-run | -n                            Print commands without running them.
  --yes | -y                                Assume yes for destructive confirmations.
  --base | --base-ref <ref>                 Override change-detection base ref.
""");
        return 0;
    }

    private async Task<int> RunLocalAsync(IReadOnlyList<string> args)
    {
        var action = args.Count == 0 ? "hot" : args[0].ToLowerInvariant();
        var services = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        switch (action)
        {
            case "hot":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "--hot" }.Concat(services));
            case "image":
                if (services.Count > 0)
                {
                    return await RunComposeImageDeployAsync(services, build: true, forceRecreate: false);
                }

                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "--image" });
            case "fresh":
            case "--fresh":
            case "-fresh":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "--fresh" });
            case "selected-image":
                RequireServices(services, "local selected-image");
                return await RunComposeImageDeployAsync(services, build: true, forceRecreate: false);
            case "selected-up":
                RequireServices(services, "local selected-up");
                return await RunComposeImageDeployAsync(services, build: false, forceRecreate: true);
            case "status":
            case "ps":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "status" }.Concat(args.Skip(1)));
            case "logs":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "logs" }.Concat(args.Skip(1)));
            case "restart":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "restart" }.Concat(args.Skip(1)));
            case "smoke":
            case "down":
            case "clean":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { action }.Concat(args.Skip(1)));
            default:
                Ui.Error($"Unknown local action: {action}");
                return 2;
        }
    }

    private async Task<int> RunComposeImageDeployAsync(IReadOnlyList<string> services, bool build, bool forceRecreate)
    {
        ValidateServices(services, allowUnknown: true);

        if (build)
        {
            var buildArgs = new List<string> { "compose", "-f", "deploy/docker-compose.yml", "build" };
            buildArgs.AddRange(services);
            var buildExit = await _context.Runner.RunAsync("docker", buildArgs);
            if (buildExit != 0)
            {
                return buildExit;
            }
        }

        var upArgs = new List<string> { "compose", "-f", "deploy/docker-compose.yml", "up", "-d", "--no-deps" };
        if (forceRecreate)
        {
            upArgs.Add("--force-recreate");
        }

        upArgs.AddRange(services);
        return await _context.Runner.RunAsync("docker", upArgs);
    }

    private async Task<int> RunEcsAsync(IReadOnlyList<string> args)
    {
        var action = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        var services = args.Skip(1).ToList();

        switch (action)
        {
            case "hybrid":
            case "workers":
                return await RunScriptRequiredAsync(_context.Paths.LocalDeployScript, new[] { "--ecs-workers" });
            case "repos":
                return await RunScriptRequiredAsync(_context.Scripts.Required("aws/create-ecr-repos.sh"), services);
            case "build":
                ValidateServicesOrDefault(services, cloudOnly: true);
                return await RunScriptRequiredAsync(_context.Scripts.Required("aws/build-push-ecr.sh"), services);
            case "deploy":
                ValidateServicesOrDefault(services, cloudOnly: true);
                return await RunScriptRequiredAsync(_context.Scripts.Required("aws/deploy-ecs-services.sh"), services);
            case "replace":
                ValidateServicesOrDefault(services, cloudOnly: true);
                return await RunScriptRequiredAsync(_context.Scripts.Required("aws/replace-ecs-worker-tasks.sh"), services);
            case "release":
                ValidateServicesOrDefault(services, cloudOnly: true);
                var createExit = await RunScriptRequiredAsync(_context.Scripts.Required("aws/create-ecr-repos.sh"), Array.Empty<string>());
                if (createExit != 0)
                {
                    return createExit;
                }

                var buildExit = await RunScriptRequiredAsync(_context.Scripts.Required("aws/build-push-ecr.sh"), services);
                if (buildExit != 0)
                {
                    return buildExit;
                }

                return await RunScriptRequiredAsync(_context.Scripts.Required("aws/deploy-ecs-services.sh"), services);
            case "autoscale":
                return await RunScriptRequiredAsync(_context.Scripts.Required("aws/autoscale-ecs-workers.sh"), Array.Empty<string>());
            case "status":
                var statusScript = _context.Scripts.Find("aws/ecs-command-center-status.sh");
                if (statusScript is not null)
                {
                    return await RunScriptRequiredAsync(statusScript, services);
                }

                return await _context.Runner.RunAsync("aws", new[] { "ecs", "list-services", "--cluster", RequireEnv("ECS_CLUSTER"), "--output", "table" });
            default:
                Ui.Error($"Unknown ECS action: {action}");
                return 2;
        }
    }

    private async Task<int> RunAzureAsync(IReadOnlyList<string> args)
    {
        EnvFileLoader.LoadProviderEnvironmentFiles(_context, "azure");
        var action = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        var services = args.Skip(1).ToList();

        switch (action)
        {
            case "build":
                ValidateServicesOrDefault(services, cloudOnly: false);
                return await RunProviderScriptAsync("Azure ACR build", _context.Scripts.AzureBuildScript, services);
            case "deploy":
                ValidateServicesOrDefault(services, cloudOnly: false);
                return await RunProviderScriptAsync("Azure Container Apps deploy", _context.Scripts.AzureDeployScript, services);
            case "release":
                ValidateServicesOrDefault(services, cloudOnly: false);
                var buildExit = await RunProviderScriptAsync("Azure ACR build", _context.Scripts.AzureBuildScript, services);
                if (buildExit != 0)
                {
                    return buildExit;
                }

                return await RunProviderScriptAsync("Azure Container Apps deploy", _context.Scripts.AzureDeployScript, services);
            case "status":
                return await RunAzureStatusAsync();
            default:
                Ui.Error($"Unknown Azure action: {action}");
                return 2;
        }
    }

    private async Task<int> RunGoogleAsync(IReadOnlyList<string> args)
    {
        EnvFileLoader.LoadProviderEnvironmentFiles(_context, "google");
        EnvFileLoader.LoadProviderEnvironmentFiles(_context, "gcp");

        var action = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
        var services = args.Skip(1).ToList();

        switch (action)
        {
            case "build":
                ValidateServicesOrDefault(services, cloudOnly: false);
                return await RunProviderScriptAsync("Google Artifact Registry build", _context.Scripts.GoogleBuildScript, services);
            case "deploy":
                ValidateServicesOrDefault(services, cloudOnly: false);
                return await RunProviderScriptAsync("Google Cloud Run deploy", _context.Scripts.GoogleDeployScript, services);
            case "release":
                ValidateServicesOrDefault(services, cloudOnly: false);
                var buildExit = await RunProviderScriptAsync("Google Artifact Registry build", _context.Scripts.GoogleBuildScript, services);
                if (buildExit != 0)
                {
                    return buildExit;
                }

                return await RunProviderScriptAsync("Google Cloud Run deploy", _context.Scripts.GoogleDeployScript, services);
            case "status":
                return await RunGoogleStatusAsync();
            default:
                Ui.Error($"Unknown Google action: {action}");
                return 2;
        }
    }

    private int ShowAffected()
    {
        var files = _context.ChangeDetector.GetChangedFiles();
        var services = _context.ChangeDetector.GetAffectedServiceNames();

        Ui.Header("Changed files");
        foreach (var file in files)
        {
            Ui.WriteLine(file);
        }

        if (files.Count == 0)
        {
            Ui.Warn("No changed files detected.");
        }

        Ui.Header("Affected services");
        foreach (var service in services)
        {
            Ui.WriteLine(service);
        }

        if (services.Count == 0)
        {
            Ui.Warn("No affected services detected.");
        }

        return 0;
    }

    private int ShowServices()
    {
        Ui.Header("Services");
        foreach (var service in _context.Services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var cloud = service.EcrEnabled ? "cloud" : "local";
            Ui.WriteLine($"{service.Name,-42} {cloud,-6} {service.ProjectDir}");
        }

        return 0;
    }

    private int Unknown(string command)
    {
        Ui.Error($"Unknown command: {command}");
        Ui.Info("Run with --help for usage.");
        return 2;
    }

    private async Task<int> RunProviderScriptAsync(string displayName, string? scriptPath, IReadOnlyList<string> services)
    {
        if (scriptPath is null)
        {
            Ui.Error($"{displayName} script was not found.");
            Ui.Info("Expected one of the provider scripts under deploy/azure, deploy/google, deploy/gcp, or a sibling argus-multicloud-deploy-scripts checkout.");
            return 2;
        }

        return await RunScriptRequiredAsync(scriptPath, services);
    }

    private async Task<int> RunAzureStatusAsync()
    {
        var resourceGroup = Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");
        if (string.IsNullOrWhiteSpace(resourceGroup))
        {
            resourceGroup = Ui.Prompt("AZURE_RESOURCE_GROUP");
        }

        return await _context.Runner.RunAsync("az", new[]
        {
            "containerapp", "list",
            "--resource-group", resourceGroup,
            "--output", "table"
        });
    }

    private async Task<int> RunGoogleStatusAsync()
    {
        var region = Environment.GetEnvironmentVariable("GOOGLE_REGION")
            ?? Environment.GetEnvironmentVariable("GCP_REGION")
            ?? Environment.GetEnvironmentVariable("CLOUD_RUN_REGION");

        if (string.IsNullOrWhiteSpace(region))
        {
            region = Ui.Prompt("Cloud Run region", "us-central1");
        }

        return await _context.Runner.RunAsync("gcloud", new[]
        {
            "run", "services", "list",
            "--platform", "managed",
            "--region", region
        });
    }

    private async Task<int> RunScriptRequiredAsync(string scriptPath, IEnumerable<string> args)
    {
        if (!File.Exists(scriptPath))
        {
            Ui.Error($"Required script was not found: {_context.Paths.Rel(scriptPath)}");
            return 2;
        }

        return await _context.Runner.RunScriptAsync(scriptPath, args.ToList());
    }

    private static string RequireEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Set {name} before running this command.");
        }

        return value;
    }

    private void ValidateServicesOrDefault(IReadOnlyList<string> services, bool cloudOnly)
    {
        if (services.Count == 0)
        {
            return;
        }

        ValidateServices(services, allowUnknown: false, cloudOnly);
    }

    private void ValidateServices(IReadOnlyList<string> services, bool allowUnknown, bool cloudOnly = false)
    {
        var known = _context.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var service in services)
        {
            if (!known.TryGetValue(service, out var definition))
            {
                if (allowUnknown)
                {
                    Ui.Warn($"Service '{service}' is not in deploy/service-catalog.tsv; passing it through to the underlying tool.");
                    continue;
                }

                throw new ArgumentException($"Unknown service '{service}'. Run 'services' to list valid services.");
            }

            if (cloudOnly && !definition.EcrEnabled)
            {
                throw new ArgumentException($"Service '{service}' is not marked as cloud/ECR deployable in deploy/service-catalog.tsv.");
            }
        }
    }

    private static void RequireServices(IReadOnlyList<string> services, string commandName)
    {
        if (services.Count == 0)
        {
            throw new ArgumentException($"{commandName} requires at least one service.");
        }
    }
}

internal sealed class ScriptCatalog
{
    private readonly PathResolver _paths;
    private readonly List<string> _searchRoots;

    public ScriptCatalog(PathResolver paths)
    {
        _paths = paths;
        _searchRoots = BuildSearchRoots(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AzureBuildScript = FindFirst(
            "azure/build-push-acr.sh",
            "deploy/azure/build-push-acr.sh",
            "build-push-acr.sh");
        AzureDeployScript = FindFirst(
            "azure/deploy-container-apps.sh",
            "azure/deploy-aca-services.sh",
            "azure/deploy-azure-container-apps.sh",
            "deploy/azure/deploy-container-apps.sh",
            "deploy/azure/deploy-aca-services.sh",
            "deploy/azure/deploy-azure-container-apps.sh");
        GoogleBuildScript = FindFirst(
            "google/build-push-gar.sh",
            "gcp/build-push-gar.sh",
            "google/build-push-gcr.sh",
            "deploy/google/build-push-gar.sh",
            "deploy/gcp/build-push-gar.sh",
            "deploy/google/build-push-gcr.sh");
        GoogleDeployScript = FindFirst(
            "google/deploy-cloud-run.sh",
            "google/deploy-cloudrun-services.sh",
            "gcp/deploy-cloud-run.sh",
            "gcp/deploy-cloudrun-services.sh",
            "deploy/google/deploy-cloud-run.sh",
            "deploy/google/deploy-cloudrun-services.sh",
            "deploy/gcp/deploy-cloud-run.sh",
            "deploy/gcp/deploy-cloudrun-services.sh");
    }

    public string? AzureBuildScript { get; }
    public string? AzureDeployScript { get; }
    public string? GoogleBuildScript { get; }
    public string? GoogleDeployScript { get; }

    public string? Find(string relativePath)
    {
        var direct = Path.Combine(_paths.DeployDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (var root in _searchRoots)
        {
            var candidate = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var deployCandidate = Path.Combine(root, "deploy", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(deployCandidate))
            {
                return deployCandidate;
            }
        }

        return null;
    }

    public string Required(string relativePath) =>
        Find(relativePath) ?? Path.Combine(_paths.DeployDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string? FindFirst(params string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            foreach (var root in _searchRoots)
            {
                var candidate = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildSearchRoots(PathResolver paths)
    {
        yield return paths.DeployDir;
        yield return paths.RepoRoot;
        yield return Path.Combine(paths.RepoRoot, "argus-multicloud-deploy-scripts");

        var parent = Directory.GetParent(paths.RepoRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            yield return Path.Combine(parent, "argus-multicloud-deploy-scripts");
        }
    }
}

internal sealed record ServiceDefinition(
    string Name,
    string ProjectDir,
    string AppDll,
    string Dockerfile,
    bool EcrEnabled,
    string Kind,
    IReadOnlyList<string> ExtraSourceDirs)
{
    public string ProjectFile(string repoRoot) => Path.Combine(repoRoot, "src", ProjectDir, $"{ProjectDir}.csproj");
    public string ProjectSourceDir => NormalizePath($"src/{ProjectDir}");
}

internal static class ServiceCatalog
{
    private static readonly string[] FallbackServices =
    {
        "command-center-gateway",
        "command-center-operations-api",
        "command-center-discovery-api",
        "command-center-worker-control-api",
        "command-center-maintenance-api",
        "command-center-updates-api",
        "command-center-realtime",
        "command-center-web",
        "gatekeeper",
        "worker-spider",
        "worker-http-requester",
        "worker-enum",
        "worker-portscan",
        "worker-highvalue",
        "worker-techid"
    };

    public static IReadOnlyList<ServiceDefinition> Load(PathResolver paths)
    {
        if (!File.Exists(paths.ServiceCatalogFile))
        {
            return FallbackServices
                .Select(name => new ServiceDefinition(name, string.Empty, string.Empty, string.Empty, true, "unknown", Array.Empty<string>()))
                .ToList();
        }

        var services = new List<ServiceDefinition>();

        foreach (var rawLine in File.ReadAllLines(paths.ServiceCatalogFile))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 6)
            {
                continue;
            }

            var extras = columns.Length >= 7
                ? columns[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizePath)
                    .ToArray()
                : Array.Empty<string>();

            services.Add(new ServiceDefinition(
                Name: columns[0],
                ProjectDir: columns[1],
                AppDll: columns[2],
                Dockerfile: NormalizePath(columns[3]),
                EcrEnabled: columns[4] == "1" || columns[4].Equals("true", StringComparison.OrdinalIgnoreCase),
                Kind: columns[5],
                ExtraSourceDirs: extras));
        }

        return services;
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim('/');
}

internal sealed class ChangeDetector
{
    private readonly PathResolver _paths;
    private readonly IReadOnlyList<ServiceDefinition> _services;
    private readonly string? _baseRef;
    private IReadOnlyList<string>? _cachedChangedFiles;
    private IReadOnlyList<string>? _cachedAffectedServices;

    private static readonly HashSet<string> GlobalInvalidators = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArgusEngine.slnx",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "deploy/docker-compose.yml",
        "deploy/service-catalog.tsv",
        ".dockerignore"
    };

    public ChangeDetector(PathResolver paths, IReadOnlyList<ServiceDefinition> services, string? baseRef)
    {
        _paths = paths;
        _services = services;
        _baseRef = baseRef;
    }

    public IReadOnlyList<string> GetChangedFiles()
    {
        if (_cachedChangedFiles is not null)
        {
            return _cachedChangedFiles;
        }

        if (!Directory.Exists(Path.Combine(_paths.RepoRoot, ".git")))
        {
            _cachedChangedFiles = Array.Empty<string>();
            return _cachedChangedFiles;
        }

        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in RunGitLines("diff", "--name-only"))
        {
            files.Add(NormalizePath(file));
        }

        foreach (var file in RunGitLines("diff", "--cached", "--name-only"))
        {
            files.Add(NormalizePath(file));
        }

        foreach (var file in RunGitLines("ls-files", "--others", "--exclude-standard"))
        {
            files.Add(NormalizePath(file));
        }

        var baseRef = ResolveBaseRef();
        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            foreach (var file in RunGitLines("diff", "--name-only", $"{baseRef}...HEAD"))
            {
                files.Add(NormalizePath(file));
            }
        }

        _cachedChangedFiles = files.ToList();
        return _cachedChangedFiles;
    }

    public IReadOnlyList<string> GetAffectedServiceNames()
    {
        if (_cachedAffectedServices is not null)
        {
            return _cachedAffectedServices;
        }

        var changedFiles = GetChangedFiles();
        if (changedFiles.Count == 0)
        {
            _cachedAffectedServices = Array.Empty<string>();
            return _cachedAffectedServices;
        }

        if (changedFiles.Any(file => GlobalInvalidators.Contains(file)))
        {
            _cachedAffectedServices = _services
                .Where(s => s.EcrEnabled || !string.IsNullOrWhiteSpace(s.ProjectDir))
                .Select(s => s.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return _cachedAffectedServices;
        }

        var graph = ProjectDependencyGraph.Build(_paths, _services);
        var affected = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in _services)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                service.ProjectSourceDir
            };

            foreach (var dir in service.ExtraSourceDirs)
            {
                directories.Add(dir);
            }

            if (graph.ServiceSourceDirectories.TryGetValue(service.Name, out var referencedDirectories))
            {
                foreach (var dir in referencedDirectories)
                {
                    directories.Add(dir);
                }
            }

            foreach (var file in changedFiles)
            {
                if (directories.Any(dir => IsUnder(file, dir)))
                {
                    affected.Add(service.Name);
                    break;
                }

                if (!string.IsNullOrWhiteSpace(service.Dockerfile)
                    && string.Equals(file, service.Dockerfile, StringComparison.OrdinalIgnoreCase))
                {
                    affected.Add(service.Name);
                    break;
                }
            }
        }

        _cachedAffectedServices = affected.ToList();
        return _cachedAffectedServices;
    }

    private string? ResolveBaseRef()
    {
        if (!string.IsNullOrWhiteSpace(_baseRef))
        {
            return _baseRef;
        }

        var upstream = RunGitSingle("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (!string.IsNullOrWhiteSpace(upstream))
        {
            var mergeBase = RunGitSingle("merge-base", "HEAD", upstream);
            if (!string.IsNullOrWhiteSpace(mergeBase))
            {
                return mergeBase;
            }
        }

        return null;
    }

    private IEnumerable<string> RunGitLines(params string[] args)
    {
        var result = RunGit(args);
        if (result.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private string? RunGitSingle(params string[] args)
    {
        var result = RunGit(args);
        if (result.ExitCode != 0)
        {
            return null;
        }

        return result.Output.Trim();
    }

    private CommandCapture RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _paths.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new CommandCapture(1, string.Empty);
            }

            var stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new CommandCapture(process.ExitCode, stdout);
        }
        catch
        {
            return new CommandCapture(1, string.Empty);
        }
    }

    private static bool IsUnder(string file, string directory)
    {
        directory = NormalizePath(directory);
        file = NormalizePath(file);
        return file.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim('/');
}

internal sealed record CommandCapture(int ExitCode, string Output);

internal sealed class ProjectDependencyGraph
{
    private ProjectDependencyGraph(Dictionary<string, HashSet<string>> serviceSourceDirectories)
    {
        ServiceSourceDirectories = serviceSourceDirectories;
    }

    public Dictionary<string, HashSet<string>> ServiceSourceDirectories { get; }

    public static ProjectDependencyGraph Build(PathResolver paths, IReadOnlyList<ServiceDefinition> services)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var memo = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in services)
        {
            if (string.IsNullOrWhiteSpace(service.ProjectDir))
            {
                continue;
            }

            var project = service.ProjectFile(paths.RepoRoot);
            result[service.Name] = CollectProjectDirectories(paths, project, memo);
        }

        return new ProjectDependencyGraph(result);
    }

    private static HashSet<string> CollectProjectDirectories(PathResolver paths, string projectFile, Dictionary<string, HashSet<string>> memo)
    {
        projectFile = Path.GetFullPath(projectFile);

        if (memo.TryGetValue(projectFile, out var cached))
        {
            return new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(projectFile))
        {
            memo[projectFile] = directories;
            return directories;
        }

        var projectDirectory = Path.GetDirectoryName(projectFile);
        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            directories.Add(NormalizePath(Path.GetRelativePath(paths.RepoRoot, projectDirectory)));
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch
        {
            memo[projectFile] = directories;
            return directories;
        }

        var references = document
            .Descendants()
            .Where(e => e.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>();

        foreach (var reference in references)
        {
            var referencedProject = Path.GetFullPath(Path.Combine(projectDirectory ?? paths.RepoRoot, reference));
            foreach (var dir in CollectProjectDirectories(paths, referencedProject, memo))
            {
                directories.Add(dir);
            }
        }

        memo[projectFile] = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        return directories;
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim('/');
}

internal sealed class ProcessRunner
{
    private readonly string _workingDirectory;
    private readonly bool _dryRun;

    public ProcessRunner(string workingDirectory, bool dryRun)
    {
        _workingDirectory = workingDirectory;
        _dryRun = dryRun;
    }

    public async Task<int> RunScriptAsync(string scriptPath, IReadOnlyList<string> args)
    {
        if (OperatingSystem.IsWindows())
        {
            if (scriptPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return await RunAsync("pwsh", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath }.Concat(args));
            }

            return await RunAsync("bash", new[] { scriptPath }.Concat(args));
        }

        return await RunAsync("bash", new[] { scriptPath }.Concat(args));
    }

    public async Task<int> RunShellAsync(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            return await RunAsync("pwsh", new[] { "-NoProfile", "-Command", command });
        }

        return await RunAsync("bash", new[] { "-lc", command });
    }

    public async Task<int> RunAsync(string executable, IEnumerable<string> args)
    {
        var argList = args.ToList();
        Ui.Command(executable, argList);

        if (_dryRun)
        {
            return 0;
        }

        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false
        };

        // Prevent deploy/deploy.sh from launching this UI again when the CLI calls
        // the existing shell deployment backend.
        psi.Environment["ARGUS_NO_UI"] = "1";

        foreach (var arg in argList)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            Ui.Error($"Failed to start command: {executable}");
            return 1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}

internal static class EnvFileLoader
{
    public static void LoadKnownEnvironmentFiles(DeploymentContext context)
    {
        LoadIfExists(Path.Combine(context.Paths.DeployDir, ".env"));
        LoadIfExists(Path.Combine(context.Paths.DeployDir, "aws", ".env"));
        LoadProviderEnvironmentFiles(context, "azure");
        LoadProviderEnvironmentFiles(context, "google");
        LoadProviderEnvironmentFiles(context, "gcp");
    }

    public static void LoadProviderEnvironmentFiles(DeploymentContext context, string provider)
    {
        var candidates = new[]
        {
            Path.Combine(context.Paths.DeployDir, provider, ".env"),
            Path.Combine(context.Paths.RepoRoot, "argus-multicloud-deploy-scripts", "deploy", provider, ".env"),
            Path.Combine(Directory.GetParent(context.Paths.RepoRoot)?.FullName ?? context.Paths.RepoRoot, "argus-multicloud-deploy-scripts", "deploy", provider, ".env")
        };

        foreach (var candidate in candidates)
        {
            LoadIfExists(candidate);
        }
    }

    private static void LoadIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].Trim();
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim().Trim('"', '\'');

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

internal static class Ui
{
    public static int Choose(string prompt, IReadOnlyList<string> choices)
    {
        while (true)
        {
            WriteLine();
            Info(prompt);
            for (var i = 0; i < choices.Count; i++)
            {
                WriteLine($"  {i + 1}. {choices[i]}");
            }

            var input = Prompt("Selection", "1");
            if (int.TryParse(input, out var selected) && selected >= 1 && selected <= choices.Count)
            {
                return selected - 1;
            }

            Warn("Enter a valid number.");
        }
    }

    public static string Prompt(string prompt, string? defaultValue = null)
    {
        if (defaultValue is null)
        {
            Write($"{prompt}: ");
        }
        else
        {
            Write($"{prompt} [{defaultValue}]: ");
        }

        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input) && defaultValue is not null)
        {
            return defaultValue;
        }

        return input?.Trim() ?? string.Empty;
    }

    public static bool Confirm(string prompt, bool defaultValue, bool assumeYes)
    {
        if (assumeYes)
        {
            return true;
        }

        var suffix = defaultValue ? "Y/n" : "y/N";
        var input = Prompt($"{prompt} ({suffix})", defaultValue ? "y" : "n");
        return input.StartsWith('y', StringComparison.OrdinalIgnoreCase);
    }

    public static void Header(string text)
    {
        WriteLine();
        WithColor(ConsoleColor.Cyan, () => WriteLine($"== {text} =="));
    }

    public static void Info(string text) => WithColor(ConsoleColor.Green, () => WriteLine(text));
    public static void Warn(string text) => WithColor(ConsoleColor.Yellow, () => WriteLine(text));
    public static void Error(string text) => WithColor(ConsoleColor.Red, () => WriteLine(text));

    public static void Command(string executable, IReadOnlyList<string> args)
    {
        WithColor(ConsoleColor.DarkGray, () => WriteLine($"> {executable} {string.Join(' ', args.Select(QuoteIfNeeded))}"));
    }

    public static void Pause()
    {
        WriteLine();
        Write("Press Enter to continue...");
        _ = Console.ReadLine();
    }

    public static void Write(string text) => Console.Write(text);
    public static void WriteLine() => Console.WriteLine();
    public static void WriteLine(string text) => Console.WriteLine(text);

    private static void WithColor(ConsoleColor color, Action action)
    {
        var original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            action();
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    private static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    }
}
