namespace Game.Gateway.BoundaryEvents;

public sealed class BoundaryEventOptions
{
    public bool Enabled { get; set; }
    public string? Path { get; set; }
    public long SegmentBytes { get; set; } = 64 * 1024 * 1024;
    public int FlushIntervalMilliseconds { get; set; } = 1000;
    public int Capacity { get; set; } = 8192;
    public string Service { get; set; } = "lumberjacks-gateway";
    public string Instance { get; set; } = "unknown";
    public string Release { get; set; } = "unknown";
}
