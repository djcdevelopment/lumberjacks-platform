using Godot;
using System.Collections.Generic;

namespace CommunitySurvival.Replay;

/// <summary>
/// Replay-mode selection bus. Holds the set of selected entityIds plus a
/// per-entity "highlight enabled" flag. Emits SelectionChanged on any state
/// change. Visual + UI consumers (HighlightRing, ProfileCard) subscribe;
/// SelectionInput is the only writer in slice 1.
/// </summary>
public partial class SelectionManager : Node
{
    private readonly HashSet<string> _selected = new();
    private readonly HashSet<string> _highlightDisabled = new();

    [Signal] public delegate void SelectionChangedEventHandler();

    public IReadOnlyCollection<string> Selected => _selected;

    public bool IsSelected(string entityId) => _selected.Contains(entityId);

    public bool IsHighlightEnabled(string entityId)
        => _selected.Contains(entityId) && !_highlightDisabled.Contains(entityId);

    public void SetSingle(string entityId)
    {
        bool changed = _selected.Count != 1 || !_selected.Contains(entityId);
        if (!changed) return;
        _selected.Clear();
        _highlightDisabled.Clear();
        _selected.Add(entityId);
        EmitSignal(SignalName.SelectionChanged);
    }

    public void ToggleAdd(string entityId)
    {
        if (!_selected.Add(entityId))
        {
            _selected.Remove(entityId);
            _highlightDisabled.Remove(entityId);
        }
        EmitSignal(SignalName.SelectionChanged);
    }

    public void Clear()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        _highlightDisabled.Clear();
        EmitSignal(SignalName.SelectionChanged);
    }

    public void SetHighlightEnabled(string entityId, bool enabled)
    {
        if (!_selected.Contains(entityId)) return;
        bool changed = enabled
            ? _highlightDisabled.Remove(entityId)
            : _highlightDisabled.Add(entityId);
        if (changed) EmitSignal(SignalName.SelectionChanged);
    }
}
