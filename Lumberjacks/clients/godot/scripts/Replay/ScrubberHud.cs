using Godot;

namespace CommunitySurvival.Replay;

/// <summary>
/// Bottom-bar playback control. Layout: [status] [speed] [scrub bar] [time].
///
/// Keyboard: ↑ play/pause, → speed up, ← speed down. Speed steps are
/// powers of two from 0.25× to 4×. Drag the scrub bar to seek — playback
/// auto-pauses while dragging and resumes on release.
/// </summary>
public partial class ScrubberHud : CanvasLayer
{
    private static readonly float[] SpeedSteps = { 0.25f, 0.5f, 1f, 2f, 4f };

    private ReplayLoader _loader;
    private HSlider _slider;
    private Label _timeLabel;
    private Label _speedLabel;
    private Label _statusLabel;
    private int _speedIdx = 2;
    private bool _scrubbing = false;
    private bool _wasPausedBeforeScrub = false;

    public override void _Ready()
    {
        _loader = GetParent().GetNodeOrNull<ReplayLoader>("ReplayLoader");

        var panel = new PanelContainer
        {
            AnchorLeft = 0,
            AnchorRight = 1,
            AnchorTop = 1,
            AnchorBottom = 1,
            OffsetLeft = 16,
            OffsetRight = -16,
            OffsetTop = -56,
            OffsetBottom = -16,
        };
        AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.07f, 0.85f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        });

        var hbox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddChild(hbox);

        _statusLabel = new Label { Text = "▶", CustomMinimumSize = new Vector2(20, 0) };
        hbox.AddChild(_statusLabel);

        _speedLabel = new Label { Text = "1x", CustomMinimumSize = new Vector2(46, 0) };
        hbox.AddChild(_speedLabel);

        _slider = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.0001,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        hbox.AddChild(_slider);
        _slider.DragStarted += OnDragStarted;
        _slider.DragEnded += OnDragEnded;
        _slider.ValueChanged += OnValueChanged;

        _timeLabel = new Label { Text = "00:00 / 00:00", CustomMinimumSize = new Vector2(110, 0) };
        hbox.AddChild(_timeLabel);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_loader == null) return;
        if (@event is not InputEventKey ek || !ek.Pressed || ek.Echo) return;

        switch (ek.Keycode)
        {
            case Key.Up:    _loader.TogglePaused(); break;
            case Key.Right: ChangeSpeed(+1); break;
            case Key.Left:  ChangeSpeed(-1); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    private void ChangeSpeed(int delta)
    {
        var newIdx = Mathf.Clamp(_speedIdx + delta, 0, SpeedSteps.Length - 1);
        if (newIdx == _speedIdx) return;
        _speedIdx = newIdx;
        var s = SpeedSteps[_speedIdx];
        _loader.SetSpeed(s);
        _speedLabel.Text = s < 1f ? $"{s:0.##}x" : $"{s:0.#}x";
    }

    private void OnDragStarted()
    {
        _scrubbing = true;
        _wasPausedBeforeScrub = _loader.IsPaused;
        _loader.SetPaused(true);
    }

    private void OnDragEnded(bool valueChanged)
    {
        if (_loader.DurationMs > 0)
        {
            var ms = (int)(_slider.Value * _loader.DurationMs);
            _loader.SeekToMs(ms);
        }
        _loader.SetPaused(_wasPausedBeforeScrub);
        _scrubbing = false;
    }

    private void OnValueChanged(double v)
    {
        if (_scrubbing && _loader != null && _loader.DurationMs > 0)
        {
            _loader.SeekToMs((int)(v * _loader.DurationMs));
        }
    }

    public override void _Process(double delta)
    {
        if (_loader == null) return;
        if (!_scrubbing && _loader.DurationMs > 0)
        {
            _slider.SetValueNoSignal((double)_loader.CurrentTimeMs / _loader.DurationMs);
        }
        _timeLabel.Text = $"{FormatMs(_loader.CurrentTimeMs)} / {FormatMs(_loader.DurationMs)}";
        _statusLabel.Text = _loader.IsPaused ? "❚❚" : "▶";
    }

    private static string FormatMs(int ms)
    {
        var sec = ms / 1000;
        return $"{sec / 60:D2}:{sec % 60:D2}";
    }
}
