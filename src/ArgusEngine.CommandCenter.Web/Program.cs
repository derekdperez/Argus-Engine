var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/", () => Results.Text("Argus CommandCenter.Web shell is ready for UI migration.", "text/plain"));

await app.RunAsync().ConfigureAwait(false);
