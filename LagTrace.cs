using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Rocket.API;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace LagTrace
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Design note — why no stack sampler
    //
    //  Thread.Suspend() deadlocks on Mono when the main thread holds the GC lock
    //  (which it does frequently mid-frame). Reading StackTrace without Suspend is
    //  a data race — you get 1-2 garbage frames. Both approaches fail on Mono.
    //  Harmony instrumentation gives us exact ms/call/max with no threading risk.
    // ─────────────────────────────────────────────────────────────────────────────

    public class LagTracePlugin : RocketPlugin<LagTraceConfig>
    {
        public static LagTracePlugin Instance { get; private set; }
        public static readonly SpikeDetector Spikes = new SpikeDetector();

        private Harmony _harmony;

        protected override void Load()
        {
            Instance = this;
            _harmony = new Harmony("com.lagtrace");
            gameObject.AddComponent<FrameTimingComponent>();
            HarmonyPatcher.PatchAll(_harmony);

            if (Configuration.Instance.AutoPrint)
                InvokeRepeating(nameof(OnFlushWindow),
                    Configuration.Instance.WindowSeconds,
                    Configuration.Instance.WindowSeconds);

            Logger.Log("[LagTrace] Loaded. Commands: /lag  /lagtop  /lagplugins  /lagspike  /lagreset");
        }

        protected override void Unload()
        {
            if (Configuration.Instance.AutoPrint)
                CancelInvoke(nameof(OnFlushWindow));
            _harmony?.UnpatchAll("com.lagtrace");
            Logger.Log("[LagTrace] Unloaded.");
        }

        private void OnFlushWindow()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[LagTrace] Top methods ──");
            foreach (var e in Timings.GetTop(Configuration.Instance.TopN))
                sb.AppendLine($"  {e.Name}  {e.TotalMs:F2}ms  avg {e.AvgMs:F3}ms  x{e.Calls}");
            Logger.Log(sb.ToString());
        }

        public static void OnFrameComplete(float deltaMs) => Spikes.Feed(deltaMs);
    }

    public class LagTraceConfig : IRocketPluginConfiguration
    {
        public int   WindowSeconds    = 60;
        public float SpikeThresholdMs = 50f;
        public bool  AutoPrint        = false;
        public int   TopN             = 10;

        public void LoadDefaults()
        {
            WindowSeconds    = 60;
            SpikeThresholdMs = 50f;
            AutoPrint        = false;
            TopN             = 10;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Timings
    // ─────────────────────────────────────────────────────────────────────────────
    public static class Timings
    {
        private static readonly Dictionary<string, Bucket> _b = new Dictionary<string, Bucket>(128);
        private static readonly ReaderWriterLockSlim _rwl = new ReaderWriterLockSlim();

        public static void Record(string name, long ticks)
        {
            _rwl.EnterWriteLock();
            try
            {
                if (!_b.TryGetValue(name, out var b)) _b[name] = b = new Bucket();
                b.TotalTicks += ticks;
                b.Calls++;
                if (ticks > b.MaxTicks) b.MaxTicks = ticks;
            }
            finally { _rwl.ExitWriteLock(); }
        }

        public static IDisposable Start(string name) => new Scope(name);

        public static List<TimingEntry> GetTop(int n, bool byAvg = false)
        {
            _rwl.EnterReadLock();
            try
            {
                return _b.Select(kv =>
                {
                    double tot = kv.Value.TotalTicks * 1000.0 / Stopwatch.Frequency;
                    double avg = kv.Value.Calls > 0 ? tot / kv.Value.Calls : 0;
                    double max = kv.Value.MaxTicks * 1000.0 / Stopwatch.Frequency;
                    return new TimingEntry { Name = kv.Key, TotalMs = tot, AvgMs = avg, MaxMs = max, Calls = kv.Value.Calls };
                })
                .OrderByDescending(e => byAvg ? e.AvgMs : e.TotalMs)
                .Take(n).ToList();
            }
            finally { _rwl.ExitReadLock(); }
        }

        // Aggregate method timings up to per-plugin totals using the label registry.
        public static List<PluginEntry> GetPluginTotals(int n, TrackerCategory? filter = null)
        {
            _rwl.EnterReadLock();
            Dictionary<string, Bucket> snap;
            try { snap = new Dictionary<string, Bucket>(_b); }
            finally { _rwl.ExitReadLock(); }

            var totals  = new Dictionary<string, PluginAcc>(32);
            double grandMs = 0;

            foreach (var kv in snap)
            {
                double ms = kv.Value.TotalTicks * 1000.0 / Stopwatch.Frequency;
                grandMs += ms;

                if (!HarmonyPatcher.TryGetLabel(kv.Key, out var label, out var cat)) continue;
                if (filter.HasValue && cat != filter.Value) continue;

                if (!totals.TryGetValue(label, out var acc))
                    totals[label] = acc = new PluginAcc { Cat = cat };
                acc.Ms    += ms;
                acc.Calls += kv.Value.Calls;
            }

            if (grandMs <= 0) return new List<PluginEntry>();

            return totals
                .Select(kv => new PluginEntry
                {
                    Label    = kv.Key,
                    TotalMs  = kv.Value.Ms,
                    Calls    = kv.Value.Calls,
                    Pct      = kv.Value.Ms * 100.0 / grandMs,
                    Category = kv.Value.Cat,
                })
                .OrderByDescending(e => e.TotalMs)
                .Take(n).ToList();
        }

        public static void Reset()
        {
            _rwl.EnterWriteLock();
            try { _b.Clear(); }
            finally { _rwl.ExitWriteLock(); }
        }

        private class Bucket { public long TotalTicks, MaxTicks; public int Calls; }
        private class PluginAcc { public double Ms; public long Calls; public TrackerCategory Cat; }

        private class Scope : IDisposable
        {
            private readonly string _n; private readonly Stopwatch _sw;
            public Scope(string n) { _n = n; _sw = Stopwatch.StartNew(); }
            public void Dispose() { _sw.Stop(); Record(_n, _sw.ElapsedTicks); }
        }
    }

    public class TimingEntry
    {
        public string Name; public double TotalMs, AvgMs, MaxMs; public int Calls;
    }

    public class PluginEntry
    {
        public string Label; public double TotalMs, Pct; public long Calls; public TrackerCategory Category;
    }

    public enum TrackerCategory { Plugin, Engine, Core }

    // ─────────────────────────────────────────────────────────────────────────────
    //  HarmonyPatcher
    // ─────────────────────────────────────────────────────────────────────────────
    public static class HarmonyPatcher
    {
        private static readonly string[] UnturnedManagerTypeNames =
        {
            "SDG.Unturned.VehicleManager",
            "SDG.Unturned.AnimalManager",
            "SDG.Unturned.ZombieManager",
            "SDG.Unturned.BarricadeManager",
            "SDG.Unturned.StructureManager",
            "SDG.Unturned.ResourceManager",
            "SDG.Unturned.ObjectManager",
            "SDG.Unturned.ItemManager",
            "SDG.Unturned.LightingManager",
            "SDG.Unturned.WeatherEventListenerManager",
            "SDG.Unturned.ClaimManager",
            "SDG.Unturned.DamageTool",
            "SDG.Unturned.PlayerMovement",
            "SDG.Unturned.PlayerLife",
            "SDG.Unturned.PlayerAnimator",
            "SDG.Unturned.InteractableVehicle",
            "SDG.Unturned.UseableGun",
            "SDG.Unturned.UseableMelee",
        };

        private static readonly string[] TargetMethods = { "FixedUpdate", "Update", "LateUpdate" };

        // Maps "TypeName.MethodName" → (display label, category) for /lagplugins aggregation.
        private static readonly Dictionary<string, (string label, TrackerCategory cat)> _labels
            = new Dictionary<string, (string, TrackerCategory)>(256);

        public static bool TryGetLabel(string key, out string label, out TrackerCategory cat)
        {
            if (_labels.TryGetValue(key, out var t)) { label = t.label; cat = t.cat; return true; }
            label = null; cat = TrackerCategory.Core; return false;
        }

        public static void PatchAll(Harmony h)
        {
            // Rocket plugins
            foreach (var plugin in Rocket.Core.R.Plugins.GetPlugins())
            {
                if (plugin.GetType().Assembly == typeof(LagTracePlugin).Assembly) continue;
                foreach (var m in TargetMethods)
                    TryPatch(h, plugin.GetType(), m, plugin.Name, TrackerCategory.Plugin);
            }

            // Unturned engine managers
            var asmCs = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asmCs == null) { Logger.Log("[LagTrace] Assembly-CSharp not found."); return; }

            foreach (var typeName in UnturnedManagerTypeNames)
            {
                var t = asmCs.GetType(typeName);
                if (t == null) continue;
                foreach (var m in TargetMethods)
                    TryPatch(h, t, m, $"[Engine] {t.Name}", TrackerCategory.Engine);
            }

            Logger.Log($"[LagTrace] Instrumented {_labels.Count} methods.");
        }

        private static void TryPatch(Harmony h, Type t, string method,
                                     string label, TrackerCategory cat)
        {
            var mi = t.GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null) return;
            try
            {
                h.Patch(mi,
                    prefix:  new HarmonyMethod(typeof(HarmonyPatcher), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(HarmonyPatcher), nameof(Postfix)));
                _labels[$"{t.Name}.{method}"] = (label, cat);
            }
            catch (Exception ex)
            {
                Logger.Log($"[LagTrace] Cannot patch {t.Name}.{method}: {ex.Message}");
            }
        }

        // These must be public so Harmony can invoke them across assemblies.
        public static void Prefix(ref object __state) => __state = Stopwatch.StartNew();

        public static void Postfix(MethodBase __originalMethod, object __state)
        {
            if (!(__state is Stopwatch sw)) return;
            sw.Stop();
            Timings.Record($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}",
                           sw.ElapsedTicks);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  SpikeDetector — safe because Feed() is called from LateUpdate (main thread).
    //  StackTrace() without Suspend() is fine here — we ARE the thread.
    // ─────────────────────────────────────────────────────────────────────────────
    public class SpikeDetector
    {
        private const int MaxSpikes = 20;
        private readonly SpikeRecord[] _ring = new SpikeRecord[MaxSpikes];
        private int _head, _count;
        private readonly object _lock = new object();

        public void Feed(float frameMs)
        {
            if (frameMs < LagTracePlugin.Instance.Configuration.Instance.SpikeThresholdMs) return;

            // Safe: we're already on the main thread, no Suspend needed.
            string[] frames;
            try
            {
                var st = new StackTrace(false);
                int depth = Math.Min(st.FrameCount, 40);
                frames = new string[depth];
                for (int i = 0; i < depth; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    frames[i] = m != null ? $"{m.DeclaringType?.FullName}.{m.Name}" : "?";
                }
            }
            catch { frames = Array.Empty<string>(); }

            lock (_lock)
            {
                _ring[_head % MaxSpikes] = new SpikeRecord
                    { TimestampUtc = DateTime.UtcNow, FrameMs = frameMs, Stack = frames };
                _head++;
                _count = Math.Min(_count + 1, MaxSpikes);
            }
        }

        public SpikeRecord GetLast()
        {
            lock (_lock) { return _count == 0 ? null : _ring[(_head - 1 + MaxSpikes) % MaxSpikes]; }
        }

        public List<SpikeRecord> GetAll()
        {
            lock (_lock)
            {
                var list = new List<SpikeRecord>(_count);
                for (int i = 0; i < _count; i++)
                    list.Add(_ring[(_head - 1 - i + MaxSpikes * 2) % MaxSpikes]);
                return list;
            }
        }
    }

    public class SpikeRecord { public DateTime TimestampUtc; public float FrameMs; public string[] Stack; }

    // ─────────────────────────────────────────────────────────────────────────────
    //  FrameTimingComponent
    // ─────────────────────────────────────────────────────────────────────────────
    public class FrameTimingComponent : MonoBehaviour
    {
        private float _lastTime;

        private void LateUpdate()
        {
            float now   = Time.realtimeSinceStartup;
            float delta = (now - _lastTime) * 1000f;
            _lastTime   = now;
            if (delta > 0f) LagTracePlugin.OnFrameComplete(delta);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Commands
    // ─────────────────────────────────────────────────────────────────────────────

    public class CommandLag : IRocketCommand
    {
        public string Name => "lag"; public string Help => "TPS + top 5 methods.";
        public string Syntax => "/lag"; public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "lagtrace.lag" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            var top = Timings.GetTop(5);
            var sb  = new StringBuilder();
            sb.AppendLine($"[LagTrace] TPS: {1f / Time.smoothDeltaTime:F1}  Frame: {Time.smoothDeltaTime * 1000f:F1}ms");
            if (top.Count == 0) sb.AppendLine("  (no data yet)");
            else foreach (var e in top)
                sb.AppendLine($"  {Trunc(e.Name, 52)}  {e.TotalMs:F2}ms  avg {e.AvgMs:F3}ms  x{e.Calls}");
            Reply(caller, sb.ToString());
        }
    }

    public class CommandLagTop : IRocketCommand
    {
        public string Name => "lagtop"; public string Help => "Top N methods. Args: [n] [avg]";
        public string Syntax => "/lagtop [n] [avg]"; public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string> { "lt" };
        public List<string> Permissions => new List<string> { "lagtrace.lagtop" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = LagTracePlugin.Instance.Configuration.Instance.TopN;
            bool byAvg = false;
            foreach (var a in args)
            {
                if (int.TryParse(a, out int p)) n = Math.Max(1, Math.Min(p, 50));
                if (a.Equals("avg", StringComparison.OrdinalIgnoreCase)) byAvg = true;
            }
            var entries = Timings.GetTop(n, byAvg);
            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] Top {n} ({(byAvg ? "avg" : "total")}):");
            if (entries.Count == 0) sb.AppendLine("  (no data yet)");
            else foreach (var e in entries)
                sb.AppendLine($"  {Trunc(e.Name, 46)}  {e.TotalMs:F2}ms  avg {e.AvgMs:F3}ms  max {e.MaxMs:F2}ms  x{e.Calls}");
            Reply(caller, sb.ToString());
        }
    }

    public class CommandLagPlugins : IRocketCommand
    {
        public string Name => "lagplugins"; public string Help => "CPU per plugin. Args: [n] [engine|plugins]";
        public string Syntax => "/lagplugins [n] [engine|plugins]"; public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string> { "lp" };
        public List<string> Permissions => new List<string> { "lagtrace.lagplugins" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = 20; TrackerCategory? filter = null;
            foreach (var a in args)
            {
                if (int.TryParse(a, out int p)) n = Math.Max(1, Math.Min(p, 50));
                else if (a.Equals("engine",  StringComparison.OrdinalIgnoreCase)) filter = TrackerCategory.Engine;
                else if (a.Equals("plugins", StringComparison.OrdinalIgnoreCase)) filter = TrackerCategory.Plugin;
            }
            var entries = Timings.GetPluginTotals(n, filter);
            if (entries.Count == 0) { Reply(caller, "[LagTrace] No data yet."); return; }

            var sb = new StringBuilder();
            string fl = filter == null ? "all" : filter == TrackerCategory.Engine ? "engine" : "plugins";
            sb.AppendLine($"[LagTrace] CPU by source ({fl}, top {n}):");

            if (filter == null)
            {
                double eMs = entries.Where(e => e.Category == TrackerCategory.Engine).Sum(e => e.TotalMs);
                double pMs = entries.Where(e => e.Category == TrackerCategory.Plugin).Sum(e => e.TotalMs);
                sb.AppendLine($"  Summary  [Engine] {eMs:F0}ms   [Plugins] {pMs:F0}ms");
                sb.AppendLine();
            }

            TrackerCategory? lastCat = null;
            foreach (var e in entries.OrderBy(e => e.Category).ThenByDescending(e => e.TotalMs))
            {
                if (e.Category != lastCat)
                {
                    sb.AppendLine(e.Category == TrackerCategory.Engine ? "  ── Unturned engine ──" : "  ── Rocket plugins ──");
                    lastCat = e.Category;
                }
                int f = (int)Math.Round(Math.Min(e.Pct, 100.0) / 5.0);
                string bar = new string('\u2588', f) + new string('\u2591', 20 - f);
                sb.AppendLine($"  {bar}  {e.Pct,5:F1}%  {e.TotalMs,7:F0}ms  {Trunc(e.Label, 32)}  x{e.Calls}");
            }
            Reply(caller, sb.ToString());
        }
    }

    public class CommandLagSpike : IRocketCommand
    {
        public string Name => "lagspike"; public string Help => "Last spike stack. 'list' for all recent.";
        public string Syntax => "/lagspike [list]"; public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string> { "ls" };
        public List<string> Permissions => new List<string> { "lagtrace.lagspike" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            bool list = args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase);
            if (list)
            {
                var all = LagTracePlugin.Spikes.GetAll();
                if (all.Count == 0) { Reply(caller, "[LagTrace] No spikes yet."); return; }
                var sb = new StringBuilder();
                sb.AppendLine("[LagTrace] Recent spikes:");
                foreach (var s in all) sb.AppendLine($"  {s.TimestampUtc:HH:mm:ss}  {s.FrameMs:F1}ms");
                Reply(caller, sb.ToString());
                return;
            }
            var spike = LagTracePlugin.Spikes.GetLast();
            if (spike == null) { Reply(caller, "[LagTrace] No spikes yet."); return; }
            var out2 = new StringBuilder();
            out2.AppendLine($"[LagTrace] Spike {spike.TimestampUtc:HH:mm:ss} UTC  {spike.FrameMs:F1}ms");
            out2.AppendLine("  Stack (top = leaf):");
            int shown = Math.Min(spike.Stack.Length, 20);
            for (int i = 0; i < shown; i++)
                out2.AppendLine($"  [{i,2}] {Trunc(spike.Stack[i], 70)}");
            if (spike.Stack.Length > shown)
                out2.AppendLine($"  ... +{spike.Stack.Length - shown} frames");
            Reply(caller, out2.ToString());
        }
    }

    public class CommandLagReset : IRocketCommand
    {
        public string Name => "lagreset"; public string Help => "Reset all timing data.";
        public string Syntax => "/lagreset"; public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "lagtrace.reset" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            Timings.Reset();
            Reply(caller, "[LagTrace] Data cleared.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────────────────────────────────────
    internal static class CommandHelpers
    {
        public static void Reply(IRocketPlayer player, string message)
        {
            if (player is ConsolePlayer)
                Logger.Log(message);
            else
                Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    Rocket.Unturned.Chat.UnturnedChat.Say(player, message, Color.cyan));
        }

        public static string Truncate(string s, int max) =>
            s == null ? "" : s.Length <= max ? s : "\u2026" + s.Substring(s.Length - (max - 1));
    }

    public abstract class CommandBase
    {
        protected static void Reply(IRocketPlayer p, string msg) => CommandHelpers.Reply(p, msg);
        protected static string Trunc(string s, int n) => CommandHelpers.Truncate(s, n);
    }
}
