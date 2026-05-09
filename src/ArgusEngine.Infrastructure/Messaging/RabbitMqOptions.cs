namespace ArgusEngine.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string Username { get; set; } = "argus";

    public string Password { get; set; } = "argus";

    public string VirtualHost { get; set; } = "/";

    public bool UseTls { get; set; }

    // Do not fail worker/API process startup just because the RabbitMQ broker is
    // still booting or temporarily unavailable. MassTransit will keep trying to
    // connect in the background, and publish/send calls can fail individually
    // while the broker is unavailable instead of crash-looping the whole host.
    public bool WaitUntilStarted { get; set; }

    public int StartTimeoutSeconds { get; set; } = 120;

    public int StopTimeoutSeconds { get; set; } = 30;
}
