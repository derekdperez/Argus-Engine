using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ArgusEngine.Domain.Entities;

[Table("system_errors")]
public sealed class SystemError
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(100)]
    public string Component { get; set; } = "";

    [MaxLength(100)]
    public string? MachineName { get; set; }

    [MaxLength(50)]
    public string LogLevel { get; set; } = "";

    public string Message { get; set; } = "";

    public string? Exception { get; set; }

    public string? LoggerName { get; set; }

    [Column(TypeName = "jsonb")]
    public string? MetadataJson { get; set; }
}
