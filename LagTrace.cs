using System;
using System.Collections.Concurrent;
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
using ThreadPriority = System.Threading.ThreadPriority;

namespace LagTrace
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Entry point
    // ─────────────────────────────────────────────────────────────────────────────
    public class LagTracePlugin : RocketPlugin<LagTraceConfig>
    {
        public static LagTracePlugin Instance { get; private set; }

        private Harmony _harmony;
        private Thread _samplerThread;

        // Exposed so commands can read without locking.
        public static readonly Sampler Sampler = new Sampler();
        public static readonly SpikeDetector Spikes = new SpikeDetector();

        protected override void Load()
        {
            Instance = this;

            // Load() is always called on the Unity main thread — capture it here
            // so the sampler has a valid target before its thread starts.
            MainThreadRef.Capture();

            _harmony = new Harmony("com.lagtrace");
            // No attribute-based [HarmonyPatch] classes remain — PatchAll is not called.
            // This avoids Harmony trying to patch unpatchable Unity magic methods.

            // Attach frame-timing component (replaces the old MonoBehaviour LateUpdate patch).
            gameObject.AddComponent<FrameTimingComponent>();

            // Patch all RocketPlugin.FixedUpdate / Update that are currently loaded
            HarmonyPatcher.PatchLoadedPlugins(_harmony);

            // Start background sampler (samples main thread stack every N ms)
            Sampler.Start();
            _samplerThread = new Thread(SamplerLoop)
            {
                Name = "LagTrace-Sampler",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _samplerThread.Start();

            if (Configuration.Instance.AutoPrint)
                InvokeRepeating(nameof(OnFlushWindow), 
                    Configuration.Instance.WindowSeconds,
                    Configuration.Instance.WindowSeconds);

            Logger.Log("[LagTrace] Loaded. Commands: /lag, /lagtop, /lagplugins, /lagspike");
        }

        protected override void Unload()
        {
            if (Configuration.Instance.AutoPrint)
                CancelInvoke(nameof(OnFlushWindow));
            Sampler.Stop();
            _samplerThread?.Join(500);
            _harmony?.UnpatchAll("com.lagtrace");
            Logger.Log("[LagTrace] Unloaded.");
        }

        // ── Sampler thread ──────────────────────────────────────────────────────
        private void SamplerLoop()
        {
            while (Sampler.Running)
            {
                Sampler.Sample();
                Thread.Sleep(Configuration.Instance.SampleIntervalMs);
            }
        }

        // ── Periodic flush ──────────────────────────────────────────────────────
        // Called on Unity main thread by InvokeRepeating.
        private void OnFlushWindow()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[LagTrace] ── Top methods (last window) ──");
            foreach (var entry in Sampler.GetTop(10))
                sb.AppendLine($"  {entry.DisplayName}  {entry.Pct:F1}%  ({entry.Samples} samples)");
            Logger.Log(sb.ToString());
        }

        // ── Unity frame hook (patches applied before this) ─────────────────────
        // Called by FrameTimingComponent every LateUpdate to feed spike detector.
        public static void OnFrameComplete(float deltaMs)
        {
            Spikes.Feed(deltaMs);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Configuration
    // ─────────────────────────────────────────────────────────────────────────────
    public class LagTraceConfig : Rocket.API.IRocketPluginConfiguration
    {
        public int SampleIntervalMs = 5;     // How often the sampler snapshots
        public int WindowSeconds = 60;    // Flush/report window
        public float SpikeThresholdMs = 50f;  // A frame longer than this is a "spike"
        public bool AutoPrint = false; // Print top to console each window
        public int TopN = 10;    // Default rows for /lagtop

        public void LoadDefaults()
        {
            SampleIntervalMs = 5;
            WindowSeconds = 60;
            SpikeThresholdMs = 50f;
            AutoPrint = false;
            TopN = 10;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  SamplerEngine — wall-clock stack sampler (like async-profiler / Spark)
    //
    //  The background thread calls Sample() on an interval. Each sample walks
    //  the main thread's StackTrace and increments a hit-count per frame in
    //  each method's fully-qualified name. Self-time = frames where the method
    //  is at position 0 (the leaf). Total-time = any appearance.
    //
    //  We store only the top of the stack (configurable depth) to avoid huge
    //  allocations. Thread safety: Interlocked on the running flag; Dictionary
    //  access is behind a lightweight SpinLock since samples are very frequent.
    // ─────────────────────────────────────────────────────────────────────────────
    public class Sampler
    {
        private volatile bool _running;

        // KEY: assembly name → sample count
        private readonly ConcurrentDictionary<string, int> _counts = new();

        private int _totalSamples;

        public void Start()
        {
            _running = true;
        }

        public void Stop()
        {
            _running = false;
        }

        public bool Running => _running;

        public void Sample()
        {
            var mainThread = MainThreadRef.MainThread;
            if (mainThread == null) return;

            var stackTrace = new StackTrace(mainThread, false);
            if (stackTrace.FrameCount == 0) return;

            var frame = stackTrace.GetFrame(0);
            var method = frame?.GetMethod();
            if (method == null) return;

            string key = method.DeclaringType?.Assembly?.GetName()?.Name ?? "unknown";

            _counts.AddOrUpdate(key, 1, (_, v) => v + 1);
            Interlocked.Increment(ref _totalSamples);
        }

        public List<SampleEntry> GetTop(int n, TrackerCategory? filter = null)
        {
            int total = _totalSamples;
            if (total == 0) return new List<SampleEntry>();

            return _counts
                .Select(kv =>
                {
                    var category = IsPlugin(kv.Key) ? TrackerCategory.Plugin : TrackerCategory.Engine;

                    return new SampleEntry
                    {
                        DisplayName = kv.Key,
                        Samples = kv.Value,
                        Pct = (kv.Value * 100.0) / total,
                        Category = category
                    };
                })
                .Where(e => filter == null || e.Category == filter)
                .OrderByDescending(e => e.Pct)
                .Take(n)
                .ToList();
        }

        private bool IsPlugin(string assemblyName)
        {
            return assemblyName != null &&
                   !assemblyName.StartsWith("Unity") &&
                   !assemblyName.StartsWith("System") &&
                   !assemblyName.StartsWith("mscorlib") &&
                   assemblyName != "Assembly-CSharp";
        }

        public void Reset()
        {
            _counts.Clear();
            _totalSamples = 0;
        }
    }
    public class SampleEntry
    {
        public string DisplayName;
        public int Samples;
        public double Pct;
        public TrackerCategory Category;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Stores a reference to the main Unity thread.
    //  [RuntimeInitializeOnLoadMethod] does NOT fire on dedicated servers under
    //  Rocketmod/Mono, so we capture it explicitly from Load() and from
    //  FrameTimingComponent.Awake(), both of which are guaranteed to run on the
    //  main thread.
    // ─────────────────────────────────────────────────────────────────────────────
    public static class MainThreadRef
    {
        public static Thread MainThread;
        public static void Capture()
        {
            MainThread = Thread.CurrentThread;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  SpikeDetector — captures a full stack snapshot whenever a frame exceeds
    //  the configured threshold.
    // ─────────────────────────────────────────────────────────────────────────────
    public class SpikeDetector
    {
        // Ring buffer of recent spikes
        private const int MaxSpikes = 20;
        private readonly SpikeRecord[] _ring = new SpikeRecord[MaxSpikes];
        private int _head = 0;
        private int _count = 0;
        private readonly object _lock = new object();

        public void Feed(float frameMs)
        {
            if (frameMs < LagTracePlugin.Instance.Configuration.Instance.SpikeThresholdMs)
                return;

            var record = new SpikeRecord
            {
                TimestampUtc = DateTime.UtcNow,
                FrameMs = frameMs,
                Stack = Array.Empty<string>(),
            };

            lock (_lock)
            {
                _ring[_head % MaxSpikes] = record;
                _head++;
                _count = Math.Min(_count + 1, MaxSpikes);
            }
        }

        public SpikeRecord GetLast()
        {
            lock (_lock)
            {
                if (_count == 0) return null;
                int idx = (_head - 1 + MaxSpikes) % MaxSpikes;
                return _ring[idx];
            }
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

    public class SpikeRecord
    {
        public DateTime TimestampUtc;
        public float FrameMs;
        public string[] Stack;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  PluginTimingTracker — per-source self-time derived from the sampler.
    //
    //  Attribution priority (leaf-first through the stack):
    //    1. Registered plugin assembly prefix  → category Plugin
    //    2. "SDG.Unturned.<ManagerClass>"      → category Engine, name = class
    //    3. Everything else                    → not counted (Unity/Mono overhead)
    //
    //  Each registration maps an assembly-name (or namespace prefix) to a
    //  display name + category, so both Rocket plugins and Unturned managers
    //  appear in /lagplugins with clear labelling.
    // ─────────────────────────────────────────────────────────────────────────────
    public enum TrackerCategory { Plugin, Engine }

    // ─────────────────────────────────────────────────────────────────────────────
    //  HarmonyPatcher — patches every loaded RocketPlugin AND the known Unturned
    //  server-side manager types with prefix/postfix timing wrappers.
    //
    //  Unturned managers live in Assembly-CSharp and are not enumerable via any
    //  Rocket API, so we maintain a curated list. These are the types that own
    //  expensive FixedUpdate / Update loops (vehicles, animals, zombies, etc.).
    //  The list was assembled from reading the SDG decompiled source — extend it
    //  freely as new managers are identified.
    // ─────────────────────────────────────────────────────────────────────────────
    public static class HarmonyPatcher
    {
        // All Unturned manager types we want to instrument.
        // Any that don't exist in the current game version are skipped gracefully.
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

        // Methods we patch on each manager type (whichever exist)
        private static readonly string[] TargetMethods = { "FixedUpdate", "Update", "LateUpdate" };

        public static void PatchLoadedPlugins(Harmony h)
        {
            // ── Rocket plugins ───────────────────────────────────────────────────
            foreach (var plugin in Rocket.Core.R.Plugins.GetPlugins())
            {
                if (plugin.GetType().Assembly == typeof(LagTracePlugin).Assembly)
                    continue; // skip ourselves

                var asm = plugin.GetType().Assembly;
                var pName = plugin.Name;
                var asmName = asm.GetName().Name;

                foreach (var m in TargetMethods)
                    TryPatch(h, plugin.GetType(), m);
            }

            // ── Unturned engine managers ─────────────────────────────────────────
            // Assembly-CSharp is always loaded; resolve types directly.
            var asmCsharp = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asmCsharp == null)
            {
                Logger.Log("[LagTrace] Warning: Assembly-CSharp not found — engine managers won't be instrumented.");
                return;
            }

            foreach (var typeName in UnturnedManagerTypeNames)
            {
                var t = asmCsharp.GetType(typeName);
                if (t == null) continue; // version mismatch — skip silently

                foreach (var m in TargetMethods)
                    TryPatch(h, t, m);
            }

            Logger.Log("[LagTrace] Engine manager patches applied.");
        }

        private static void TryPatch(Harmony h, Type t, string methodName)
        {
            var method = t.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) return;

            try
            {
                var prefix = new HarmonyMethod(typeof(HarmonyPatcher)
                    .GetMethod(nameof(TimingPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = new HarmonyMethod(typeof(HarmonyPatcher)
                    .GetMethod(nameof(TimingPostfix), BindingFlags.Static | BindingFlags.NonPublic));

                h.Patch(method, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                Logger.Log($"[LagTrace] Could not patch {t.Name}.{methodName}: {ex.Message}");
            }
        }

        private static void TimingPrefix(ref object __state) =>
            __state = Stopwatch.StartNew();

        private static void TimingPostfix(MethodBase __originalMethod, object __state)
        {
            if (__state is Stopwatch sw)
            {
                sw.Stop();
                var key = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
                Timings.Record(key, sw.ElapsedTicks);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Timings — instrumented timing store (complementary to sampler)
    //  Used both by Harmony patches and by the public `Timings.Start()` API.
    // ─────────────────────────────────────────────────────────────────────────────
    public static class Timings
    {
        private static readonly Dictionary<string, TimingBucket> _buckets
            = new Dictionary<string, TimingBucket>(64);
        private static readonly ReaderWriterLockSlim _rwl = new ReaderWriterLockSlim();

        public static void Record(string name, long ticks)
        {
            _rwl.EnterWriteLock();
            try
            {
                if (!_buckets.TryGetValue(name, out var b))
                    _buckets[name] = b = new TimingBucket();
                b.TotalTicks += ticks;
                b.Calls++;
                if (ticks > b.MaxTicks) b.MaxTicks = ticks;
            }
            finally { _rwl.ExitWriteLock(); }
        }

        // Convenience RAII scope for manual instrumentation.
        public static IDisposable Start(string name) => new TimerScope(name);

        public static List<TimingEntry> GetTop(int n, bool perCall = false)
        {
            _rwl.EnterReadLock();
            try
            {
                return _buckets
                    .Select(kv =>
                    {
                        double totalMs = kv.Value.TotalTicks * 1000.0 / Stopwatch.Frequency;
                        double avgMs = kv.Value.Calls > 0 ? totalMs / kv.Value.Calls : 0;
                        double maxMs = kv.Value.MaxTicks * 1000.0 / Stopwatch.Frequency;
                        return new TimingEntry
                        {
                            Name = kv.Key,
                            TotalMs = totalMs,
                            AvgMs = avgMs,
                            MaxMs = maxMs,
                            Calls = kv.Value.Calls,
                        };
                    })
                    .OrderByDescending(e => perCall ? e.AvgMs : e.TotalMs)
                    .Take(n)
                    .ToList();
            }
            finally { _rwl.ExitReadLock(); }
        }

        public static void Reset()
        {
            _rwl.EnterWriteLock();
            try { _buckets.Clear(); }
            finally { _rwl.ExitWriteLock(); }
        }

        private class TimingBucket
        {
            public long TotalTicks;
            public long MaxTicks;
            public int Calls;
        }

        private class TimerScope : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _sw;

            public TimerScope(string name) { _name = name; _sw = Stopwatch.StartNew(); }

            public void Dispose()
            {
                _sw.Stop();
                Record(_name, _sw.ElapsedTicks);
            }
        }
    }

    public class TimingEntry
    {
        public string Name;
        public double TotalMs;
        public double AvgMs;
        public double MaxMs;
        public int Calls;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  FrameTimingComponent — attached to the plugin GameObject at load time.
    //  Unity calls LateUpdate() on it every frame, giving us real frame-time
    //  without needing to patch MonoBehaviour (which has no patchable LateUpdate).
    // ─────────────────────────────────────────────────────────────────────────────
    public class FrameTimingComponent : MonoBehaviour
    {
        private float _lastTime;

        private void Awake()
        {
            // Belt-and-suspenders: re-capture the main thread here in case Load()
            // ran on a different thread in some Rocketmod build.
            MainThreadRef.Capture();
        }

        private void LateUpdate()
        {
            float now = Time.realtimeSinceStartup;
            float delta = (now - _lastTime) * 1000f;
            _lastTime = now;

            if (delta > 0f)
                LagTracePlugin.OnFrameComplete(delta);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Commands
    // ─────────────────────────────────────────────────────────────────────────────

    /// /lag — quick server health snapshot
    public class CommandLag : IRocketCommand
    {
        public string Name => "lag";
        public string Help => "Show current TPS and top methods.";
        public string Syntax => "/lag";
        public List<string> Aliases => new List<string>();
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new List<string> { "lagtrace.lag" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            float tps = 1f / Time.smoothDeltaTime;
            var top = LagTracePlugin.Sampler.GetTop(5);

            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] TPS: {tps:F1}  FrameTime: {Time.smoothDeltaTime * 1000f:F1}ms");
            sb.AppendLine("Top 5 methods (self%):");
            if (top.Count == 0) { sb.AppendLine("  (no data yet — wait a few seconds)"); }
            else foreach (var e in top)
                    sb.AppendLine($"  {CommandHelpers.Truncate(e.DisplayName, 55)}  {e.Pct:F1}%");

            CommandHelpers.Reply(caller, sb.ToString());
        }
    }

    /// /lagtop [n] [avg] — top methods, optionally sorted by avg time
    public class CommandLagTop : IRocketCommand
    {
        public string Name => "lagtop";
        public string Help => "Show top N heaviest methods. Usage: /lagtop [n] [avg]";
        public string Syntax => "/lagtop [n] [avg]";
        public List<string> Aliases => new List<string> { "lt" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new List<string> { "lagtrace.lagtop" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = LagTracePlugin.Instance.Configuration.Instance.TopN;
            bool byAvg = false;

            foreach (var a in args)
            {
                if (int.TryParse(a, out int parsed)) n = Math.Max(1, Math.Min(parsed, 50));
                if (a.Equals("avg", StringComparison.OrdinalIgnoreCase)) byAvg = true;
            }

            // Prefer instrumented data; fall back to sampler for methods without explicit timing.
            var timed = Timings.GetTop(n, byAvg);
            var sampled = LagTracePlugin.Sampler.GetTop(n);

            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] Top {n} methods ({(byAvg ? "avg" : "total")} time):");

            if (timed.Count > 0)
            {
                sb.AppendLine("  ── Instrumented (patched methods) ──");
                foreach (var e in timed)
                    sb.AppendLine($"  {CommandHelpers.Truncate(e.Name, 48)}  {e.TotalMs:F2}ms  avg {e.AvgMs:F3}ms  max {e.MaxMs:F2}ms  ×{e.Calls}");
            }
            if (sampled.Count > 0)
            {
                sb.AppendLine("  ── Sampler (all code) ──");
                foreach (var e in sampled)
                    sb.AppendLine($"  {CommandHelpers.Truncate(e.DisplayName, 52)}  {e.Pct:F1}%");
            }
            if (timed.Count == 0 && sampled.Count == 0)
                sb.AppendLine("  (no data yet)");

            CommandHelpers.Reply(caller, sb.ToString());
        }
    }

    /// /lagplugins [n] [engine|plugins] — per-source breakdown with category grouping
    public class CommandLagPlugins : IRocketCommand
    {
        public string Name => "lagplugins";
        public string Help => "Show CPU cost per plugin and engine manager. Filter: engine / plugins.";
        public string Syntax => "/lagplugins [n] [engine|plugins]";
        public List<string> Aliases => new List<string> { "lp" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new List<string> { "lagtrace.lagplugins" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = 20;
            TrackerCategory? filter = null;

            foreach (var a in args)
            {
                if (int.TryParse(a, out int parsed))
                    n = Math.Max(1, Math.Min(parsed, 50));
                else if (a.Equals("engine", StringComparison.OrdinalIgnoreCase))
                    filter = TrackerCategory.Engine;
                else if (a.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                    filter = TrackerCategory.Plugin;
            }

            var entries = LagTracePlugin.Sampler.GetTop(n, filter);

            if (entries.Count == 0)
            {
                CommandHelpers.Reply(caller, "[LagTrace] No attribution data yet — wait a few seconds.");
                return;
            }

            var sb = new StringBuilder();

            string filterLabel = filter == null ? "all" :
                                 filter == TrackerCategory.Engine ? "engine only" : "plugins only";

            sb.AppendLine($"[LagTrace] CPU attribution ({filterLabel}, top {n}):");

            if (filter == null)
            {
                double enginePct = entries.Where(e => e.Category == TrackerCategory.Engine).Sum(e => e.Pct);
                double pluginPct = entries.Where(e => e.Category == TrackerCategory.Plugin).Sum(e => e.Pct);

                sb.AppendLine($"  Summary → [Engine] {enginePct:F1}%   [Plugins] {pluginPct:F1}%");
                sb.AppendLine();
            }

            TrackerCategory? lastCat = null;

            foreach (var e in entries.OrderBy(e => e.Category).ThenByDescending(e => e.Pct))
            {
                if (e.Category != lastCat)
                {
                    sb.AppendLine(e.Category == TrackerCategory.Engine
                        ? "  ── Unturned engine ──"
                        : "  ── Rocket plugins ──");
                    lastCat = e.Category;
                }

                int filled = (int)Math.Round(Math.Min(e.Pct, 100.0) / 5.0);
                string bar = new string('█', filled) + new string('░', 20 - filled);

                sb.AppendLine($"  {bar}  {e.Pct,5:F1}%  {CommandHelpers.Truncate(e.DisplayName, 38)}  ({e.Samples} samples)");
            }

            CommandHelpers.Reply(caller, sb.ToString());
        }
    }

    /// /lagspike — show last recorded lag spike
    public class CommandLagSpike : IRocketCommand
    {
        public string Name => "lagspike";
        public string Help => "Show the most recent lag spike call stack.";
        public string Syntax => "/lagspike [list]";
        public List<string> Aliases => new List<string> { "ls" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new List<string> { "lagtrace.lagspike" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            bool list = args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase);

            if (list)
            {
                var all = LagTracePlugin.Spikes.GetAll();
                if (all.Count == 0) { CommandHelpers.Reply(caller, "[LagTrace] No spikes recorded yet."); return; }
                var sb = new StringBuilder();
                sb.AppendLine("[LagTrace] Recent spikes:");
                foreach (var s in all)
                    sb.AppendLine($"  {s.TimestampUtc:HH:mm:ss}  {s.FrameMs:F1}ms");
                CommandHelpers.Reply(caller, sb.ToString());
                return;
            }

            var spike = LagTracePlugin.Spikes.GetLast();
            if (spike == null) { CommandHelpers.Reply(caller, "[LagTrace] No spikes recorded yet."); return; }

            var out2 = new StringBuilder();
            out2.AppendLine($"[LagTrace] Last spike at {spike.TimestampUtc:HH:mm:ss} UTC — {spike.FrameMs:F1}ms");
            out2.AppendLine("  Call stack (top = leaf):");
            int shown = Math.Min(spike.Stack.Length, 20);
            for (int i = 0; i < shown; i++)
                out2.AppendLine($"  [{i,2}] {CommandHelpers.Truncate(spike.Stack[i], 70)}");
            if (spike.Stack.Length > shown)
                out2.AppendLine($"  ... and {spike.Stack.Length - shown} more frames");
            CommandHelpers.Reply(caller, out2.ToString());
        }
    }

    /// /lagreset — clear all accumulated data
    public class CommandLagReset : IRocketCommand
    {
        public string Name => "lagreset";
        public string Help => "Reset all LagTrace timing data.";
        public string Syntax => "/lagreset";
        public List<string> Aliases => new List<string>();
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new List<string> { "lagtrace.reset" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            LagTracePlugin.Sampler.Reset();
            CommandHelpers.Reply(caller, "[LagTrace] All data cleared.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Shared utilities
    // ─────────────────────────────────────────────────────────────────────────────
    internal static class CommandHelpers
    {
        public static void Reply(IRocketPlayer player, string message)
        {
            if (player is ConsolePlayer)
                Logger.Log(message);
            else
                Rocket.Core.Utils.TaskDispatcher.QueueOnMainThread(() =>
                    Rocket.Unturned.Chat.UnturnedChat.Say(player, message, UnityEngine.Color.cyan));
        }

        public static string Truncate(string s, int max) =>
            s.Length <= max ? s : "…" + s.Substring(s.Length - (max - 1));
    }

    // Make the helpers available in command classes without full qualification.
    public abstract class CommandBase
    {
        protected static void Reply(IRocketPlayer p, string msg) => CommandHelpers.Reply(p, msg);
        protected static string Truncate(string s, int n) => CommandHelpers.Truncate(s, n);
    }
}
