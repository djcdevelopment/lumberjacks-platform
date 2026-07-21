using Godot;
using System;

namespace CommunitySurvival.Replay;

/// <summary>
/// Slice 0 debug HUD. Four lines, top-left:
///   pull <id8>          / FAIL panel on validation error
///   mm:ss / mm:ss       /   error message line 2
///   1.0x                /   (placeholder for future scrubber)
///   schema vN
///
/// Not for end-user UX — operator visibility once multiple replay exports
/// exist on disk and you need to know which one is playing.
/// </summary>
public partial class DebugHud : CanvasLayer
{
    private Label _line1;
    private Label _line2;
    private Label _line3;
    private Label _line4;
    private ReplayLoader _loader;
    private bool _hudShownAsFailed = false;

    public override void _Ready()
    {
        // ReplayLoader is a sibling under ReplayMain.
        var parent = GetParent();
        if (parent != null && parent.HasNode("ReplayLoader"))
        {
            _loader = parent.GetNode<ReplayLoader>("ReplayLoader");
        }

        var panel = new PanelContainer { Position = new Vector2(8, 8) };
        AddChild(panel);
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.65f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        });

        var vbox = new VBoxContainer();
        panel.AddChild(vbox);

        _line1 = NewLabel(); vbox.AddChild(_line1);
        _line2 = NewLabel(); vbox.AddChild(_line2);
        _line3 = NewLabel(); vbox.AddChild(_line3);
        _line4 = NewLabel(); vbox.AddChild(_line4);
    }

    private static Label NewLabel()
    {
        return new Label
        {
            Text = "...",
            Modulate = new Color(0.95f, 0.95f, 0.95f),
        };
    }

    public override void _Process(double delta)
    {
        if (_loader == null)
        {
            _line1.Text = "no ReplayLoader";
            return;
        }

        if (_loader.LoadFailed)
        {
            if (!_hudShownAsFailed)
            {
                _line1.Modulate = new Color(1f, 0.35f, 0.35f);
                _hudShownAsFailed = true;
            }
            _line1.Text = "REPLAY FAILED";
            _line2.Text = _loader.LoadError ?? "(no error message)";
            _line3.Text = "";
            _line4.Text = $"expected schema v1";
            return;
        }

        var pid = _loader.PullId ?? "—";
        var pidShort = pid.Length >= 8 ? pid.Substring(0, 8) : pid;
        var boss = _loader.BossName ?? "(no boss)";
        _line1.Text = $"pull {pidShort} · {boss}";
        _line2.Text = $"{FormatMs(_loader.CurrentTimeMs)} / {FormatMs(_loader.DurationMs)}";
        _line3.Text = "1.0x";
        _line4.Text = $"schema {_loader.SchemaVersion ?? "?"}";
    }

    private static string FormatMs(int ms)
    {
        var totalSec = ms / 1000;
        var m = totalSec / 60;
        var s = totalSec % 60;
        return $"{m:D2}:{s:D2}";
    }
}
