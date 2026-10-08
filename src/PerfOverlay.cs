using System;
using System.Text;
using Godot;

namespace Primordium;

// Performance: frame time, the simulation thread (ms per tick, ticks per second, World.Prof stages),
// the view's steps and the HUD. F3 shows it over the map; `--perf N` prints it every N frames.
public partial class PerfOverlay : Control
{
    public Main Main;
    Label label;
    int frames;
    double frameMs, frameMax, hudMs, mainMs, renderCpu, renderGpu, setupCpu;
    bool measuring;
    readonly double[] view = new double[View3D.ProfSlots];
    readonly double[] prof0 = new double[8];
    long ticks0;
    double tickMs0, publish0, stats0;
    int publishes0, statsRuns0;
    ulong since = Time.GetTicksUsec();

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.TopLeft);
        Position = new Vector2(60, 104);
        var bg = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.72f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6 };
        label = new Label();
        label.AddThemeStyleboxOverride("normal", bg);
        label.AddThemeFontOverride("font", new SystemFont { FontNames = new[] { "Menlo", "Monaco", "Courier New" } });
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.85f, 1f, 0.85f));
        AddChild(label);
        Visible = false;
    }

    public void Reset(SimRunner sim)
    {
        frames = 0; frameMs = frameMax = hudMs = mainMs = renderCpu = renderGpu = setupCpu = 0;
        Array.Clear(view);
        Array.Copy(sim.World.Prof, prof0, 8);
        ticks0 = sim.TotalTicks; tickMs0 = sim.TotalTickMs;
        publish0 = sim.PublishMs; publishes0 = sim.Publishes;
        stats0 = sim.StatsMs; statsRuns0 = sim.StatsRuns;
        since = Time.GetTicksUsec();
        if (Main.View != null) Main.View.TilesUploaded = Main.View.BandsUploaded = 0;
    }

    // Once a frame: delta is the time since the previous frame, main the ms spent in Main._Process.
    public void Frame(double delta, double main, double hud, double[] viewProf)
    {
        frames++;
        Position = new Vector2(16 + (Main.Ui?.LeftInset ?? 0), 104);   // under the status lines, right of the sidebar
        frameMs += delta * 1000;
        frameMax = Math.Max(frameMax, delta * 1000);
        var vp = GetViewport().GetViewportRid();
        if (!measuring) { RenderingServer.ViewportSetMeasureRenderTime(vp, true); measuring = true; }
        renderCpu += RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp);
        renderGpu += RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp);
        setupCpu += RenderingServer.GetFrameSetupTimeCpu();
        mainMs += main;
        hudMs += hud;
        for (int k = 0; k < view.Length; k++) view[k] += viewProf[k];
    }

    public int Frames => frames;
    public double Seconds => (Time.GetTicksUsec() - since) / 1e6;
    public void Show(string text) => label.Text = text;

    public string Report(bool multiline)
    {
        var sim = Main.Sim;
        var w = sim.World;
        double n = Math.Max(1, frames), secs = Math.Max(1e-3, (Time.GetTicksUsec() - since) / 1e6);
        long ticks = sim.TotalTicks - ticks0;
        double tickMs = sim.TotalTickMs - tickMs0, per = Math.Max(1, ticks);
        var p = w.Prof;
        string nl = multiline ? "\n" : " | ";
        var sb = new StringBuilder();
        sb.Append($"PERF frame {frameMs / n:F1} ms (max {frameMax:F0}, {1000 * n / Math.Max(1, frameMs):F1} fps) main {mainMs / n:F2} ms · pop {w.Agents.Count:N0} tick {w.Tick:N0}");
        sb.Append(nl).Append($"sim {ticks / secs:F0} ticks/s, {tickMs / per:F2} ms/tick (target ×{sim.Tpf}{(sim.Paused ? ", paused" : "")}{(sim.FastForward ? ", fast-forward" : "")})" +
                             $" · publish {(sim.PublishMs - publish0) / Math.Max(1, sim.Publishes - publishes0):F2} ms ×{(sim.Publishes - publishes0) / secs:F0}/s" +
                             $" · stats {(sim.StatsMs - stats0) / Math.Max(1, sim.StatsRuns - statsRuns0):F1} ms ×{sim.StatsRuns - statsRuns0}");
        sb.Append(nl).Append($"world ms/tick: env {(p[0] - prof0[0]) / per:F2} (sky {(p[4] - prof0[4]) / per:F2} diff {(p[5] - prof0[5]) / per:F2} chem {(p[6] - prof0[6]) / per:F2} structure {(p[7] - prof0[7]) / per:F2}) agents {(p[1] - prof0[1]) / per:F2} merge {(p[2] - prof0[2]) / per:F2}");
        sb.Append(nl).Append("view ms/frame:");
        for (int k = 0; k < view.Length; k++) sb.Append($" {View3D.ProfNames[k]} {view[k] / n:F2}");
        sb.Append($" · hud {hudMs / n:F2} · uploads/frame: strata tiles {Main.View.TilesUploaded / n:F2}, terrain bands {Main.View.BandsUploaded / n:F2}");
        sb.Append(nl).Append($"render: cpu {renderCpu / n:F2} ms + setup {setupCpu / n:F2} ms, gpu {renderGpu / n:F2} ms · prims {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):N0} draws {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}" +
                             $" · agents drawn {Main.View.AgentsDrawn:N0}{(Main.View.FarLod ? " (far LOD)" : "")} · vmem {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576:F0} MB" +
                             $" · GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} heap {GC.GetTotalMemory(false) / 1048576} MB");
        return sb.ToString();
    }
}
