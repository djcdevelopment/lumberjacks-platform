using Godot;
using System;
using System.Collections.Generic;

namespace CommunitySurvival.Lab;

/// <summary>
/// Collapsible tuning panel with slider sections. Toggle with assigned key.
/// Each section has a header (click to collapse) and named sliders with real-time values.
/// Mouse-driven — sliders work while panel is open without capturing game input.
///
/// Usage:
///   var panel = new TuningPanel();
///   AddChild(panel);
///   var atmo = panel.AddSection("Atmosphere");
///   atmo.AddSlider("Fog Density", 0f, 0.2f, 0.02f, v => env.VolumetricFogDensity = v);
///   atmo.AddSlider("Sun Energy", 0f, 5f, 1.4f, v => sun.LightEnergy = v);
/// </summary>
public partial class TuningPanel : CanvasLayer
{
    private PanelContainer _root;
    private VBoxContainer _container;
    private readonly List<TuningSection> _sections = new();
    private bool _visible;
    private Label _toggleHint;

    public override void _Ready()
    {
        // Semi-transparent background panel, right side of screen
        _root = new PanelContainer();
        _root.AnchorLeft = 0.0f;
        _root.AnchorRight = 0.3f;
        _root.AnchorTop = 0.0f;
        _root.AnchorBottom = 1.0f;
        _root.OffsetLeft = 0;
        _root.OffsetRight = 0;

        var style = new StyleBoxFlat();
        style.BgColor = new Color(0.05f, 0.07f, 0.05f, 0.82f);
        style.ContentMarginLeft = 8;
        style.ContentMarginRight = 8;
        style.ContentMarginTop = 8;
        style.ContentMarginBottom = 8;
        _root.AddThemeStyleboxOverride("panel", style);

        var scroll = new ScrollContainer();
        scroll.AnchorRight = 1;
        scroll.AnchorBottom = 1;
        _root.AddChild(scroll);

        _container = new VBoxContainer();
        _container.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _container.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_container);

        // Title
        var title = new Label();
        title.Text = "TUNING";
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", new Color(0.7f, 0.8f, 0.6f));
        _container.AddChild(title);

        _container.AddChild(new HSeparator());

        AddChild(_root);
        _root.Visible = false;

        // Toggle hint (always visible)
        _toggleHint = new Label();
        _toggleHint.Text = "[Tab] Tuning";
        _toggleHint.AnchorLeft = 0.01f;
        _toggleHint.AnchorTop = 0.01f;
        _toggleHint.AddThemeFontSizeOverride("font_size", 12);
        _toggleHint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.5f, 0.6f));
        AddChild(_toggleHint);
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.Tab)
        {
            _visible = !_visible;
            _root.Visible = _visible;
            _toggleHint.Visible = !_visible;
        }
    }

    public TuningSection AddSection(string title)
    {
        var section = new TuningSection(title);
        _container.AddChild(section);
        _sections.Add(section);
        return section;
    }
}

/// <summary>
/// A collapsible section within the tuning panel. Click header to expand/collapse.
/// </summary>
public partial class TuningSection : VBoxContainer
{
    private string _title;
    private VBoxContainer _content;
    private Button _header;
    private bool _collapsed;
    private readonly List<TuningSlider> _sliders = new();
    private readonly List<Control> _pendingChildren = new();

    public TuningSection(string title)
    {
        _title = title;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 2);

        _header = new Button();
        _header.Text = $"▼ {_title}";
        _header.Alignment = HorizontalAlignment.Left;
        _header.Flat = true;
        _header.AddThemeFontSizeOverride("font_size", 14);
        _header.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.7f));
        _header.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 0.8f));
        _header.Pressed += OnToggle;
        AddChild(_header);

        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 1);
        AddChild(_content);

        // Replay children added before _Ready
        foreach (var s in _sliders)
            if (s.GetParent() == null) _content.AddChild(s);
        foreach (var c in _pendingChildren)
            if (c.GetParent() == null) _content.AddChild(c);
    }

    private void OnToggle()
    {
        _collapsed = !_collapsed;
        _content.Visible = !_collapsed;
        _header.Text = _collapsed ? $"► {_title}" : $"▼ {_title}";
    }

    public TuningSlider AddSlider(string name, float min, float max, float initial, Action<float> onChange)
    {
        var slider = new TuningSlider(name, min, max, initial, onChange);
        _sliders.Add(slider);
        if (_content != null)
            _content.AddChild(slider);
        return slider;
    }

    public void AddButton(string label, Action onClick)
    {
        var btn = new Button();
        btn.Text = label;
        btn.AddThemeFontSizeOverride("font_size", 12);
        btn.Pressed += () => onClick?.Invoke();
        if (_content != null)
            _content.AddChild(btn);
        else
            _pendingChildren.Add(btn);
    }
}

/// <summary>
/// A single tunable parameter: label + HSlider + value display.
/// </summary>
public partial class TuningSlider : HBoxContainer
{
    private string _name;
    private float _min, _max, _initial;
    private Action<float> _onChange;
    private HSlider _slider;
    private Label _valueLabel;

    public float Value => (float)_slider?.Value;

    public TuningSlider(string name, float min, float max, float initial, Action<float> onChange)
    {
        _name = name;
        _min = min;
        _max = max;
        _initial = initial;
        _onChange = onChange;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 4);

        var nameLabel = new Label();
        nameLabel.Text = _name;
        nameLabel.CustomMinimumSize = new Vector2(90, 0);
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        nameLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.7f, 0.6f));
        AddChild(nameLabel);

        _slider = new HSlider();
        _slider.MinValue = _min;
        _slider.MaxValue = _max;
        _slider.Step = (_max - _min) / 200f;
        _slider.Value = _initial;
        _slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _slider.CustomMinimumSize = new Vector2(80, 0);
        _slider.ValueChanged += OnValueChanged;
        AddChild(_slider);

        _valueLabel = new Label();
        _valueLabel.Text = _initial.ToString("F3");
        _valueLabel.CustomMinimumSize = new Vector2(50, 0);
        _valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _valueLabel.AddThemeFontSizeOverride("font_size", 11);
        _valueLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.7f));
        AddChild(_valueLabel);
    }

    private void OnValueChanged(double value)
    {
        float v = (float)value;
        _valueLabel.Text = v < 10 ? v.ToString("F3") : v.ToString("F1");
        _onChange?.Invoke(v);
    }

    public void SetValue(float v)
    {
        if (_slider != null) _slider.Value = v;
    }
}
