using System.Collections.Concurrent;

namespace Game.Gateway.Valheim;

public sealed record ValheimZdoInjectionCommand
{
    public string? CommandId { get; init; }
    public string? Action { get; init; }
    public string? Prefab { get; init; }
    public long? UidUser { get; init; }
    public long? UidId { get; init; }
    public long? Owner { get; init; }
    public int? OwnerRev { get; init; }
    public long? DataRev { get; init; }
    public double[]? Pos { get; init; }
}

public sealed record ValheimZdoInjectionStageRequest
{
    public string? WindowId { get; init; }
    public ValheimZdoInjectionCommand? Command { get; init; }
}

public sealed record ValheimZdoInjectionAckRequest
{
    public string? WindowId { get; init; }
    public string? CommandId { get; init; }
    public string? ClientId { get; init; }
    public bool Applied { get; init; }
    public bool Rendered { get; init; }
    public string? Reason { get; init; }
    public long? ObservedOwner { get; init; }
}

public sealed record ValheimZdoInjectionAck(
    string ClientId,
    bool Applied,
    bool Rendered,
    string Reason,
    long? ObservedOwner,
    DateTime UpdatedUtc);

public sealed record ValheimZdoInjectionWindowStatus(
    string WindowId,
    int Commands,
    long Polls,
    DateTime? FirstStagedUtc,
    DateTime? LastStagedUtc,
    IReadOnlyDictionary<string, ValheimZdoInjectionAck> Acks);

public sealed class ValheimZdoInjectionService
{
    public const int MaxCommandsPerWindow = 32;
    public const long MaxUidId = uint.MaxValue;
    public const long MaxDataRevision = uint.MaxValue;

    private readonly ConcurrentDictionary<string, Window> _windows =
        new(StringComparer.Ordinal);

    public (bool Ok, bool Duplicate, string Error) Stage(
        string windowId,
        ValheimZdoInjectionCommand command)
    {
        var error = ValidateWindowId(windowId) ?? ValidateCommand(command);
        if (error is not null)
            return (false, false, error);

        var window = _windows.GetOrAdd(windowId, static _ => new Window());
        return window.Stage(command);
    }

    public (bool Ok, string Error, IReadOnlyList<ValheimZdoInjectionCommand> Commands) Poll(
        string windowId,
        string clientId)
    {
        var error = ValidateWindowId(windowId) ?? ValidateToken(clientId, "client_id");
        if (error is not null)
            return (false, error, Array.Empty<ValheimZdoInjectionCommand>());
        if (!_windows.TryGetValue(windowId, out var window))
            return (true, string.Empty, Array.Empty<ValheimZdoInjectionCommand>());
        return (true, string.Empty, window.Poll(clientId));
    }

    public (bool Ok, string Error) Ack(ValheimZdoInjectionAckRequest request)
    {
        var error = ValidateWindowId(request.WindowId)
            ?? ValidateToken(request.CommandId, "command_id")
            ?? ValidateToken(request.ClientId, "client_id");
        if (error is not null)
            return (false, error);
        if (!_windows.TryGetValue(request.WindowId!, out var window))
            return (false, "window_id is unknown");
        return window.Ack(request);
    }

    public ValheimZdoInjectionWindowStatus GetStatus(string windowId) =>
        _windows.TryGetValue(windowId, out var window)
            ? window.Status(windowId)
            : new(windowId, 0, 0, null, null,
                new Dictionary<string, ValheimZdoInjectionAck>());

    public IReadOnlyList<ValheimZdoInjectionWindowStatus> GetAllStatuses() =>
        _windows.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value.Status(kv.Key)).ToList();

    public bool Reset(string windowId) => _windows.TryRemove(windowId, out _);

    public int ResetAll()
    {
        var count = _windows.Count;
        _windows.Clear();
        return count;
    }

    public static string? ValidateCommand(ValheimZdoInjectionCommand? command)
    {
        if (command is null)
            return "command is required";
        var tokenError = ValidateToken(command.CommandId, "command_id");
        if (tokenError is not null)
            return tokenError;
        if (!string.Equals(command.Action, "upsert", StringComparison.Ordinal))
            return "action must be 'upsert'";
        if (string.IsNullOrWhiteSpace(command.Prefab) || command.Prefab.Length > 128
            || command.Prefab.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
            return "prefab must be a 1-128 character prefab name";
        if (command.UidUser is null or 0 || command.Owner is null or 0)
            return "uid_user and owner must be non-zero";
        if (command.UidUser != command.Owner)
            return "uid_user must equal owner for the synthetic authority namespace";
        if (command.UidId is null or <= 0 or > MaxUidId)
            return $"uid_id must be in 1..{MaxUidId}";
        if (command.OwnerRev is null or <= 0 or > ushort.MaxValue)
            return $"owner_rev must be in 1..{ushort.MaxValue}";
        if (command.DataRev is null or <= 0 or > MaxDataRevision)
            return $"data_rev must be in 1..{MaxDataRevision}";
        if (command.Pos is null || command.Pos.Length != 3 || command.Pos.Any(v => !double.IsFinite(v)))
            return "pos must contain three finite numbers";
        if (Math.Abs(command.Pos[0]) > 20_000 || command.Pos[1] is < -500 or > 2_000
            || Math.Abs(command.Pos[2]) > 20_000)
            return "pos is outside the bounded Valheim test world envelope";
        return null;
    }

    private static string? ValidateWindowId(string? value) => ValidateToken(value, "window_id");

    private static string? ValidateToken(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            return $"{name} must use 1-128 letters, digits, '.', '_' or '-'";
        return null;
    }

    private sealed class Window
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ValheimZdoInjectionCommand> _commands =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ValheimZdoInjectionAck> _acks =
            new(StringComparer.Ordinal);
        private long _polls;
        private DateTime? _firstStagedUtc;
        private DateTime? _lastStagedUtc;

        public (bool Ok, bool Duplicate, string Error) Stage(ValheimZdoInjectionCommand command)
        {
            lock (_gate)
            {
                if (_commands.ContainsKey(command.CommandId!))
                    return (true, true, string.Empty);
                if (_commands.Count >= MaxCommandsPerWindow)
                    return (false, false, $"window command cap ({MaxCommandsPerWindow}) reached");
                _commands.Add(command.CommandId!, command);
                var now = DateTime.UtcNow;
                _firstStagedUtc ??= now;
                _lastStagedUtc = now;
                return (true, false, string.Empty);
            }
        }

        public IReadOnlyList<ValheimZdoInjectionCommand> Poll(string clientId)
        {
            lock (_gate)
            {
                _polls++;
                return _commands.Values
                    .Where(c => !_acks.ContainsKey(AckKey(c.CommandId!, clientId)))
                    .ToList();
            }
        }

        public (bool Ok, string Error) Ack(ValheimZdoInjectionAckRequest request)
        {
            lock (_gate)
            {
                if (!_commands.ContainsKey(request.CommandId!))
                    return (false, "command_id is unknown in this window");
                _acks[AckKey(request.CommandId!, request.ClientId!)] = new(
                    request.ClientId!, request.Applied, request.Rendered,
                    request.Reason?.Trim() ?? string.Empty, request.ObservedOwner, DateTime.UtcNow);
                return (true, string.Empty);
            }
        }

        public ValheimZdoInjectionWindowStatus Status(string windowId)
        {
            lock (_gate)
            {
                return new(windowId, _commands.Count, _polls, _firstStagedUtc, _lastStagedUtc,
                    new Dictionary<string, ValheimZdoInjectionAck>(_acks));
            }
        }

        private static string AckKey(string commandId, string clientId) => commandId + "@" + clientId;
    }
}
