using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            ShowHelp();
            return 0;
        }

        var repoRoot = ResolveRepoRoot(options.Root);
        var context = new CliContext(repoRoot, options);

        RenderHeader(context);

        if (!context.IsRepoRoot)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] could not find ArgusEngine.slnx and deploy/docker-compose.yml from the current directory.");
            AnsiConsole.MarkupLine($"Using [grey]{Escape(context.Root)}[/]. Use [green]--root <path>[/] to point at the repo root.");
            AnsiConsole.WriteLine();
        }

        try
        {
            RunMainMenu(context);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes | ExceptionFormats.ShortenMethods);
            return 1;
        }
    }

    private static void RunMainMenu(CliContext context)
    {
        while (true)
        {
            var choice = PromptMenu(
                "What do you want to do?",
                new[]
                {
                    new MenuChoice("System overview / inventory", ShowOverview),
                    new MenuChoice("Check and diagnose prerequisites", CheckMenu),
                    new MenuChoice("Monitor services and containers", MonitorMenu),
                    new MenuChoice("Build and test projects", BuildTestMenu),
                    new MenuChoice("Deploy stack", DeployMenu),
                    new MenuChoice("Manage services and containers", ServicesMenu),
                    new MenuChoice("Scale workers/services", ScaleMenu),
                    new MenuChoice("Restart / recreate services", RestartMenu),
                    new MenuChoice("Logs", LogsMenu),
                    new MenuChoice("Run existing deploy scripts", ScriptsMenu),
                    new MenuChoice("Stop / clean up stack", CleanupMenu),
                    new MenuChoice("Exit", _ => { })
                });

            if (choice.Label == "Exit")
            {
                AnsiConsole.MarkupLine("[green]Done.[/]");
                return;
            }

            AnsiConsole.Clear();
            RenderHeader(context);
            choice.Execute(context);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Press Enter to return to the main menu.[/]");
            Console.ReadLine();
            AnsiConsole.Clear();
            RenderHeader(context);
        }
    }

    private static void ShowOverview(CliContext context)
    {
        var projects = DiscoverProjects(context);
        var services = DiscoverServices(context, includeObservability: context.Options.WithObservability);
        var workers = services.Where(IsWorkerLikeService).ToArray();

        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("Area");
        summary.AddColumn("Count");
        summary.AddColumn("Notes");
        summary.AddRow("Repo root", context.IsRepoRoot ? "OK" : "Not verified", Escape(context.Root));
        summary.AddRow(".NET projects", projects.Count.ToString(), "Discovered from src/ and deploy/");
        summary.AddRow("Compose services", services.Count.ToString(), "Discovered through docker compose config, compose files, or known defaults");
        summary.AddRow("Workers", workers.Length.ToString(), string.Join(", ", workers.Take(6)) + (workers.Length > 6 ? "..." : ""));
        summary.AddRow("Observability overlay", context.Options.WithObservability ? "Enabled" : "Disabled", "Use --with-observability to include Grafana/Prometheus/OTel compose overlay");

        AnsiConsole.Write(summary);

        AnsiConsole.WriteLine();
        RenderServicesTable("Compose services", services, workers);
        AnsiConsole.WriteLine();
        RenderProjectsTable(context, projects);
    }

    private static void CheckMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Checks and diagnostics",
            new[]
            {
                new MenuChoice("Run all local checks", c =>
                {
                    CheckTools(c);
                    CheckRepoShape(c);
                    ValidateCompose(c);
                    RunHttpHealthChecks(c);
                }),
                new MenuChoice("Check required tools", CheckTools),
                new MenuChoice("Check repo layout", CheckRepoShape),
                new MenuChoice("Validate Docker Compose config", ValidateCompose),
                new MenuChoice("Check HTTP health endpoints", RunHttpHealthChecks),
                new MenuChoice("Show docker compose config services", c =>
                {
                    RunCompose(c, new[] { "config", "--services" });
                }),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void MonitorMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Monitoring",
            new[]
            {
                new MenuChoice("Show compose status once", c => RunCompose(c, new[] { "ps" })),
                new MenuChoice("Watch compose status", WatchComposeStatus),
                new MenuChoice("Show container resource stats once", c => RunCommand(c, "docker", new[] { "stats", "--no-stream" })),
                new MenuChoice("Stream container resource stats", c => RunCommand(c, "docker", new[] { "stats" })),
                new MenuChoice("Show compose top", c => RunCompose(c, new[] { "top" })),
                new MenuChoice("Follow logs", FollowLogs),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void BuildTestMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Build and test",
            new[]
            {
                new MenuChoice("dotnet restore solution", c => RunCommand(c, "dotnet", new[] { "restore", "ArgusEngine.slnx" })),
                new MenuChoice("dotnet build solution", c => RunCommand(c, "dotnet", new[] { "build", "ArgusEngine.slnx" })),
                new MenuChoice("dotnet test solution", c => RunCommand(c, "dotnet", new[] { "test", "ArgusEngine.slnx" })),
                new MenuChoice("Build selected project", BuildSelectedProject),
                new MenuChoice("Test selected project", TestSelectedProject),
                new MenuChoice("Docker compose build all", c => RunCompose(c, new[] { "build" })),
                new MenuChoice("Docker compose build selected service(s)", BuildSelectedServices),
                new MenuChoice("Run smoke test", RunSmokeTest),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void DeployMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Deploy",
            new[]
            {
                new MenuChoice("Incremental hot deploy via deploy.sh", c => RunDeployScript(c, new[] { "up" })),
                new MenuChoice("Incremental image deploy via deploy.sh", c => RunDeployScript(c, new[] { "--image", "up" })),
                new MenuChoice("Fresh no-cache deploy via deploy.sh", c => RunDeployScript(c, new[] { "-fresh", "up" })),
                new MenuChoice("Deploy with ECS workers via deploy.sh", c => RunDeployScript(c, new[] { "--ecs-workers", "up" })),
                new MenuChoice("Compose up -d --build full stack", c => RunCompose(c, new[] { "up", "-d", "--build" })),
                new MenuChoice("Compose up selected service(s)", ComposeUpSelectedServices),
                new MenuChoice("Compose up full stack with observability overlay", ComposeUpWithObservability),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void ServicesMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Services and containers",
            new[]
            {
                new MenuChoice("List compose services", c => RenderServicesTable("Compose services", DiscoverServices(c, c.Options.WithObservability), DiscoverServices(c, c.Options.WithObservability).Where(IsWorkerLikeService).ToArray())),
                new MenuChoice("Show compose ps", c => RunCompose(c, new[] { "ps" })),
                new MenuChoice("Start selected service(s)", StartSelectedServices),
                new MenuChoice("Stop selected service(s)", StopSelectedServices),
                new MenuChoice("Pause selected service(s)", PauseSelectedServices),
                new MenuChoice("Unpause selected service(s)", UnpauseSelectedServices),
                new MenuChoice("Open shell in service container", ExecShellInService),
                new MenuChoice("Run one-off service command", RunOneOffCommand),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void ScaleMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Scale",
            new[]
            {
                new MenuChoice("Scale one worker/service", ScaleOneService),
                new MenuChoice("Scale multiple workers/services", ScaleMultipleServices),
                new MenuChoice("Scale all workers to a replica count", ScaleAllWorkers),
                new MenuChoice("Apply EC2 worker scale helper if present", RunEc2ScaleHelper),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void RestartMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Restart and recreate",
            new[]
            {
                new MenuChoice("Restart selected service(s)", RestartSelectedServices),
                new MenuChoice("Restart all compose services", c => RunCompose(c, new[] { "restart" })),
                new MenuChoice("Recreate selected service(s)", RecreateSelectedServices),
                new MenuChoice("Recreate all services without build", c => ConfirmThen(c, "Recreate every compose service?", () => RunCompose(c, new[] { "up", "-d", "--force-recreate", "--no-build" }))),
                new MenuChoice("Run deploy.sh restart", c => RunDeployScript(c, new[] { "restart" })),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void LogsMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Logs",
            new[]
            {
                new MenuChoice("Show recent logs for all services", c => RunCompose(c, new[] { "logs", "--tail", PromptTail() })),
                new MenuChoice("Show recent logs for selected service(s)", ShowSelectedLogs),
                new MenuChoice("Follow logs for selected service(s)", FollowLogs),
                new MenuChoice("Run deploy/logs.sh if present", RunLogsScript),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void ScriptsMenu(CliContext context)
    {
        var scripts = Directory.Exists(context.DeployDir)
            ? Directory.GetFiles(context.DeployDir, "*.sh", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        if (scripts.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No deploy/*.sh scripts found.[/]");
            return;
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a script to run")
                .PageSize(20)
                .AddChoices(scripts.Select(s => Path.GetRelativePath(context.Root, s))));

        var extra = AnsiConsole.Prompt(
            new TextPrompt<string>("Extra arguments? [grey](leave blank for none)[/]")
                .AllowEmpty());

        var args = new List<string> { selected };
        args.AddRange(SplitArgs(extra));
        RunCommand(context, "bash", args);
    }

    private static void CleanupMenu(CliContext context)
    {
        var choice = PromptMenu(
            "Stop / clean up",
            new[]
            {
                new MenuChoice("Compose down --remove-orphans", c => ConfirmThen(c, "Stop the stack and remove orphans?", () => RunCompose(c, new[] { "down", "--remove-orphans" }))),
                new MenuChoice("Compose down --remove-orphans --volumes", c => ConfirmThen(c, "Remove containers AND compose volumes? This deletes local data.", () => RunCompose(c, new[] { "down", "--remove-orphans", "--volumes" }))),
                new MenuChoice("Run deploy.sh clean", c => ConfirmThen(c, "Run deploy.sh clean? This removes compose volumes when confirmed.", () => RunDeployScript(c, new[] { "clean" }, new Dictionary<string, string> { ["CONFIRM_ARGUS_CLEAN"] = "yes" }))),
                new MenuChoice("Prune dangling Docker build cache", c => ConfirmThen(c, "Run docker builder prune?", () => RunCommand(c, "docker", new[] { "builder", "prune" }))),
                new MenuChoice("Back", _ => { })
            });

        if (choice.Label != "Back")
        {
            choice.Execute(context);
        }
    }

    private static void CheckTools(CliContext context)
    {
        var rows = new[]
        {
            CheckTool(context, "dotnet", new[] { "--version" }),
            CheckDockerCompose(context),
            CheckTool(context, "docker", new[] { "version", "--format", "{{.Server.Version}}" }),
            CheckTool(context, "git", new[] { "--version" }),
            CheckTool(context, "bash", new[] { "--version" }),
            CheckTool(context, "curl", new[] { "--version" })
        };

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Tool");
        table.AddColumn("Status");
        table.AddColumn("Details");

        foreach (var row in rows)
        {
            table.AddRow(Escape(row.Name), row.Ok ? "[green]OK[/]" : "[red]Missing/failed[/]", Escape(row.Details));
        }

        AnsiConsole.Write(table);
    }

    private static ToolCheck CheckTool(CliContext context, string command, IReadOnlyList<string> args)
    {
        var result = RunCapture(context, command, args, allowFailure: true, quiet: true);
        var details = string.IsNullOrWhiteSpace(result.Output)
            ? result.Error.Trim()
            : result.Output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;

        return new ToolCheck(command, result.ExitCode == 0, details);
    }

    private static ToolCheck CheckDockerCompose(CliContext context)
    {
        var result = RunCapture(context, "docker", new[] { "compose", "version" }, allowFailure: true, quiet: true);
        if (result.ExitCode == 0)
        {
            return new ToolCheck("docker compose", true, result.Output.Trim());
        }

        result = RunCapture(context, "docker-compose", new[] { "version" }, allowFailure: true, quiet: true);
        return new ToolCheck("docker-compose", result.ExitCode == 0, string.IsNullOrWhiteSpace(result.Output) ? result.Error.Trim() : result.Output.Trim());
    }

    private static void CheckRepoShape(CliContext context)
    {
        var checks = new (string Name, string Path, bool Ok)[]
        {
            ("Solution", "ArgusEngine.slnx", File.Exists(Path.Combine(context.Root, "ArgusEngine.slnx"))),
            ("Deploy compose", "deploy/docker-compose.yml", File.Exists(Path.Combine(context.Root, "deploy", "docker-compose.yml"))),
            ("Deploy script", "deploy/deploy.sh", File.Exists(Path.Combine(context.Root, "deploy", "deploy.sh"))),
            ("Smoke test", "deploy/smoke-test.sh", File.Exists(Path.Combine(context.Root, "deploy", "smoke-test.sh"))),
            ("Source folder", "src", Directory.Exists(Path.Combine(context.Root, "src"))),
            ("Deploy UI project", "deploy/ArgusEngine.DeployUi/ArgusEngine.DeployUi.csproj", File.Exists(Path.Combine(context.Root, "deploy", "ArgusEngine.DeployUi", "ArgusEngine.DeployUi.csproj")))
        };

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Check");
        table.AddColumn("Status");
        table.AddColumn("Path");

        foreach (var check in checks)
        {
            table.AddRow(Escape(check.Name), check.Ok ? "[green]OK[/]" : "[red]Missing[/]", Escape(check.Path));
        }

        AnsiConsole.Write(table);
    }

    private static void ValidateCompose(CliContext context)
    {
        RunCompose(context, new[] { "config", "--quiet" });
    }

    private static void RunHttpHealthChecks(CliContext context)
    {
        var endpoints = new[]
        {
            ("Gateway ready", "http://127.0.0.1:8081/health/ready"),
            ("Web ready", "http://127.0.0.1:8082/health/ready"),
            ("Operations API ready", "http://127.0.0.1:8083/health/ready"),
            ("Discovery API ready", "http://127.0.0.1:8084/health/ready"),
            ("Worker Control API ready", "http://127.0.0.1:8085/health/ready"),
            ("Maintenance API ready", "http://127.0.0.1:8086/health/ready"),
            ("Updates API ready", "http://127.0.0.1:8087/health/ready"),
            ("Realtime ready", "http://127.0.0.1:8088/health/ready"),
            ("Prometheus", "http://127.0.0.1:9090/-/ready"),
            ("Grafana", "http://127.0.0.1:3000/api/health")
        };

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4)
        };

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Endpoint");
        table.AddColumn("Status");
        table.AddColumn("URL");

        foreach (var (name, url) in endpoints)
        {
            try
            {
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                var color = response.IsSuccessStatusCode ? "green" : "yellow";
                table.AddRow(Escape(name), $"[{color}]{(int)response.StatusCode} {Escape(response.ReasonPhrase ?? "")}[/]", Escape(url));
            }
            catch (Exception ex)
            {
                table.AddRow(Escape(name), "[red]Unavailable[/]", Escape($"{url} ({ex.Message})"));
            }
        }

        AnsiConsole.Write(table);
    }

    private static void WatchComposeStatus(CliContext context)
    {
        var seconds = AnsiConsole.Prompt(
            new TextPrompt<int>("Refresh interval seconds?")
                .DefaultValue(5)
                .Validate(v => v <= 0 ? ValidationResult.Error("Use a positive interval.") : ValidationResult.Success()));

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                AnsiConsole.Clear();
                RenderHeader(context);
                AnsiConsole.MarkupLine($"[grey]{Escape(DateTimeOffset.Now.ToString("u"))}[/] [yellow]Press Ctrl+C to stop watching.[/]");
                AnsiConsole.WriteLine();

                var result = RunCapture(context, ComposeCommand(context), ComposeArguments(context, new[] { "ps" }), allowFailure: true, quiet: true);
                WriteCommandOutput(result);

                Thread.Sleep(TimeSpan.FromSeconds(seconds));
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void BuildSelectedProject(CliContext context)
    {
        var project = SelectProject(context, "Build which project?");
        if (project is null)
        {
            return;
        }

        RunCommand(context, "dotnet", new[] { "build", project });
    }

    private static void TestSelectedProject(CliContext context)
    {
        var project = SelectProject(context, "Test which project?");
        if (project is null)
        {
            return;
        }

        RunCommand(context, "dotnet", new[] { "test", project });
    }

    private static void BuildSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Build which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "build" }.Concat(services).ToArray());
    }

    private static void RunSmokeTest(CliContext context)
    {
        var path = Path.Combine(context.DeployDir, "smoke-test.sh");
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[red]deploy/smoke-test.sh not found.[/]");
            return;
        }

        var baseUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("BASE_URL?")
                .DefaultValue("http://localhost:8082"));

        RunCommand(
            context,
            "bash",
            new[] { "deploy/smoke-test.sh" },
            new Dictionary<string, string> { ["BASE_URL"] = baseUrl });
    }

    private static void ComposeUpSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Deploy which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "up", "-d", "--build" }.Concat(services).ToArray());
    }

    private static void ComposeUpWithObservability(CliContext context)
    {
        var clone = context with { Options = context.Options with { WithObservability = true } };
        RunCompose(clone, new[] { "up", "-d", "--build" });
    }

    private static void StartSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Start which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "start" }.Concat(services).ToArray());
    }

    private static void StopSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Stop which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        ConfirmThen(context, $"Stop {services.Count} service(s)?", () => RunCompose(context, new[] { "stop" }.Concat(services).ToArray()));
    }

    private static void PauseSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Pause which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "pause" }.Concat(services).ToArray());
    }

    private static void UnpauseSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Unpause which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "unpause" }.Concat(services).ToArray());
    }

    private static void ExecShellInService(CliContext context)
    {
        var service = SelectOneService(context, "Open a shell in which service?", workersOnly: false);
        if (service is null)
        {
            return;
        }

        var shell = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Shell")
                .AddChoices("sh", "bash"));

        RunCompose(context, new[] { "exec", service, shell });
    }

    private static void RunOneOffCommand(CliContext context)
    {
        var service = SelectOneService(context, "Run command in which service image?", workersOnly: false);
        if (service is null)
        {
            return;
        }

        var command = AnsiConsole.Prompt(
            new TextPrompt<string>("Command?")
                .DefaultValue("sh -lc 'env | sort | head -50'"));

        var args = new List<string> { "run", "--rm", service };
        args.AddRange(SplitArgs(command));
        RunCompose(context, args);
    }

    private static void ScaleOneService(CliContext context)
    {
        var service = SelectOneService(context, "Scale which service?", workersOnly: false);
        if (service is null)
        {
            return;
        }

        var replicas = PromptReplicaCount();
        RunCompose(context, new[] { "up", "-d", "--no-build", "--scale", $"{service}={replicas}", service });
    }

    private static void ScaleMultipleServices(CliContext context)
    {
        var services = SelectServices(context, "Scale which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        var replicas = PromptReplicaCount();
        var args = new List<string> { "up", "-d", "--no-build" };
        args.AddRange(services.SelectMany(service => new[] { "--scale", $"{service}={replicas}" }));
        args.AddRange(services);
        RunCompose(context, args);
    }

    private static void ScaleAllWorkers(CliContext context)
    {
        var workers = DiscoverServices(context, context.Options.WithObservability)
            .Where(IsWorkerLikeService)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (workers.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No worker-like services discovered.[/]");
            return;
        }

        var replicas = PromptReplicaCount();
        var args = new List<string> { "up", "-d", "--no-build" };
        args.AddRange(workers.SelectMany(service => new[] { "--scale", $"{service}={replicas}" }));
        args.AddRange(workers);

        ConfirmThen(context, $"Scale {workers.Length} worker services to {replicas} replica(s)?", () => RunCompose(context, args));
    }

    private static void RunEc2ScaleHelper(CliContext context)
    {
        var path = Path.Combine(context.DeployDir, "apply-ec2-worker-scale.sh");
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[yellow]deploy/apply-ec2-worker-scale.sh not found.[/]");
            return;
        }

        var extra = AnsiConsole.Prompt(
            new TextPrompt<string>("Arguments for apply-ec2-worker-scale.sh?")
                .AllowEmpty());

        var args = new List<string> { "deploy/apply-ec2-worker-scale.sh" };
        args.AddRange(SplitArgs(extra));
        RunCommand(context, "bash", args);
    }

    private static void RestartSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Restart which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "restart" }.Concat(services).ToArray());
    }

    private static void RecreateSelectedServices(CliContext context)
    {
        var services = SelectServices(context, "Recreate which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        ConfirmThen(context, $"Force recreate {services.Count} service(s)?", () => RunCompose(context, new[] { "up", "-d", "--force-recreate", "--no-deps", "--no-build" }.Concat(services).ToArray()));
    }

    private static void ShowSelectedLogs(CliContext context)
    {
        var services = SelectServices(context, "Show logs for which service(s)?", workersOnly: false);
        if (services.Count == 0)
        {
            return;
        }

        RunCompose(context, new[] { "logs", "--tail", PromptTail() }.Concat(services).ToArray());
    }

    private static void FollowLogs(CliContext context)
    {
        var services = SelectServices(context, "Follow logs for which service(s)?", workersOnly: false);
        var args = new List<string> { "logs", "--tail", PromptTail(), "-f" };
        args.AddRange(services);
        RunCompose(context, args);
    }

    private static void RunLogsScript(CliContext context)
    {
        var path = Path.Combine(context.DeployDir, "logs.sh");
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[yellow]deploy/logs.sh not found.[/]");
            return;
        }

        var extra = AnsiConsole.Prompt(
            new TextPrompt<string>("Arguments for logs.sh?")
                .DefaultValue("--errors"));

        var args = new List<string> { "deploy/logs.sh" };
        args.AddRange(SplitArgs(extra));
        RunCommand(context, "bash", args);
    }

    private static void RunDeployScript(CliContext context, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment = null)
    {
        var script = Path.Combine(context.DeployDir, "deploy.sh");
        if (!File.Exists(script))
        {
            AnsiConsole.MarkupLine("[red]deploy/deploy.sh not found.[/]");
            return;
        }

        RunCommand(context, "bash", new[] { "deploy/deploy.sh" }.Concat(args).ToArray(), environment);
    }

    private static void RunCompose(CliContext context, IEnumerable<string> args)
    {
        RunCommand(context, ComposeCommand(context), ComposeArguments(context, args));
    }

    private static string ComposeCommand(CliContext context)
    {
        var dockerCompose = RunCapture(context, "docker", new[] { "compose", "version" }, allowFailure: true, quiet: true);
        return dockerCompose.ExitCode == 0 ? "docker" : "docker-compose";
    }

    private static IReadOnlyList<string> ComposeArguments(CliContext context, IEnumerable<string> args)
    {
        var result = new List<string>();

        if (ComposeCommand(context) == "docker")
        {
            result.Add("compose");
        }

        result.Add("-f");
        result.Add("deploy/docker-compose.yml");

        var observability = Path.Combine(context.DeployDir, "docker-compose.observability.yml");
        if (context.Options.WithObservability && File.Exists(observability))
        {
            result.Add("-f");
            result.Add("deploy/docker-compose.observability.yml");
        }

        result.AddRange(args);
        return result;
    }

    private static int RunCommand(
        CliContext context,
        string command,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var argList = args.ToArray();
        AnsiConsole.Write(new Rule($"[yellow]{Escape(DisplayCommand(command, argList))}[/]").LeftJustified());

        if (context.Options.DryRun)
        {
            AnsiConsole.MarkupLine("[grey]Dry run: command not executed.[/]");
            return 0;
        }

        var psi = new ProcessStartInfo(command)
        {
            WorkingDirectory = context.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in argList)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Console.WriteLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    var previous = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine(e.Data);
                    Console.ForegroundColor = previous;
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                AnsiConsole.MarkupLine("[green]Command completed successfully.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Command failed with exit code {process.ExitCode}.[/]");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start command:[/] {Escape(ex.Message)}");
            return -1;
        }
    }

    private static RunResult RunCapture(
        CliContext context,
        string command,
        IEnumerable<string> args,
        bool allowFailure = false,
        bool quiet = false)
    {
        var argList = args.ToArray();

        if (context.Options.DryRun && !quiet)
        {
            AnsiConsole.MarkupLine($"[grey]Dry run capture:[/] {Escape(DisplayCommand(command, argList))}");
            return new RunResult(0, string.Empty, string.Empty);
        }

        var psi = new ProcessStartInfo(command)
        {
            WorkingDirectory = context.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in argList)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new RunResult(-1, string.Empty, $"Failed to start {command}");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            var result = new RunResult(process.ExitCode, outputTask.GetAwaiter().GetResult(), errorTask.GetAwaiter().GetResult());
            if (!allowFailure && result.ExitCode != 0)
            {
                throw new InvalidOperationException($"{DisplayCommand(command, argList)} failed: {result.Error}");
            }

            return result;
        }
        catch (Exception ex) when (allowFailure)
        {
            return new RunResult(-1, string.Empty, ex.Message);
        }
    }

    private static void WriteCommandOutput(RunResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            Console.WriteLine(result.Output.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine(result.Error.TrimEnd());
            Console.ForegroundColor = previous;
        }

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Exit code: {result.ExitCode}[/]");
        }
    }

    private static IReadOnlyList<string> DiscoverServices(CliContext context, bool includeObservability)
    {
        var clone = context with { Options = context.Options with { WithObservability = includeObservability } };
        var result = RunCapture(clone, ComposeCommand(clone), ComposeArguments(clone, new[] { "config", "--services" }), allowFailure: true, quiet: true);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output))
        {
            return result.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var fromFiles = ParseComposeServicesFromFiles(context, includeObservability);
        if (fromFiles.Count > 0)
        {
            return fromFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return KnownComposeServices(includeObservability)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ParseComposeServicesFromFiles(CliContext context, bool includeObservability)
    {
        var files = new List<string> { Path.Combine(context.DeployDir, "docker-compose.yml") };
        if (includeObservability)
        {
            files.Add(Path.Combine(context.DeployDir, "docker-compose.observability.yml"));
        }

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "services",
            "volumes",
            "configs",
            "environment",
            "ports",
            "depends_on",
            "healthcheck",
            "build",
            "args",
            "image",
            "command",
            "restart",
            "volumes",
            "entrypoint",
            "labels",
            "networks",
            "deploy"
        };

        var services = new List<string>();

        foreach (var file in files.Where(File.Exists))
        {
            var text = File.ReadAllText(file);
            var serviceBlockMatch = Regex.Match(text, @"services:\s*(?<body>.*?)(?:\s+volumes:|\s+networks:|\z)", RegexOptions.Singleline);
            if (!serviceBlockMatch.Success)
            {
                continue;
            }

            var body = serviceBlockMatch.Groups["body"].Value;
            foreach (Match match in Regex.Matches(body, @"(?:^|\s)(?<name>[A-Za-z0-9][A-Za-z0-9_.-]*):\s+(?:image:|build:|environment:|command:|depends_on:|ports:|restart:)", RegexOptions.Singleline))
            {
                var name = match.Groups["name"].Value;
                if (!reserved.Contains(name) && !services.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    services.Add(name);
                }
            }
        }

        return services;
    }

    private static IReadOnlyList<string> KnownComposeServices(bool includeObservability)
    {
        var services = new List<string>
        {
            "postgres",
            "filestore-db-init",
            "redis",
            "rabbitmq",
            "command-center-gateway",
            "command-center-operations-api",
            "command-center-discovery-api",
            "command-center-worker-control-api",
            "command-center-maintenance-api",
            "command-center-updates-api",
            "command-center-realtime",
            "command-center-bootstrapper",
            "command-center-spider-dispatcher",
            "command-center-web",
            "gatekeeper",
            "worker-spider",
            "worker-http-requester",
            "worker-enum",
            "worker-portscan",
            "worker-highvalue",
            "worker-techid"
        };

        if (includeObservability)
        {
            services.AddRange(new[] { "otel-collector", "prometheus", "grafana" });
        }

        return services;
    }

    private static IReadOnlyList<string> DiscoverProjects(CliContext context)
    {
        var projects = new List<string>();

        var src = Path.Combine(context.Root, "src");
        if (Directory.Exists(src))
        {
            projects.AddRange(Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories));
        }

        var deployUi = Path.Combine(context.Root, "deploy", "ArgusEngine.DeployUi");
        if (Directory.Exists(deployUi))
        {
            projects.AddRange(Directory.GetFiles(deployUi, "*.csproj", SearchOption.AllDirectories));
        }

        return projects
            .Select(path => Path.GetRelativePath(context.Root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void RenderServicesTable(string title, IReadOnlyList<string> services, IReadOnlyCollection<string> workers)
    {
        AnsiConsole.Write(new Rule($"[bold]{Escape(title)}[/]").LeftJustified());
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Service");
        table.AddColumn("Kind");

        foreach (var service in services)
        {
            var kind = workers.Contains(service) || IsWorkerLikeService(service)
                ? "[cyan]worker[/]"
                : service.Contains("postgres", StringComparison.OrdinalIgnoreCase) ||
                  service.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
                  service.Contains("rabbitmq", StringComparison.OrdinalIgnoreCase)
                    ? "[purple]dependency[/]"
                    : service.Contains("grafana", StringComparison.OrdinalIgnoreCase) ||
                      service.Contains("prometheus", StringComparison.OrdinalIgnoreCase) ||
                      service.Contains("otel", StringComparison.OrdinalIgnoreCase)
                        ? "[blue]observability[/]"
                        : "[green]app[/]";

            table.AddRow(Escape(service), kind);
        }

        AnsiConsole.Write(table);
    }

    private static void RenderProjectsTable(CliContext context, IReadOnlyList<string> projects)
    {
        AnsiConsole.Write(new Rule("[bold]Projects[/]").LeftJustified());
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Project");
        table.AddColumn("Kind");

        foreach (var project in projects)
        {
            var kind = project.Contains(".Workers.", StringComparison.OrdinalIgnoreCase)
                ? "[cyan]worker[/]"
                : project.Contains(".CommandCenter.", StringComparison.OrdinalIgnoreCase)
                    ? "[green]command-center[/]"
                    : project.Contains("tests", StringComparison.OrdinalIgnoreCase)
                        ? "[yellow]test[/]"
                        : project.Contains("DeployUi", StringComparison.OrdinalIgnoreCase)
                            ? "[blue]deploy-cli[/]"
                            : "[grey]library/app[/]";
            table.AddRow(Escape(project), kind);
        }

        AnsiConsole.Write(table);
    }

    private static IReadOnlyList<string> SelectServices(CliContext context, string title, bool workersOnly)
    {
        var services = DiscoverServices(context, context.Options.WithObservability)
            .Where(service => !workersOnly || IsWorkerLikeService(service))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (services.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No services discovered.[/]");
            return Array.Empty<string>();
        }

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title(title)
                .PageSize(20)
                .NotRequired()
                .InstructionsText("[grey](Space to toggle, Enter to accept. Leave empty to cancel.)[/]")
                .AddChoices(services));

        return selected;
    }

    private static string? SelectOneService(CliContext context, string title, bool workersOnly)
    {
        var services = DiscoverServices(context, context.Options.WithObservability)
            .Where(service => !workersOnly || IsWorkerLikeService(service))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (services.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No services discovered.[/]");
            return null;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .PageSize(20)
                .AddChoices(services));
    }

    private static string? SelectProject(CliContext context, string title)
    {
        var projects = DiscoverProjects(context);
        if (projects.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .csproj files discovered.[/]");
            return null;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .PageSize(20)
                .AddChoices(projects));
    }

    private static int PromptReplicaCount()
    {
        return AnsiConsole.Prompt(
            new TextPrompt<int>("Replica count?")
                .DefaultValue(1)
                .Validate(value => value < 0
                    ? ValidationResult.Error("Replica count must be zero or greater.")
                    : ValidationResult.Success()));
    }

    private static string PromptTail()
    {
        var value = AnsiConsole.Prompt(
            new TextPrompt<int>("Tail lines?")
                .DefaultValue(200)
                .Validate(v => v <= 0 ? ValidationResult.Error("Use a positive number.") : ValidationResult.Success()));

        return value.ToString();
    }

    private static void ConfirmThen(CliContext context, string message, Action action)
    {
        if (!context.Options.Yes && !AnsiConsole.Confirm(message, false))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return;
        }

        action();
    }

    private static bool IsWorkerLikeService(string service)
    {
        return service.StartsWith("worker-", StringComparison.OrdinalIgnoreCase) ||
               service.Contains("spider-dispatcher", StringComparison.OrdinalIgnoreCase) ||
               service.Equals("gatekeeper", StringComparison.OrdinalIgnoreCase);
    }

    private static MenuChoice PromptMenu(string title, IEnumerable<MenuChoice> choices)
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<MenuChoice>()
                .Title(title)
                .PageSize(18)
                .UseConverter(choice => choice.Label)
                .AddChoices(choices));
    }

    private static string ResolveRepoRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var solution = Path.Combine(current.FullName, "ArgusEngine.slnx");
            var compose = Path.Combine(current.FullName, "deploy", "docker-compose.yml");
            if (File.Exists(solution) && File.Exists(compose))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void RenderHeader(CliContext context)
    {
        AnsiConsole.Write(
            new FigletText("Argus Deploy")
                .LeftJustified());

        var options = new List<string>();
        if (context.Options.DryRun)
        {
            options.Add("dry-run");
        }

        if (context.Options.Yes)
        {
            options.Add("yes");
        }

        if (context.Options.WithObservability)
        {
            options.Add("observability");
        }

        var suffix = options.Count == 0 ? string.Empty : $" [{string.Join(", ", options)}]";
        AnsiConsole.MarkupLine($"[grey]Root:[/] {Escape(context.Root)}{Escape(suffix)}");
        AnsiConsole.WriteLine();
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]ArgusEngine.DeployUi[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Interactive deployment and operations menu for Argus Engine.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Options[/]");
        AnsiConsole.MarkupLine("  [green]--root <path>[/]             Use a specific repository root.");
        AnsiConsole.MarkupLine("  [green]--with-observability[/]     Include deploy/docker-compose.observability.yml for compose operations.");
        AnsiConsole.MarkupLine("  [green]--dry-run[/]                Print commands without executing them.");
        AnsiConsole.MarkupLine("  [green]-y, --yes[/]                Skip confirmation prompts for destructive actions.");
        AnsiConsole.MarkupLine("  [green]-h, --help[/]               Show this help.");
    }

    private static string DisplayCommand(string command, IEnumerable<string> args)
    {
        return string.Join(" ", new[] { command }.Concat(args).Select(Quote));
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }

    private static IReadOnlyList<string> SplitArgs(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var args = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (c == '\\' && i + 1 < value.Length)
            {
                i++;
                current.Append(value[i]);
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private static string Escape(string? value)
    {
        return Markup.Escape(value ?? string.Empty);
    }
}

internal sealed record MenuChoice(string Label, Action<CliContext> Execute);

internal sealed record ToolCheck(string Name, bool Ok, string Details);

internal sealed record RunResult(int ExitCode, string Output, string Error);

internal sealed record CliContext(string Root, CliOptions Options)
{
    public string DeployDir => Path.Combine(Root, "deploy");

    public bool IsRepoRoot =>
        File.Exists(Path.Combine(Root, "ArgusEngine.slnx")) &&
        File.Exists(Path.Combine(Root, "deploy", "docker-compose.yml"));
}

internal sealed record CliOptions(
    string? Root,
    bool WithObservability,
    bool DryRun,
    bool Yes,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        string? root = null;
        var withObservability = false;
        var dryRun = false;
        var yes = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--root requires a path.");
                    }

                    root = args[++i];
                    break;

                case "--with-observability":
                    withObservability = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "-y":
                case "--yes":
                    yes = true;
                    break;

                case "-h":
                case "--help":
                    help = true;
                    break;

                default:
                    throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        return new CliOptions(root, withObservability, dryRun, yes, help);
    }
}
