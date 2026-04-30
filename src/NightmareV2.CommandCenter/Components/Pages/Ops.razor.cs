using Microsoft.AspNetCore.Components;
using NightmareV2.CommandCenter.Models;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace NightmareV2.CommandCenter.Components.Pages;

public partial class Ops
{
    private List<TargetDto>? Targets { get; set; }
    private HashSet<string> SelectedTargetIds { get; set; } = new();

    protected override async Task OnInitializedAsync()
    {
        // Fetching initial target list
        Targets = await Http.GetFromJsonAsync<List<TargetDto>>("api/targets") ?? new();
    }

    private void OnTargetToggled(string id, ChangeEventArgs e)
    {
        if (e.Value is bool selected && selected)
            SelectedTargetIds.Add(id);
        else
            SelectedTargetIds.Remove(id);
    }

    private async Task RestartEnum(bool all)
    {
        var targetList = all ? null : SelectedTargetIds.ToArray();
        var request = new RestartToolRequest(targetList, all);
        
        var response = await Http.PostAsJsonAsync("api/ops/subdomain-enum/restart", request);
        if (response.IsSuccessStatusCode && !all)
        {
            SelectedTargetIds.Clear();
        }
    }

    private async Task RestartSpider(bool all)
    {
        var targetList = all ? null : SelectedTargetIds.ToArray();
        var request = new RestartToolRequest(targetList, all);
        
        var response = await Http.PostAsJsonAsync("api/ops/spider/restart", request);
        if (response.IsSuccessStatusCode && !all)
        {
            SelectedTargetIds.Clear();
        }
    }
}

public record TargetDto(string Id, string Name, string Status);
