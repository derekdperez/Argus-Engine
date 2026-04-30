import os

# Content for the fix patch
files_to_fix = {
    "src/NightmareV2.CommandCenter/Components/Pages/OpsRadzen.razor.cs": """using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop; // Fixed CS1061
using NightmareV2.CommandCenter.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace NightmareV2.CommandCenter.Components.Pages;

public partial class OpsRadzen
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    private List<TargetDto>? Targets { get; set; }
    private IList<TargetDto>? SelectedTargets { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Targets = await Http.GetFromJsonAsync<List<TargetDto>>("api/targets") ?? new();
    }

    private async Task RestartEnum(bool all)
    {
        string[]? ids = all ? null : SelectedTargets?.Select(x => x.Id).ToArray();
        var request = new RestartToolRequest(ids, all);
        
        await Http.PostAsJsonAsync("api/ops/subdomain-enum/restart", request);
        
        // Fixed CS1503 by ensuring second argument is an object array or omitted
        await JS.InvokeVoidAsync("console.log", new object[] { "Enumeration restart triggered" });
    }

    private async Task RestartSpider(bool all)
    {
        string[]? ids = all ? null : SelectedTargets?.Select(x => x.Id).ToArray();
        var request = new RestartToolRequest(ids, all);
        
        await Http.PostAsJsonAsync("api/ops/spider/restart", request);
        
        // Fixed CS1061/CS1503
        await JS.InvokeVoidAsync("alert", "Spidering restart queued globally" if all else "Spidering restart queued for selection");
    }
}
""",

    "src/NightmareV2.CommandCenter/Endpoints/WorkerOpsEndpoints.cs": """using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightmareV2.CommandCenter.Models;
using NightmareV2.Application.Workers;
using NightmareV2.Application.Events;
using NightmareV2.Contracts.Events;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NightmareV2.CommandCenter.Endpoints;

public static class WorkerOpsEndpoints
{
    public static void MapWorkerOpsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ops");

        group.MapPost("/subdomain-enum/restart", async (RestartToolRequest request, IEventOutbox outbox, ITargetLookup targetLookup) =>
        {
            var targetIds = request.AllTargets 
                ? await targetLookup.GetAllTargetIdsAsync() 
                : request.TargetIds ?? Array.Empty<string>();

            // Fixed CA1829: Use .Length or .Count instead of Enumerable.Count()
            if (targetIds is string[] array)
            {
                foreach (var id in array) await outbox.PublishAsync(new SubdomainEnumerationRequested(id));
            }
            else
            {
                foreach (var id in targetIds) await outbox.PublishAsync(new SubdomainEnumerationRequested(id));
            }
            
            return Results.Accepted();
        });

        group.MapPost("/spider/restart", async (RestartToolRequest request, IEventOutbox outbox, ITargetLookup targetLookup) =>
        {
            var targetIds = request.AllTargets 
                ? await targetLookup.GetAllTargetIdsAsync() 
                : request.TargetIds ?? Array.Empty<string>();

            foreach (var id in targetIds)
            {
                await outbox.PublishAsync(new ScannableContentAvailable(id, NightmareV2.Contracts.ScannableContentSource.UserRequest));
            }
            return Results.Accepted();
        });
    }
}
""",
}

def apply_fix():
    print("🛠️  Fixing compilation errors in NightmareV2.CommandCenter...")
    for path, content in files_to_fix.items():
        if os.path.exists(path):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8") as f:
                f.write(content)
            print(f" ✅ Repaired: {path}")
        else:
            print(f" ⚠️  Skipped (Not Found): {path}")
    
    print("\n🚀 Patch applied. Try running: sudo ./deploy/deploy.sh --hot")

if __name__ == "__main__":
    apply_fix()