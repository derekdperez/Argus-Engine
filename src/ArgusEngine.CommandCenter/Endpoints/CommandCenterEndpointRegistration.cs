using ArgusEngine.CommandCenter.DataMaintenance;
using ArgusEngine.CommandCenter.Diagnostics;
using ArgusEngine.CommandCenter.Hubs;

namespace ArgusEngine.CommandCenter.Endpoints;

public static class CommandCenterEndpointRegistration
{
    public static WebApplication MapCommandCenterEndpoints(this WebApplication app)
    {
        // Preserved current/post-deletion endpoints.
        app.MapAssetAdmissionDecisionEndpoints();
        app.MapDataRetentionAdminEndpoints();
        app.MapHttpArtifactBackfillEndpoints();

        // Restored deleted web-application endpoints from 717c1c5 plus current operational status.
        app.MapAdminUsageEndpoints();
        app.MapAssetEndpoints();
        app.MapAssetGraphEndpoints();
        app.MapBusJournalEndpoints();
        app.MapCommandCenterStatusEndpoints();
        app.MapDataMaintenanceEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapEc2WorkerEndpoints();
        app.MapEventTraceEndpoints();
        app.MapFileStoreEndpoints();
        app.MapHighValueFindingEndpoints();
        app.MapHttpRequestQueueEndpoints();
        app.MapOpsEndpoints();
        app.MapTagEndpoints();
        app.MapTargetEndpoints();
        app.MapToolRestartEndpoints();
        app.MapWorkerEndpoints();

        app.MapHub<DiscoveryHub>("/hubs/discovery");

        return app;
    }
}
