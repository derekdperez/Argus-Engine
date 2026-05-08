namespace ArgusEngine.CommandCenter.Web.Clients;
public class RealtimeApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
