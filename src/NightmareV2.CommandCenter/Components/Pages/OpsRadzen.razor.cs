using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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
        
        await JS.InvokeVoidAsync("console.log", "Enumeration restart triggered");
    }

    private async Task RestartSpider(bool all)
    {
        string[]? ids = all ? null : SelectedTargets?.Select(x => x.Id).ToArray();
        var request = new RestartToolRequest(ids, all);
        
        await Http.PostAsJsonAsync("api/ops/spider/restart", request);
        
        // Fixed: Correct C# ternary operator (all ? "..." : "...")
        var message = all ? "Spidering restart queued globally" : "Spidering restart queued for selection";
        await JS.InvokeVoidAsync("alert", message);
    }
}
