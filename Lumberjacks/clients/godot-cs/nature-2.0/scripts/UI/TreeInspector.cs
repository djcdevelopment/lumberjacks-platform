using Godot;
using System;
using System.Text.Json;
using CommunitySurvival.Core;

namespace CommunitySurvival.UI;

/// <summary>
/// Shows tree data when player is near and presses E.
/// "Staying in one place increases depth of understanding."
/// </summary>
public partial class TreeInspector : CanvasLayer
{
    private GameState _state;
    private Panel _panel;
    private Label _titleLabel;
    private Label _detailLabel;
    private Label _promptLabel;
    private string _nearestTreeId;
    private const float InspectRange = 8f;

    public override void _Ready()
    {
        _state = GetNode<GameState>("/root/GameState");
        BuildUI();
        _panel.Hide();
    }

    public override void _Process(double delta)
    {
        if (_state.MyPlayerId == null) return;
        var playerPos = _state.GetPosition(_state.MyPlayerId);

        // Find nearest tree
        _nearestTreeId = _state.FindNearestTree(playerPos, InspectRange);

        if (_nearestTreeId != null)
        {
            _promptLabel.Text = "[E] Study";
            _promptLabel.Show();
        }
        else
        {
            _promptLabel.Hide();
            _panel.Hide();
        }
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.E && _nearestTreeId != null)
        {
            ShowInspection(_nearestTreeId);
        }
        else if (ev is InputEventKey k2 && k2.Pressed && k2.Keycode == Key.Escape && _panel.Visible)
        {
            _panel.Hide();
        }
    }

    private void ShowInspection(string entityId)
    {
        var data = _state.GetEntityMeta(entityId);
        if (data == null) { _panel.Hide(); return; }

        var lines = new System.Text.StringBuilder();

        // Health
        double health = data.ContainsKey("health") ? (double)data["health"] : 100;
        if (health >= 80) lines.AppendLine("Condition: Healthy, standing tall");
        else if (health >= 40) lines.AppendLine($"Condition: Wounded — deep cuts in the bark ({health:F0}%)");
        else if (health > 0) lines.AppendLine($"Condition: Barely standing ({health:F0}%)");
        else lines.AppendLine("Condition: Felled");

        // Growth history
        if (data.ContainsKey("growth_history"))
        {
            try
            {
                using var doc = JsonDocument.Parse((string)data["growth_history"]);
                var gh = doc.RootElement;

                if (gh.TryGetProperty("age_years", out var age))
                {
                    int years = int.Parse(age.GetString() ?? "100");
                    if (years > 150) lines.AppendLine($"Age: Ancient — roughly {years} years of growth");
                    else if (years > 80) lines.AppendLine($"Age: Mature — about {years} years");
                    else lines.AppendLine($"Age: Young — perhaps {years} years");
                }

                if (gh.TryGetProperty("twist", out var twist))
                {
                    float tw = float.Parse(twist.GetString() ?? "0");
                    if (Math.Abs(tw) > 1.0) lines.AppendLine("Wind: Heavily shaped by prevailing winds — trunk twists visibly");
                    else if (Math.Abs(tw) > 0.3) lines.AppendLine("Wind: Slight lean from years of wind exposure");
                    else lines.AppendLine("Wind: Grew in a sheltered spot — straight trunk");
                }

                if (gh.TryGetProperty("fire_scars", out var fire))
                {
                    if (fire.GetString() == "True")
                        lines.AppendLine("History: Bark scarred from a past fire — survived and grew back");
                }
            }
            catch { }
        }

        // Lean (from chopping)
        double leanX = data.ContainsKey("lean_x") ? (double)data["lean_x"] : 0;
        double leanZ = data.ContainsKey("lean_z") ? (double)data["lean_z"] : 0;
        if (Math.Abs(leanX) > 0.5 || Math.Abs(leanZ) > 0.5)
            lines.AppendLine("Lean: Noticeably leaning from axe strikes");

        // Regrowth
        double regrowth = data.ContainsKey("regrowth_progress") ? (double)data["regrowth_progress"] : 0;
        if (regrowth > 0 && regrowth < 1)
            lines.AppendLine($"Regrowth: A sapling is emerging ({regrowth * 100:F0}%)");

        _titleLabel.Text = "Oak Tree";
        _detailLabel.Text = lines.ToString().TrimEnd();
        _panel.Show();
    }

    private void BuildUI()
    {
        // Prompt label (bottom center)
        _promptLabel = new Label();
        _promptLabel.Text = "[E] Study";
        _promptLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _promptLabel.AnchorLeft = 0.5f; _promptLabel.AnchorRight = 0.5f;
        _promptLabel.AnchorTop = 0.85f; _promptLabel.AnchorBottom = 0.85f;
        _promptLabel.OffsetLeft = -60; _promptLabel.OffsetRight = 60;
        _promptLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.7f));
        _promptLabel.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_promptLabel);

        // Inspection panel (right side)
        _panel = new Panel();
        _panel.AnchorLeft = 0.65f; _panel.AnchorRight = 0.98f;
        _panel.AnchorTop = 0.1f; _panel.AnchorBottom = 0.6f;
        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = new Color(0.08f, 0.1f, 0.08f, 0.85f);
        panelStyle.CornerRadiusTopLeft = 4;
        panelStyle.CornerRadiusTopRight = 4;
        panelStyle.CornerRadiusBottomLeft = 4;
        panelStyle.CornerRadiusBottomRight = 4;
        panelStyle.ContentMarginLeft = 16;
        panelStyle.ContentMarginRight = 16;
        panelStyle.ContentMarginTop = 12;
        panelStyle.ContentMarginBottom = 12;
        _panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(_panel);

        var vbox = new VBoxContainer();
        vbox.AnchorRight = 1; vbox.AnchorBottom = 1;
        vbox.OffsetLeft = 16; vbox.OffsetRight = -16;
        vbox.OffsetTop = 12; vbox.OffsetBottom = -12;
        _panel.AddChild(vbox);

        _titleLabel = new Label();
        _titleLabel.Text = "Tree";
        _titleLabel.AddThemeFontSizeOverride("font_size", 22);
        _titleLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.9f, 0.7f));
        vbox.AddChild(_titleLabel);

        var sep = new HSeparator();
        vbox.AddChild(sep);

        _detailLabel = new Label();
        _detailLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detailLabel.AddThemeFontSizeOverride("font_size", 14);
        _detailLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.7f));
        vbox.AddChild(_detailLabel);
    }
}
