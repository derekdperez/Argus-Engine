namespace ArgusEngine.CommandCenter.Web.Clients;
public class WorkerControlApiClient(HttpClient client)
{
    public HttpClient Client { get; } = client;
}
