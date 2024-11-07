namespace PathlingS3Import;

sealed class ProgressCheckpoint
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastProcessedObjectUrl { get; set; }
}
