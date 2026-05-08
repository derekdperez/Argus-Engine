namespace ArgusEngine.CommandCenter.Web.Clients;
public class MaintenanceApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
