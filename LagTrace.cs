using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Rocket.API;
using Rocket.Core.Logging;
using Rocket.Core.Plugins;
using UnityEngine;

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
        public static readonly SamplerEngine Sampler = new SamplerEngine();
        public static readonly SpikeDetector Spikes = new SpikeDetector();
        public static readonly PluginTimingTracker PluginTracker = new PluginTimingTracker();

        protected override void Load()
        {
            Instance = this;

            _harmony = new Harmony("com.lagtrace");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Patch all RocketPlugin.FixedUpdate / Update that are currently loaded
            HarmonyPatcher.PatchLoadedPlugins(_harmony);

            // Start background sampler (samples main thread stack every N ms)
            _samplerThread = new Thread(SamplerLoop)
            {
                Name       = "LagTrace-Sampler",
                IsBackground = true,
                Priority   = ThreadPriority.BelowNormal
            };
            _samplerThread.Start();

            InvokeRepeating(nameof(OnFlushWindow),
                Configuration.Instance.WindowSeconds,
                Configuration.Instance.WindowSeconds);

            Logger.Log("[LagTrace] Loaded. Commands: /lag, /lagtop, /lagplugins, /lagspike");
        }

        protected override void Unload()
        {
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
            if (Configuration.Instance.AutoPrint)
            {
                var sb = new StringBuilder();
                sb.AppendLine("[LagTrace] ── Top methods (last window) ──");
                foreach (var entry in Sampler.GetTop(10))
                    sb.AppendLine($"  {entry.Name}  {entry.SelfPct:F1}%  ({entry.TotalSamples} samples)");
                Logger.Log(sb.ToString());
            }
        }

        // ── Unity frame hook (patches applied before this) ─────────────────────
        // Called by FrameTimingPatch every LateUpdate to feed spike detector.
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
        public int    SampleIntervalMs = 5;     // How often the sampler snapshots
        public int    WindowSeconds    = 60;    // Flush/report window
        public float  SpikeThresholdMs = 50f;  // A frame longer than this is a "spike"
        public bool   AutoPrint        = false; // Print top to console each window
        public int    TopN             = 10;    // Default rows for /lagtop

        public void LoadDefaults()
        {
            SampleIntervalMs = 5;
            WindowSeconds    = 60;
            SpikeThresholdMs = 50f;
            AutoPrint        = false;
            TopN             = 10;
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
    public class SamplerEngine
    {
        private const int MaxDepth = 40;

        private volatile bool _running = true;
        public  bool Running => _running;

        // Key = fully-qualified method name, value = (self, total) sample counts
        private readonly Dictionary<string, (long self, long total)> _counts
            = new Dictionary<string, (long, long)>(512);
        private SpinLock _lock = new SpinLock(false);

        // For spike recording we also store the last raw stack
        private string[] _lastStack = Array.Empty<string>();

        public void Stop() => _running = false;

        public void Sample()
        {
            // Capture stack on the main Unity thread via Thread.CurrentThread is NOT
            // the sampler thread. We need a reference to the main thread stored at
            // startup. This is done via MainThreadRef below.
            var mainThread = MainThreadRef.Thread;
            if (mainThread == null || !_running) return;

            StackTrace st;
            try
            {
#pragma warning disable CS0618 // Suspend/Resume are deprecated but still functional on .NET 4.x
                mainThread.Suspend();
                st = new StackTrace(mainThread, false);
                mainThread.Resume();
#pragma warning restore CS0618
            }
            catch
            {
                return; // Thread may have ended between Suspend and StackTrace
            }

            int depth  = Math.Min(st.FrameCount, MaxDepth);
            var frames = new string[depth];

            for (int i = 0; i < depth; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                frames[i] = m != null
                    ? $"{m.DeclaringType?.FullName ?? "?"}.{m.Name}"
                    : "?";
            }

            bool taken = false;
            _lock.Enter(ref taken);
            try
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    var name = frames[i];
                    _counts.TryGetValue(name, out var c);
                    _counts[name] = (i == 0 ? c.self + 1 : c.self, c.total + 1);
                }
                _lastStack = frames;
            }
            finally
            {
                if (taken) _lock.Exit();
            }
        }

        public string[] GetLastStack()
        {
            bool taken = false;
            _lock.Enter(ref taken);
            try   { return (string[])_lastStack.Clone(); }
            finally { if (taken) _lock.Exit(); }
        }

        public List<SamplerEntry> GetTop(int n)
        {
            bool taken = false;
            _lock.Enter(ref taken);
            Dictionary<string, (long self, long total)> snap;
            try   { snap = new Dictionary<string, (long, long)>(_counts); }
            finally { if (taken) _lock.Exit(); }

            long grandTotal = snap.Values.Sum(v => v.self);
            if (grandTotal == 0) return new List<SamplerEntry>();

            return snap
                .OrderByDescending(kv => kv.Value.self)
                .Take(n)
                .Select(kv => new SamplerEntry
                {
                    Name         = kv.Key,
                    TotalSamples = kv.Value.self,
                    SelfPct      = kv.Value.self * 100.0 / grandTotal,
                    TotalPct     = kv.Value.total * 100.0 / grandTotal,
                })
                .ToList();
        }

        public void Reset()
        {
            bool taken = false;
            _lock.Enter(ref taken);
            try   { _counts.Clear(); }
            finally { if (taken) _lock.Exit(); }
        }
    }

    public class SamplerEntry
    {
        public string Name;
        public long   TotalSamples;
        public double SelfPct;
        public double TotalPct;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Stores a reference to the main Unity thread.
    //  Set from a MonoBehaviour Awake() that runs on startup.
    // ─────────────────────────────────────────────────────────────────────────────
    public static class MainThreadRef
    {
        public static Thread Thread { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Capture() => Thread = System.Threading.Thread.CurrentThread;
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
        private int  _head  = 0;
        private int  _count = 0;
        private readonly object _lock = new object();

        public void Feed(float frameMs)
        {
            if (frameMs < LagTracePlugin.Instance.Configuration.Instance.SpikeThresholdMs)
                return;

            var stack = LagTracePlugin.Sampler.GetLastStack();
            var record = new SpikeRecord
            {
                TimestampUtc = DateTime.UtcNow,
                FrameMs      = frameMs,
                Stack        = stack,
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
        public float    FrameMs;
        public string[] Stack;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  PluginTimingTracker — per-plugin self-time derived from the sampler.
    //  We attribute a sample to a plugin by checking whether any frame in the
    //  stack matches the plugin's assembly name.
    // ─────────────────────────────────────────────────────────────────────────────
    public class PluginTimingTracker
    {
        // Populated by HarmonyPatcher when it finds plugin assemblies.
        private readonly Dictionary<string, string> _assemblyToPlugin
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, long> _hits
            = new Dictionary<string, long>();
        private readonly object _lock = new object();
        private long _totalSamples;

        public void RegisterPlugin(string pluginName, string assemblyName)
        {
            lock (_lock)
                _assemblyToPlugin[assemblyName] = pluginName;
        }

        // Called from SamplerEngine after each sample (optional integration).
        public void Record(string[] frames)
        {
            // Walk frames and credit the first plugin we find (leaf-first = blame)
            foreach (var frame in frames)
            {
                // Frame format: "Namespace.Type.Method"
                // We check if the root namespace matches any registered assembly prefix.
                foreach (var kv in _assemblyToPlugin)
                {
                    if (frame.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_lock)
                        {
                            _hits.TryGetValue(kv.Value, out var h);
                            _hits[kv.Value] = h + 1;
                            _totalSamples++;
                        }
                        return;
                    }
                }
            }
            // Frame not attributable to any plugin → engine cost
            lock (_lock) _totalSamples++;
        }

        public List<PluginTimingEntry> GetTop(int n)
        {
            lock (_lock)
            {
                if (_totalSamples == 0) return new List<PluginTimingEntry>();
                return _hits
                    .OrderByDescending(kv => kv.Value)
                    .Take(n)
                    .Select(kv => new PluginTimingEntry
                    {
                        PluginName   = kv.Key,
                        Samples      = kv.Value,
                        Pct          = kv.Value * 100.0 / _totalSamples,
                    })
                    .ToList();
            }
        }

        public void Reset()
        {
            lock (_lock) { _hits.Clear(); _totalSamples = 0; }
        }
    }

    public class PluginTimingEntry
    {
        public string PluginName;
        public long   Samples;
        public double Pct;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  HarmonyPatcher — patches every loaded RocketPlugin to register with the
    //  PluginTimingTracker, and patches LateUpdate for frame-time measurement.
    // ─────────────────────────────────────────────────────────────────────────────
    public static class HarmonyPatcher
    {
        public static void PatchLoadedPlugins(Harmony h)
        {
            foreach (var plugin in Rocket.Core.R.Plugins.GetPlugins())
            {
                var asm    = plugin.GetType().Assembly;
                var pName  = plugin.Name;
                var asmName = asm.GetName().Name;

                LagTracePlugin.PluginTracker.RegisterPlugin(pName, asmName);

                // Patch FixedUpdate if present
                TryPatch(h, plugin.GetType(), "FixedUpdate", pName);
                TryPatch(h, plugin.GetType(), "Update",      pName);
                TryPatch(h, plugin.GetType(), "LateUpdate",  pName);
            }
        }

        private static void TryPatch(Harmony h, Type t, string methodName, string pluginName)
        {
            var method = t.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) return;

            try
            {
                var prefix  = new HarmonyMethod(typeof(HarmonyPatcher)
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

        // Store Stopwatch per-call in the __state object.
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
                        double avgMs   = kv.Value.Calls > 0 ? totalMs / kv.Value.Calls : 0;
                        double maxMs   = kv.Value.MaxTicks  * 1000.0 / Stopwatch.Frequency;
                        return new TimingEntry
                        {
                            Name    = kv.Key,
                            TotalMs = totalMs,
                            AvgMs   = avgMs,
                            MaxMs   = maxMs,
                            Calls   = kv.Value.Calls,
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
            public int  Calls;
        }

        private class TimerScope : IDisposable
        {
            private readonly string    _name;
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
        public int    Calls;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Harmony patches
    // ─────────────────────────────────────────────────────────────────────────────

    // Measure each LateUpdate so we can report frame-time to the spike detector.
    [HarmonyPatch(typeof(MonoBehaviour), "LateUpdate")]
    public class FrameTimingPatch
    {
        private static float _lastTime;

        static void Prefix() => _lastTime = Time.realtimeSinceStartup;

        static void Postfix()
        {
            float delta = (Time.realtimeSinceStartup - _lastTime) * 1000f;
            LagTracePlugin.OnFrameComplete(delta);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Commands
    // ─────────────────────────────────────────────────────────────────────────────

    /// /lag — quick server health snapshot
    public class CommandLag : IRocketCommand
    {
        public string Name        => "lag";
        public string Help        => "Show current TPS and top methods.";
        public string Syntax       => "/lag";
        public List<string> Aliases => new List<string>();
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions    => new List<string> { "lagtrace.lag" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            float tps = 1f / Time.smoothDeltaTime;
            var top = LagTracePlugin.Sampler.GetTop(5);

            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] TPS: {tps:F1}  FrameTime: {Time.smoothDeltaTime * 1000f:F1}ms");
            sb.AppendLine("Top 5 methods (self%):");
            if (top.Count == 0) { sb.AppendLine("  (no data yet — wait a few seconds)"); }
            else foreach (var e in top)
                sb.AppendLine($"  {Truncate(e.Name, 55)}  {e.SelfPct:F1}%");

            Reply(caller, sb.ToString());
        }
    }

    /// /lagtop [n] [avg] — top methods, optionally sorted by avg time
    public class CommandLagTop : IRocketCommand
    {
        public string Name        => "lagtop";
        public string Help        => "Show top N heaviest methods. Usage: /lagtop [n] [avg]";
        public string Syntax       => "/lagtop [n] [avg]";
        public List<string> Aliases => new List<string> { "lt" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions    => new List<string> { "lagtrace.lagtop" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int  n      = LagTracePlugin.Instance.Configuration.Instance.TopN;
            bool byAvg  = false;

            foreach (var a in args)
            {
                if (int.TryParse(a, out int parsed)) n = Math.Clamp(parsed, 1, 50);
                if (a.Equals("avg", StringComparison.OrdinalIgnoreCase)) byAvg = true;
            }

            // Prefer instrumented data; fall back to sampler for methods without explicit timing.
            var timed   = Timings.GetTop(n, byAvg);
            var sampled = LagTracePlugin.Sampler.GetTop(n);

            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] Top {n} methods ({(byAvg ? "avg" : "total")} time):");

            if (timed.Count > 0)
            {
                sb.AppendLine("  ── Instrumented (patched methods) ──");
                foreach (var e in timed)
                    sb.AppendLine($"  {Truncate(e.Name, 48)}  {e.TotalMs:F2}ms  avg {e.AvgMs:F3}ms  max {e.MaxMs:F2}ms  ×{e.Calls}");
            }
            if (sampled.Count > 0)
            {
                sb.AppendLine("  ── Sampler (all code) ──");
                foreach (var e in sampled)
                    sb.AppendLine($"  {Truncate(e.Name, 52)}  {e.SelfPct:F1}%");
            }
            if (timed.Count == 0 && sampled.Count == 0)
                sb.AppendLine("  (no data yet)");

            Reply(caller, sb.ToString());
        }
    }

    /// /lagplugins [n] — per-plugin breakdown
    public class CommandLagPlugins : IRocketCommand
    {
        public string Name        => "lagplugins";
        public string Help        => "Show CPU cost per plugin.";
        public string Syntax       => "/lagplugins [n]";
        public List<string> Aliases => new List<string> { "lp" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions    => new List<string> { "lagtrace.lagplugins" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = 15;
            if (args.Length > 0 && int.TryParse(args[0], out int parsed))
                n = Math.Clamp(parsed, 1, 50);

            var entries = LagTracePlugin.PluginTracker.GetTop(n);
            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] Plugin CPU cost (sampler attribution, top {n}):");

            if (entries.Count == 0) { sb.AppendLine("  (no data yet — wait a few seconds)"); }
            else foreach (var e in entries)
                sb.AppendLine($"  {Truncate(e.PluginName, 40)}  {e.Pct:F1}%  ({e.Samples} samples)");

            Reply(caller, sb.ToString());
        }
    }

    /// /lagspike — show last recorded lag spike
    public class CommandLagSpike : IRocketCommand
    {
        public string Name        => "lagspike";
        public string Help        => "Show the most recent lag spike call stack.";
        public string Syntax       => "/lagspike [list]";
        public List<string> Aliases => new List<string> { "ls" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions    => new List<string> { "lagtrace.lagspike" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            bool list = args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase);

            if (list)
            {
                var all = LagTracePlugin.Spikes.GetAll();
                if (all.Count == 0) { Reply(caller, "[LagTrace] No spikes recorded yet."); return; }
                var sb = new StringBuilder();
                sb.AppendLine("[LagTrace] Recent spikes:");
                foreach (var s in all)
                    sb.AppendLine($"  {s.TimestampUtc:HH:mm:ss}  {s.FrameMs:F1}ms");
                Reply(caller, sb.ToString());
                return;
            }

            var spike = LagTracePlugin.Spikes.GetLast();
            if (spike == null) { Reply(caller, "[LagTrace] No spikes recorded yet."); return; }

            var out2 = new StringBuilder();
            out2.AppendLine($"[LagTrace] Last spike at {spike.TimestampUtc:HH:mm:ss} UTC — {spike.FrameMs:F1}ms");
            out2.AppendLine("  Call stack (top = leaf):");
            int shown = Math.Min(spike.Stack.Length, 20);
            for (int i = 0; i < shown; i++)
                out2.AppendLine($"  [{i,2}] {Truncate(spike.Stack[i], 70)}");
            if (spike.Stack.Length > shown)
                out2.AppendLine($"  ... and {spike.Stack.Length - shown} more frames");
            Reply(caller, out2.ToString());
        }
    }

    /// /lagreset — clear all accumulated data
    public class CommandLagReset : IRocketCommand
    {
        public string Name        => "lagreset";
        public string Help        => "Reset all LagTrace timing data.";
        public string Syntax       => "/lagreset";
        public List<string> Aliases => new List<string>();
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions    => new List<string> { "lagtrace.reset" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            Timings.Reset();
            LagTracePlugin.Sampler.Reset();
            LagTracePlugin.PluginTracker.Reset();
            Reply(caller, "[LagTrace] All data cleared.");
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
                    ((SDG.Unturned.SteamPlayer)
                        ((Rocket.Unturned.Player.UnturnedPlayer)player).SteamPlayer())
                        ?.sendChat(message, UnityEngine.Color.cyan));
        }

        public static string Truncate(string s, int max) =>
            s.Length <= max ? s : "…" + s.Substring(s.Length - (max - 1));
    }

    // Make the helpers available in command classes without full qualification.
    public abstract class CommandBase
    {
        protected static void Reply(IRocketPlayer p, string msg) => CommandHelpers.Reply(p, msg);
        protected static string Truncate(string s, int n)        => CommandHelpers.Truncate(s, n);
    }
}
