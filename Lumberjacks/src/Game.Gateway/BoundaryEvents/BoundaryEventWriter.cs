using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Game.Gateway.BoundaryEvents;

public sealed class BoundaryEventWriter : IBoundaryEventSink, IHostedService, IDisposable
{
    private readonly BoundaryEventOptions _options;
    private readonly ILogger<BoundaryEventWriter> _logger;
    private readonly Channel<BoundaryEventEnvelope> _events;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private StreamWriter? _writer;
    private string? _openPath;
    private DateOnly _segmentDate;
    private int _segmentNumber;
    private long _segmentBytes;
    private readonly Stopwatch _flushClock = Stopwatch.StartNew();
    private readonly object _gate = new();

    private long _droppedRows;
    private long _faults;
    public long DroppedRows => Interlocked.Read(ref _droppedRows);
    public long Faults => Interlocked.Read(ref _faults);

    public BoundaryEventWriter(IOptions<BoundaryEventOptions> options, ILogger<BoundaryEventWriter> logger)
    {
        _options = options.Value;
        _logger = logger;
        _events = Channel.CreateBounded<BoundaryEventEnvelope>(new BoundedChannelOptions(Math.Max(1, _options.Capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool TryWrite(BoundaryEventEnvelope envelope)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Path)) return false;
        if (_events.Writer.TryWrite(envelope)) return true;
        Interlocked.Increment(ref _droppedRows);
        return false;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Path)) return Task.CompletedTask;
        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        if (_worker is not null) await _worker.WaitAsync(cancellationToken);
        CloseSegment();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var envelope in _events.Reader.ReadAllAsync(cancellationToken))
            {
                var line = envelope.Serialize();
                EnsureSegment(line);
                await _writer!.WriteLineAsync(line);
                _segmentBytes += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
                if (_options.FlushIntervalMilliseconds <= 0 ||
                    _flushClock.ElapsedMilliseconds >= _options.FlushIntervalMilliseconds)
                {
                    await _writer.FlushAsync();
                    _flushClock.Restart();
                }
            }
            if (_writer is not null) await _writer.FlushAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _faults);
            _logger.LogError(ex, "Boundary event writer stopped unexpectedly");
        }
    }

    private void EnsureSegment(string line)
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        if (_writer is not null && (_segmentDate != date ||
            (_segmentBytes > 0 && _segmentBytes + bytes > Math.Max(1024, _options.SegmentBytes))))
            CloseSegment();
        if (_writer is not null) return;

        var directory = Path.GetFullPath(_options.Path!);
        Directory.CreateDirectory(directory);
        _segmentDate = date;
        do
        {
            _segmentNumber++;
            _openPath = Path.Combine(directory,
                $"{date:yyyyMMdd}-{_segmentNumber:000000}.open.jsonl");
        } while (File.Exists(_openPath));
        _writer = new StreamWriter(new FileStream(_openPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read), new System.Text.UTF8Encoding(false));
        _segmentBytes = 0;
    }

    private void CloseSegment()
    {
        lock (_gate)
        {
            if (_writer is null || _openPath is null) return;
            try
            {
                _writer.Flush();
                _writer.Dispose();
                var finalPath = _openPath[..^".open.jsonl".Length] + ".jsonl";
                File.Move(_openPath, finalPath);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _faults);
                _logger.LogError(ex, "Could not close boundary event segment");
            }
            finally
            {
                _writer = null;
                _openPath = null;
                _segmentBytes = 0;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _writer?.Dispose();
        _stop.Dispose();
    }
}
