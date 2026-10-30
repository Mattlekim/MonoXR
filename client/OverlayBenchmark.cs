using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MonoXR.Client;

public enum BenchmarkState { Idle, Running, Finished, Cancelled, Failed }

/// <summary>One measured quantity over a benchmark phase.</summary>
public sealed class BenchmarkSeries
{
    private readonly List<double> _values = new();

    public string Label { get; }
    public string Unit { get; }
    public bool LowerIsBetter { get; }

    public BenchmarkSeries(string label, string unit, bool lowerIsBetter)
    {
        Label = label; Unit = unit; LowerIsBetter = lowerIsBetter;
    }

    /// <summary>Add a reading; NaN means "could not be measured this time" and keeps the rows aligned.</summary>
    public void Add(double v) => _values.Add(double.IsFinite(v) ? v : double.NaN);

    /// <summary>Every reading in order, NaN where there was none.</summary>
    public IReadOnlyList<double> Values => _values;

    private List<double> Finite() => _values.Where(double.IsFinite).ToList();

    public bool HasData => _values.Any(double.IsFinite);
    public double Average => Finite() is { Count: > 0 } f ? f.Average() : double.NaN;
    public double Max => Finite() is { Count: > 0 } f ? f.Max() : double.NaN;

    public double P95
    {
        get
        {
            var f = Finite();
            if (f.Count == 0) return double.NaN;
            f.Sort();
            return f[Math.Clamp((int)Math.Ceiling(f.Count * 0.95) - 1, 0, f.Count - 1)];
        }
    }
}

/// <summary>Everything measured while the overlay ran in one mode.</summary>
public sealed class BenchmarkPhase
{
    public string Name { get; }
    public string Key { get; }
    public bool PerWindow { get; }
    public bool Stereo { get; }
    public double Seconds { get; internal set; }
    public int Frames { get; internal set; }
    public Dictionary<string, BenchmarkSeries> Series { get; } = new();

    internal BenchmarkPhase(string name, string key, bool perWindow, bool stereo)
    {
        Name = name; Key = key; PerWindow = perWindow; Stereo = stereo;
        foreach (var m in OverlayBenchmark.Metrics)
            Series[m.Key] = new BenchmarkSeries(m.Label, m.Unit, m.LowerIsBetter);
    }
}

/// <summary>
/// Measures what one overlay per window, and then the 3D option (an overlay per eye),
/// cost against the single canvas.
/// Runs the overlay in each of the three modes for <see cref="PhaseDuration"/> (the first
/// <see cref="Warmup"/> of each is thrown away, the layer and the GPU need a
/// moment to settle after a switch), samples GPU load and GPU memory once a
/// second, then writes a comparison.
///
/// Drive it by wrapping the host's per-tick call:
/// <c>benchmark.RunFrame(() => host.RenderFrame(t))</c>. While it runs it also
/// refreshes one window per tick (round-robin), so there is always something to
/// publish and the two modes do the same amount of drawing.
///
/// GPU load and memory come from the Windows "GPU Engine" / "GPU Process Memory"
/// performance counters (the same numbers Task Manager shows), per process: this
/// app, and the VR game the layer runs in when there is one. Whole-GPU numbers
/// can come from <see cref="SystemGpuSampler"/> (e.g. NVML) instead.
/// </summary>
public sealed class OverlayBenchmark : IDisposable
{
    internal static readonly (string Key, string Label, string Unit, bool LowerIsBetter)[] Metrics =
    {
        ("frame",     "Frame time (render + publish, this app)", "ms",   true),
        ("appGpu",    "GPU usage, this app",                      "%",    true),
        ("gameGpu",   "GPU usage, VR game",                       "%",    true),
        ("totalGpu",  "GPU usage, whole GPU",                     "%",    true),
        ("appMem",    "GPU memory, this app",                     "MB",   true),
        ("gameMem",   "GPU memory, VR game",                      "MB",   true),
        ("totalMem",  "GPU memory, whole GPU",                    "MB",   true),
        ("ovMem",     "Overlay textures + targets (this app)",    "MB",   true),
        ("overlays",  "OpenXR overlays (layers)",                 "",     true),
        ("mpix",      "Pixels copied per second",                 "Mpix/s", true),
    };

    private const double MB = 1024.0 * 1024.0;

    private readonly MonoGameOverlayHost _host;
    private readonly Stopwatch _phaseClock = new();
    private readonly Stopwatch _frameClock = new();
    private const int PhaseCount = 3;
    private readonly BenchmarkPhase?[] _phases = new BenchmarkPhase?[PhaseCount];
    private bool _originalStereo;
    private float _originalStereoDepth;
    private GpuCounterReader? _gpu;
    private int _phase;
    private TimeSpan _lastSample;
    private long _lastPixels;
    private int _stressIndex;
    private bool _originalPerWindow;
    private bool _gameSeen;
    private int _gamePid;

    /// <summary>How long each of the three modes is measured for (default 2 minutes).</summary>
    public TimeSpan PhaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Time at the start of each phase that is run but not measured.</summary>
    public TimeSpan Warmup { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Where the report (.txt) and the per-second samples (.csv) are written; null keeps them in memory only.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Optional whole-GPU reading (percent busy, MB in use), used instead of the
    /// performance counters' totals when it returns a value. Called from the UI
    /// thread about once a second, so it must be quick (read a cached value).
    /// </summary>
    public Func<(double UsagePercent, double MemoryMb)?>? SystemGpuSampler { get; set; }

    /// <summary>True while the layer is running inside a VR app, so that app's GPU numbers can be measured.</summary>
    public bool GameAttached => _host.LayerAttached;

    public BenchmarkState State { get; private set; } = BenchmarkState.Idle;
    public bool IsRunning => State == BenchmarkState.Running;

    /// <summary>One line on what it is doing, with the time left.</summary>
    public string Status { get; private set; } = "";

    /// <summary>0 to 1 over all the phases.</summary>
    public double Progress { get; private set; }

    /// <summary>The comparison, once <see cref="State"/> is Finished.</summary>
    public string? Report { get; private set; }

    /// <summary>Path of the saved report, if <see cref="OutputDirectory"/> was set.</summary>
    public string? ReportPath { get; private set; }

    /// <summary>Why it stopped, when <see cref="State"/> is Failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised (on the thread that called <see cref="RunFrame"/>) when the run ends, in any state.</summary>
    public event Action? Completed;

    public OverlayBenchmark(MonoGameOverlayHost host) => _host = host;

    /// <summary>Why a benchmark cannot start right now, or null when it can.</summary>
    public string? CannotStartReason
    {
        get
        {
            if (State == BenchmarkState.Running) return "A benchmark is already running.";
            if (_host.PreviewActive) return "Close the desktop preview first: it keeps the canvas alive in both modes, which hides the difference.";
            if (_host.PerWindowUnavailableReason is { } why) return $"One overlay per window cannot be used right now: {why}.";
            if (!_host.RenderTargets.Any(rt => rt.Visible && !rt.IsSkipped(false))) return "There are no visible windows to measure.";
            return null;
        }
    }

    public bool TryStart(out string error)
    {
        error = CannotStartReason ?? "";
        if (error.Length > 0) return false;

        _originalPerWindow = _host.PerWindowOverlays;
        _originalStereo = _host.Stereo3D;
        _originalStereoDepth = _host.StereoDepth;
        Array.Clear(_phases);
        _gameSeen = false;
        _gamePid = 0;
        Report = ReportPath = Error = null;
        _gpu?.Dispose();
        _gpu = new GpuCounterReader();
        _phase = 0;
        State = BenchmarkState.Running;
        BeginPhase();
        return true;
    }

    /// <summary>Stop now and put the overlay back the way it was.</summary>
    public void Cancel()
    {
        if (State != BenchmarkState.Running) return;
        End(BenchmarkState.Cancelled, "Cancelled.");
    }

    /// <summary>Run one tick of the host through the benchmark. Calls <paramref name="renderFrame"/> exactly once.</summary>
    public void RunFrame(Action renderFrame)
    {
        if (State != BenchmarkState.Running) { renderFrame(); return; }

        RefreshOneWindow();

        long before = _host.FramesProcessed;
        _frameClock.Restart();
        renderFrame();
        double ms = _frameClock.Elapsed.TotalMilliseconds;
        bool processed = _host.FramesProcessed != before;

        var phase = _phases[_phase]!;
        var elapsed = _phaseClock.Elapsed;
        bool measuring = elapsed >= Warmup;
        if (measuring && processed)
        {
            phase.Series["frame"].Add(ms);
            phase.Frames++;
        }

        if (elapsed - _lastSample >= TimeSpan.FromSeconds(1))
        {
            double dt = (elapsed - _lastSample).TotalSeconds;
            _lastSample = elapsed;

            // The mode must really be what this phase says: leaving per-window
            // (edit mode, too many windows) or never entering it would make the
            // two phases the same thing.
            if (_host.EditMode)
            {
                End(BenchmarkState.Failed, "Stopped: edit mode was switched on while the benchmark ran.");
                return;
            }
            if (elapsed > TimeSpan.FromSeconds(3) && _host.PerWindowActive != phase.PerWindow)
            {
                End(BenchmarkState.Failed, phase.PerWindow
                    ? $"Stopped: the overlay left one-overlay-per-window mode ({_host.PerWindowUnavailableReason ?? "unknown reason"})."
                    : "Stopped: the overlay did not stay in single-canvas mode.");
                return;
            }
            if (elapsed > TimeSpan.FromSeconds(3) && phase.Stereo && _host.StereoWindowCount == 0)
            {
                End(BenchmarkState.Failed, "Stopped: no window was shown as an overlay per eye (no overlay slots left for the second eye?).");
                return;
            }

            _gamePid = _host.LayerAttached ? (int)_host.LayerProcessId : 0;
            if (_gamePid != 0) _gameSeen = true;
            if (_gpu != null) _gpu.GamePid = _gamePid;

            if (measuring) TakeSample(phase, dt);
            else _lastPixels = _host.PublishedPixels;

            double total = PhaseCount * PhaseDuration.TotalSeconds;
            double done = _phase * PhaseDuration.TotalSeconds + Math.Min(elapsed.TotalSeconds, PhaseDuration.TotalSeconds);
            Progress = Math.Clamp(done / total, 0, 1);
            var left = PhaseDuration - elapsed;
            Status = $"Phase {_phase + 1} of {PhaseCount}: {phase.Name}" + (measuring ? "" : " (warming up)") +
                     $" - {Math.Max(0, (int)left.TotalSeconds)} s left";
        }

        if (elapsed >= PhaseDuration) EndPhase();
    }

    private void BeginPhase()
    {
        _phases[_phase] = _phase switch
        {
            0 => new BenchmarkPhase("Single canvas", "canvas", false, false),
            1 => new BenchmarkPhase("One overlay per window", "per-window", true, false),
            _ => new BenchmarkPhase("Per window + 3D (overlay per eye)", "per-window-3d", true, true),
        };
        var phase = _phases[_phase]!;
        _host.PerWindowOverlays = phase.PerWindow;
        _host.Stereo3D = phase.Stereo;
        // A window only gets a pair of overlays when it has some depth to show.
        if (phase.Stereo && MathF.Abs(_originalStereoDepth) < 0.01f) _host.StereoDepth = 0.2f;
        _phaseClock.Restart();
        _lastSample = TimeSpan.Zero;
        _lastPixels = _host.PublishedPixels;
        Status = $"Phase {_phase + 1} of {PhaseCount}: {phase.Name} (warming up)";
    }

    private void EndPhase()
    {
        var phase = _phases[_phase]!;
        phase.Seconds = Math.Max(0, (_phaseClock.Elapsed - Warmup).TotalSeconds);
        if (_phase < PhaseCount - 1)
        {
            _phase++;
            BeginPhase();
            return;
        }
        Report = BuildReport();
        if (OutputDirectory is { Length: > 0 } dir) ReportPath = Save(dir);
        Progress = 1;
        End(BenchmarkState.Finished, "Done.");
    }

    private void End(BenchmarkState state, string message)
    {
        _host.PerWindowOverlays = _originalPerWindow;
        _host.Stereo3D = _originalStereo;
        _host.StereoDepth = _originalStereoDepth;
        _gpu?.Dispose();
        _gpu = null;
        State = state;
        Status = message;
        if (state == BenchmarkState.Failed) Error = message;
        Completed?.Invoke();
    }

    // Something has to change every tick or both modes sit idle and cost nothing.
    // One window per tick, in turn: the case one overlay per window exists for.
    private void RefreshOneWindow()
    {
        var list = _host.RenderTargets;
        int n = list.Count;
        for (int k = 0; k < n; k++)
        {
            var rt = list[_stressIndex++ % n];
            if (!rt.Visible || rt.IsSkipped(false)) continue;
            rt.Invalidate();
            break;
        }
    }

    private void TakeSample(BenchmarkPhase phase, double dt)
    {
        var s = phase.Series;
        var g = _gpu?.Latest;

        long pixels = _host.PublishedPixels;
        s["mpix"].Add((pixels - _lastPixels) / 1e6 / Math.Max(dt, 1e-3));
        _lastPixels = pixels;
        s["overlays"].Add(_host.ActiveOverlayCount);
        s["ovMem"].Add(_host.OverlayGpuBytes / MB);

        bool ok = g is { Valid: true };
        s["appGpu"].Add(ok ? g!.ClientPct : double.NaN);
        s["appMem"].Add(ok ? g!.ClientMb : double.NaN);
        s["gameGpu"].Add(ok && _gamePid != 0 ? g!.GamePct : double.NaN);
        s["gameMem"].Add(ok && _gamePid != 0 ? g!.GameMb : double.NaN);

        double totalGpu = ok ? g!.TotalPct : double.NaN, totalMem = ok ? g!.TotalMb : double.NaN;
        try
        {
            if (SystemGpuSampler?.Invoke() is { } sys) { totalGpu = sys.UsagePercent; totalMem = sys.MemoryMb; }
        }
        catch (Exception) { /* keep the counter values */ }
        s["totalGpu"].Add(totalGpu);
        s["totalMem"].Add(totalMem);
    }

    // ----------------------------------------------------------------- report

    private string BuildReport()
    {
        var a = _phases[0]!;   // single canvas
        var b = _phases[1]!;   // one overlay per window
        var c = _phases[2]!;   // ... and 3D
        var sb = new StringBuilder();

        sb.AppendLine("OVERLAY BENCHMARK: single canvas vs one overlay per window vs per window + 3D");
        sb.AppendLine(new string('=', 100));
        sb.AppendLine($"Date:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows:    {_host.RenderTargets.Count(rt => rt.Visible && !rt.IsSkipped(false))} visible, canvas {_host.CanvasWidth} x {_host.CanvasHeight} px");
        sb.AppendLine($"Each mode:  {PhaseDuration.TotalSeconds:0} s (first {Warmup.TotalSeconds:0} s not counted)");
        sb.AppendLine("Workload:   one window is refreshed every tick, in turn");
        sb.AppendLine($"3D:         every window {_host.StereoDepth.ToString("0.##", CultureInfo.InvariantCulture)} m out of its frame, {c.Series["overlays"].Average:0} overlays in total");
        sb.AppendLine(_gameSeen
            ? "VR game:    attached, its GPU numbers are included"
            : "VR game:    NOT attached. Only this app's cost was measured; start the VR game with the layer for the game's GPU numbers.");
        sb.AppendLine();

        // The headline: the rows people look at first.
        string[] headline = { "gameMem", "appMem", "totalMem", "gameGpu", "appGpu", "totalGpu", "frame", "mpix" };
        AppendSummary(sb, "ONE OVERLAY PER WINDOW vs SINGLE CANVAS", a, b, headline);
        AppendSummary(sb, "3D (AN OVERLAY PER EYE) vs ONE OVERLAY PER WINDOW: what 3D adds", b, c, headline);

        sb.AppendLine("DETAILS (averages over the measured time)");
        sb.AppendLine(new string('-', 100));
        sb.AppendLine($"{"",-40}{"Canvas",10}{"Per-window",12}{"Per-win+3D",12}{"Per-win vs canvas",20}{"3D vs per-win",18}");
        foreach (var m in Metrics)
        {
            var sa = a.Series[m.Key];
            var sb2 = b.Series[m.Key];
            var sc = c.Series[m.Key];
            if (!sa.HasData && !sb2.HasData && !sc.HasData)
            {
                sb.AppendLine($"{m.Label,-40}{"n/a",10}{"n/a",12}{"n/a",12}");
                continue;
            }
            double va = sa.Average, vb = sb2.Average, vc = sc.Average;
            sb.AppendLine($"{m.Label,-40}{Fmt(va),10}{Fmt(vb),12}{Fmt(vc),12}{Change(va, vb),20}{Change(vb, vc),18}  {m.Unit}");
        }
        sb.AppendLine();
        sb.AppendLine("Frame time spread (ms)        average     95th %        worst");
        foreach (var p in new[] { a, b, c })
        {
            var f = p.Series["frame"];
            sb.AppendLine($"  {p.Name,-34}{Fmt(f.Average),9}{Fmt(f.P95),11}{Fmt(f.Max),13}   ({p.Frames} frames)");
        }
        sb.AppendLine();
        sb.AppendLine("PEAKS");
        sb.AppendLine(new string('-', 100));
        foreach (var key in new[] { "appGpu", "gameGpu", "appMem", "gameMem" })
        {
            var m = Metrics.First(x => x.Key == key);
            if (!a.Series[key].HasData && !b.Series[key].HasData && !c.Series[key].HasData) continue;
            sb.AppendLine($"{m.Label,-40}{Fmt(a.Series[key].Max),10}{Fmt(b.Series[key].Max),12}{Fmt(c.Series[key].Max),12}  {m.Unit}");
        }
        sb.AppendLine();
        sb.AppendLine("NOTES");
        sb.AppendLine(new string('-', 100));
        sb.AppendLine("- The modes run in this order, so the GPU may be a little warmer for the later ones.");
        sb.AppendLine("  Differences under about 3% are noise. Run it again to check the result is repeatable.");
        sb.AppendLine("- 'GPU memory' is the dedicated memory Windows attributes to the process. The VR game's figure");
        sb.AppendLine("  includes the layer's copy of each overlay, so it moves with the overlay sizes too.");
        sb.AppendLine("- A game that changes its own memory use while this runs (loading a track) skews that row.");
        sb.AppendLine("- Workload: only one window changes per tick. If your windows all refresh every frame, the saving");
        sb.AppendLine("  in copying shrinks; the memory saving does not.");
        sb.AppendLine("- 3D gives each window one overlay per eye (two shared textures, two layers, two copies per change),");
        sb.AppendLine("  so it costs about double what a flat window does. That is what the second comparison shows.");
        return sb.ToString();
    }

    private static void AppendSummary(StringBuilder sb, string title, BenchmarkPhase from, BenchmarkPhase to, string[] keys)
    {
        sb.AppendLine(title);
        sb.AppendLine(new string('-', 100));
        var lines = keys.Select(k => Describe(from, to, k)).Where(l => l != null).ToList();
        if (lines.Count == 0) lines.Add("No GPU counters could be read, so only frame time and copy volume are compared.");
        foreach (var l in lines) sb.AppendLine(l);
        sb.AppendLine();
    }

    // "GPU memory, VR game: 412 -> 301 MB (-27%, lower is better)"
    private static string? Describe(BenchmarkPhase a, BenchmarkPhase b, string key)
    {
        var m = Metrics.First(x => x.Key == key);
        var sa = a.Series[key];
        var sb = b.Series[key];
        if (!sa.HasData || !sb.HasData) return null;
        double va = sa.Average, vb = sb.Average;
        string unit = m.Unit.Length > 0 ? " " + m.Unit : "";
        double pct = va > 1e-9 ? (vb - va) / va * 100.0 : 0.0;
        string verdict = Math.Abs(pct) < 3.0 ? "about the same"
            : (pct < 0) == m.LowerIsBetter ? $"{Math.Abs(pct):0}% {(pct < 0 ? "lower" : "higher")}, better"
            : $"{Math.Abs(pct):0}% {(pct < 0 ? "lower" : "higher")}, worse";
        return $"{m.Label}: {Fmt(va)} -> {Fmt(vb)}{unit} ({verdict})";
    }

    private static string Fmt(double v) =>
        double.IsFinite(v) ? v.ToString(Math.Abs(v) >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) : "n/a";

    private static string Change(double a, double b)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b)) return "";
        double d = b - a;
        if (Math.Abs(d) < 0.05) d = 0;
        string abs = (d >= 0 ? "+" : "") + Fmt(d);
        if (a > 1e-9) abs += $" ({(d / a * 100.0):+0;-0;0}%)";
        return abs;
    }

    private string Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, $"overlay-benchmark-{DateTime.Now:yyyyMMdd-HHmmss}");
            File.WriteAllText(stem + ".txt", Report!);

            var csv = new StringBuilder();
            csv.Append("mode,second");
            foreach (var m in Metrics.Where(m => m.Key != "frame")) csv.Append(',').Append(m.Key);
            csv.AppendLine();
            foreach (var p in _phases)
            {
                int rows = p!.Series["overlays"].Values.Count;
                for (int i = 0; i < rows; i++)
                {
                    csv.Append(p.Key).Append(',').Append(i + 1);
                    foreach (var m in Metrics.Where(m => m.Key != "frame"))
                    {
                        double v = p.Series[m.Key].Values[i];
                        csv.Append(',').Append(double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "");
                    }
                    csv.AppendLine();
                }
            }
            File.WriteAllText(stem + ".csv", csv.ToString());
            return stem + ".txt";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public void Dispose()
    {
        if (State == BenchmarkState.Running) Cancel();
        _gpu?.Dispose();
        _gpu = null;
    }
}

/// <summary>
/// Reads GPU load and dedicated GPU memory per process from the Windows
/// performance counters ("GPU Engine", "GPU Process Memory", "GPU Adapter
/// Memory") once a second on a background timer. Load follows Task Manager: for a
/// process, its busiest engine type (3D, Copy, ...).
/// </summary>
internal sealed class GpuCounterReader : IDisposable
{
    internal sealed class Reading
    {
        public bool Valid;
        public string? Error;
        public double ClientPct, GamePct, TotalPct, ClientMb, GameMb, TotalMb;
    }

    private const double MB = 1024.0 * 1024.0;

    private readonly Timer _timer;
    private readonly int _clientPid = Environment.ProcessId;
    private int _gamePid;
    private int _busy;
    private volatile Reading _latest = new() { Error = "no reading yet" };
    private Dictionary<string, System.Diagnostics.CounterSample> _previous = new();

    public int GamePid
    {
        get => Volatile.Read(ref _gamePid);
        set => Volatile.Write(ref _gamePid, value);
    }

    public Reading Latest => _latest;

    public GpuCounterReader()
    {
        _timer = new Timer(_ => Poll(), null, 0, 1000);
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { _latest = Read(); }
        catch (Exception ex) { _latest = new Reading { Error = ex.Message }; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private Reading Read()
    {
        int game = GamePid;

        // ---- load: Utilization Percentage per engine, grouped by process and engine type
        var engine = new PerformanceCounterCategory("GPU Engine").ReadCategory();
        if (!engine.Contains("Utilization Percentage"))
            return new Reading { Error = "GPU Engine counters are not available on this system" };

        var perPidType = new Dictionary<(int Pid, string Type), double>();
        var perType = new Dictionary<string, double>();
        var current = new Dictionary<string, CounterSample>();
        foreach (InstanceData d in engine["Utilization Percentage"].Values)
        {
            string name = d.InstanceName;
            current[name] = d.Sample;
            if (!_previous.TryGetValue(name, out var before)) continue;
            double pct = CounterSample.Calculate(before, d.Sample);
            if (!(pct > 0)) continue;
            string type = EngineType(name);
            perType[type] = perType.GetValueOrDefault(type) + pct;
            if (PidOf(name) is int pid)
                perPidType[(pid, type)] = perPidType.GetValueOrDefault((pid, type)) + pct;
        }
        _previous = current;

        double Busiest(int pid)
        {
            double best = 0;
            foreach (var kv in perPidType)
                if (kv.Key.Pid == pid) best = Math.Max(best, kv.Value);
            return Math.Min(best, 100.0);
        }

        // ---- memory: dedicated usage, bytes
        double clientBytes = 0, gameBytes = 0, adapterBytes = 0;
        var procMem = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
        if (procMem.Contains("Dedicated Usage"))
            foreach (InstanceData d in procMem["Dedicated Usage"].Values)
            {
                if (PidOf(d.InstanceName) is not int pid) continue;
                if (pid == _clientPid) clientBytes += d.Sample.RawValue;
                else if (pid == game) gameBytes += d.Sample.RawValue;
            }
        var adapterMem = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
        if (adapterMem.Contains("Dedicated Usage"))
            foreach (InstanceData d in adapterMem["Dedicated Usage"].Values)
                adapterBytes += d.Sample.RawValue;

        return new Reading
        {
            Valid = true,
            ClientPct = Busiest(_clientPid),
            GamePct = game != 0 ? Busiest(game) : 0,
            TotalPct = perType.Count > 0 ? Math.Min(perType.Values.Max(), 100.0) : 0,
            ClientMb = clientBytes / MB,
            GameMb = gameBytes / MB,
            TotalMb = adapterBytes / MB,
        };
    }

    // "pid_1234_luid_0x00000000_0x0000AB12_phys_0_eng_0_engtype_3D"
    private static int? PidOf(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return null;
        int end = instance.IndexOf('_', 4);
        if (end < 0) end = instance.Length;
        return int.TryParse(instance.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : null;
    }

    private static string EngineType(string instance)
    {
        int i = instance.IndexOf("engtype_", StringComparison.Ordinal);
        return i < 0 ? "?" : instance.Substring(i + "engtype_".Length);
    }

    public void Dispose() => _timer.Dispose();
}
