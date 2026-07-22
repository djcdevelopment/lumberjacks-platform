using Game.Gateway.BoundaryEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Gateway.Tests;

public sealed class BoundaryEventWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumberjacks-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WritesCompleteRowsAndRotatesBySize()
    {
        var writer = new BoundaryEventWriter(Options.Create(new BoundaryEventOptions
        {
            Enabled = true, Path = _directory, SegmentBytes = 1024, FlushIntervalMilliseconds = 0,
        }), NullLogger<BoundaryEventWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 8; i++)
            Assert.True(writer.TryWrite(BoundaryEventEnvelope.Create("request.completed",
                new BoundaryEventSource("test", "test", "test", null), new { duration_ms = i })));
        await writer.StopAsync(CancellationToken.None);

        var files = Directory.GetFiles(_directory, "*.jsonl");
        Assert.True(files.Length > 1);
        Assert.Empty(Directory.GetFiles(_directory, "*.open.jsonl"));
        Assert.All(files, file => Assert.All(File.ReadAllLines(file), line => Assert.Contains("request.completed", line)));
    }

    [Fact]
    public async Task DisabledWriterDoesNotCreateFiles()
    {
        var writer = new BoundaryEventWriter(Options.Create(new BoundaryEventOptions
        {
            Enabled = false, Path = _directory,
        }), NullLogger<BoundaryEventWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        Assert.False(writer.TryWrite(BoundaryEventEnvelope.Create("request.completed",
            new BoundaryEventSource("test", "test", "test", null), new { })));
        await writer.StopAsync(CancellationToken.None);
        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
