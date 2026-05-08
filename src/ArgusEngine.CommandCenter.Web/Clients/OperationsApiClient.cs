namespace ArgusEngine.CommandCenter.Web.Clients;
public class OperationsApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
