using Godot;
using System;
using System.Text;
using System.Threading.Tasks;

namespace CommunitySurvival.Lab;

/// <summary>
/// Runs N terrain generation + erosion passes with randomized parameters.
/// Outputs CSV with params → metrics for analysis.
/// Run as: var sweep = new ParameterSweep(); AddChild(sweep); sweep.Run(500);
/// </summary>
public partial class ParameterSweep : Node
{
    private bool _running;
    private int _completed;
    private int _total;
    private StringBuilder _csv;
    private Label _statusLabel;

    public override void _Ready()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _statusLabel = new Label();
        _statusLabel.AnchorLeft = 0.3f; _statusLabel.AnchorRight = 0.7f;
        _statusLabel.AnchorTop = 0.4f;
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _statusLabel.AddThemeFontSizeOverride("font_size", 20);
        _statusLabel.AddThemeColorOverride("font_color", Colors.White);
        canvas.AddChild(_statusLabel);
        _statusLabel.Text = "Press P to start parameter sweep (500 runs)";
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventKey k && k.Pressed && k.Keycode == Key.P && !_running)
        {
            _ = RunSweep(500);
        }
    }

    public override void _Process(double delta)
    {
        if (_running)
            _statusLabel.Text = $"Sweep: {_completed}/{_total} ({(_completed * 100 / Math.Max(_total, 1))}%)";
    }

    private async Task RunSweep(int runs)
    {
        _running = true;
        _total = runs;
        _completed = 0;

        _csv = new StringBuilder();
        _csv.AppendLine(
            "seed,octaves,frequency,sea_level," +
            "erosion_rate,deposition_rate,evaporation,inertia,capacity,droplet_life,erosion_iters," +
            "wind_angle,wind_strength," +
            TerrainMetrics.CsvHeader);

        var masterRng = new Random(12345);

        await Task.Run(() =>
        {
            for (int i = 0; i < runs; i++)
            {
                var sim = new TerrainSim(128);

                // Randomize params
                sim.Seed = masterRng.Next(1, 10000);
                sim.Octaves = masterRng.Next(4, 8);
                sim.Frequency = 1.5f + (float)masterRng.NextDouble() * 4f;
                sim.SeaLevel = 0.2f + (float)masterRng.NextDouble() * 0.3f;
                sim.ErosionRate = 0.01f + (float)masterRng.NextDouble() * 0.4f;
                sim.DepositionRate = 0.1f + (float)masterRng.NextDouble() * 0.7f;
                sim.Evaporation = 0.001f + (float)masterRng.NextDouble() * 0.04f;
                sim.Inertia = 0.01f + (float)masterRng.NextDouble() * 0.35f;
                sim.SedimentCapacity = 1f + (float)masterRng.NextDouble() * 7f;
                sim.DropletLifetime = masterRng.Next(15, 80);
                sim.WindAngle = (float)masterRng.NextDouble() * 360f;
                sim.WindStrength = 0.5f + (float)masterRng.NextDouble() * 1.5f;

                // Pick erosion iteration count
                int[] iterOptions = { 10000, 50000, 100000, 200000 };
                int erosionIters = iterOptions[masterRng.Next(iterOptions.Length)];

                // Run
                sim.Generate();
                sim.Erode(erosionIters);
                sim.ComputeFlow();
                sim.ComputeMoisture();
                var metrics = sim.ComputeMetrics();

                // Log
                _csv.AppendLine(
                    $"{sim.Seed},{sim.Octaves},{sim.Frequency:F2},{sim.SeaLevel:F2}," +
                    $"{sim.ErosionRate:F3},{sim.DepositionRate:F3},{sim.Evaporation:F4},{sim.Inertia:F3}," +
                    $"{sim.SedimentCapacity:F1},{sim.DropletLifetime},{erosionIters}," +
                    $"{sim.WindAngle:F1},{sim.WindStrength:F2}," +
                    metrics.ToCsv());

                _completed = i + 1;
            }
        });

        // Save CSV
        var path = "user://parameter_sweep.csv";
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        file.StoreString(_csv.ToString());
        var absPath = ProjectSettings.GlobalizePath(path);

        _running = false;
        _statusLabel.Text = $"Sweep complete! {runs} runs saved to:\n{absPath}";
        GD.Print($"ParameterSweep: {runs} runs saved to {absPath}");
    }
}
