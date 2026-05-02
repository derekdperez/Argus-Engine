using System;

namespace NightmareV2.Contracts.Events;

public interface IStartScanEvent
{
    string Domain { get; }
}

public interface IEnumCompletedEvent
{
    string Domain { get; }
}

public interface IProfilingCompletedEvent
{
    string Domain { get; }
}

public interface IScanFaultedEvent
{
    string Domain { get; }
    string ErrorMessage { get; }
}

public class TriggerEnumJob
{
    public string Domain { get; set; } = string.Empty;
}

public class TriggerProfilingJob
{
    public string Domain { get; set; } = string.Empty;
}

public class TriggerFuzzingJob
{
    public string Domain { get; set; } = string.Empty;
}
