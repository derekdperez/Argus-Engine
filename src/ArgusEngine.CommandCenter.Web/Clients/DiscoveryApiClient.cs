namespace ArgusEngine.CommandCenter.Web.Clients;
public class DiscoveryApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
