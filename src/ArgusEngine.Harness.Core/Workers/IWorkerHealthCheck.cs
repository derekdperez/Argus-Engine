using System.Threading;
using System.Threading.Tasks;

namespace ArgusEngine.Harness.Core.Workers;

public record WorkerHealthCheckResult(bool Success, string Message, string Output = "");

public interface IWorkerHealthCheck
{
    string WorkerName { get; }
    Task<WorkerHealthCheckResult> RunAsync(CancellationToken ct);
}
