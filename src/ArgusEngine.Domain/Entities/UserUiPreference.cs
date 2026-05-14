namespace ArgusEngine.Domain.Entities;

public sealed class UserUiPreference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserKey { get; set; } = "";
    public string PreferenceKey { get; set; } = "";
    public string PreferenceJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

