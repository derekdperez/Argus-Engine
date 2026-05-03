using ArgusEngine.CommandCenter.DataMaintenance;
using ArgusEngine.CommandCenter.Diagnostics;
using ArgusEngine.CommandCenter.Hubs;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class CommandCenterEndpointRegistration
{
    public static WebApplication MapCommandCenterEndpoints(this WebApplication app)
    {
        DiagnosticsEndpoints.Map(app);
        DataMaintenanceEndpoints.Map(app);
        AdminUsageEndpoints.Map(app);
        Ec2WorkerEndpoints.Map(app);
        EventTraceEndpoints.Map(app);
        AssetGraphEndpoints.Map(app);
        TagEndpoints.Map(app);

        app.MapTargetEndpoints();
        app.MapHttpRequestQueueEndpoints();
        app.MapBusJournalEndpoints();
        app.MapAssetEndpoints();
        app.MapFileStoreEndpoints();
        app.MapHighValueFindingEndpoints();
        app.MapToolRestartEndpoints();
        app.MapAssetAdmissionDecisionEndpoints();
        app.MapDataRetentionAdminEndpoints();
        app.MapHttpArtifactBackfillEndpoints();

        app.MapHub<DiscoveryHub>("/hubs/discovery");

        app.MapCommandCenterInlineEndpoints();

        return app;
    }
}
