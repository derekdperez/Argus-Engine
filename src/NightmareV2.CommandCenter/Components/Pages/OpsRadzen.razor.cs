using Microsoft.AspNetCore.Components;
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
