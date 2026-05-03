namespace ArgusEngine.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string Username { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    public bool UseTls { get; set; }

    public bool WaitUntilStarted { get; set; } = true;

    public int StartTimeoutSeconds { get; set; } = 15;

    public int StopTimeoutSeconds { get; set; } = 30;
}
