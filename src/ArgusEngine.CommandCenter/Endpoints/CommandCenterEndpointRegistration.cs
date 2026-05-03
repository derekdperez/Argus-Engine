using ArgusEngine.CommandCenter.Components;
using ArgusEngine.CommandCenter.Hubs;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class CommandCenterEndpointRegistration
{
    public static WebApplication MapCommandCenterEndpoints(this WebApplication app)
    {
        app.MapAssetAdmissionDecisionEndpoints();
        app.MapAssetEndpoints();
        app.MapHighValueFindingEndpoints();
        app.MapDataRetentionAdminEndpoints();
        app.MapHttpArtifactBackfillEndpoints();
        app.MapOpsEndpoints();
        app.MapTargetEndpoints();
        app.MapWorkerEndpoints();
        app.MapHub<DiscoveryHub>("/hubs/discovery");

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
