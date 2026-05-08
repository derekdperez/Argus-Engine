namespace ArgusEngine.CommandCenter.Web.Clients;
public class UpdatesApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
