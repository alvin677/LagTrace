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
    public class LagTracePlugin : RocketPlugin<LagTraceConfig>
    {
        public static LagTracePlugin Instance { get; private set; }
        private Harmony _harmony;
        private Thread _samplerThread;

        public static readonly Sampler Sampler = new Sampler();
        public static readonly SpikeDetector Spikes = new SpikeDetector();

        protected override void Load()
        {
            Instance = this;

            MainThreadRef.Capture();

            _harmony = new Harmony("com.lagtrace");

            gameObject.AddComponent<FrameTimingComponent>();
            HarmonyPatcher.PatchLoadedPlugins(_harmony);

            Sampler.BuildRegistry();
            Sampler.Start();
            _samplerThread = new Thread(SamplerLoop)
            {
                Name = "LagTrace-Sampler",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _samplerThread.Start();

            if (Configuration.Instance.AutoPrint)
                InvokeRepeating(nameof(OnFlushWindow), Configuration.Instance.WindowSeconds, Configuration.Instance.WindowSeconds);

            Logger.Log("[LagTrace] Loaded. Commands: /lag, /lagtop, /lagplugins, /lagspike");
        }

        protected override void Unload()
        {
            if (Configuration.Instance.AutoPrint) CancelInvoke(nameof(OnFlushWindow));
            Sampler.Stop();
            _samplerThread?.Join(500);
            _harmony?.UnpatchAll("com.lagtrace");

            // Config-driven cleanup option
            if (Configuration.Instance.ClearOnUnload)
            {
                Sampler.Reset();
                Timings.Reset();
            }

            Logger.Log("[LagTrace] Unloaded.");
        }

        private void SamplerLoop()
        {
            while (Sampler.Running)
            {
                Sampler.Sample();
                Thread.Sleep(Configuration.Instance.SampleIntervalMs);
            }
        }

        private void OnFlushWindow()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[LagTrace] ── Top methods (last window) ──");
            foreach (var entry in Sampler.GetTop(10))
                sb.AppendLine($"  {entry.DisplayName}  {entry.Pct:F1}%  ({entry.Samples} samples)");
            Logger.Log(sb.ToString());
        }

        public static void OnFrameComplete(float deltaMs)
        {
            Spikes.Feed(deltaMs);
        }
    }

    public class LagTraceConfig : Rocket.API.IRocketPluginConfiguration
    {
        public int SampleIntervalMs = 5;
        public int WindowSeconds = 60;
        public float SpikeThresholdMs = 50f;
        public bool AutoPrint = false;
        public int TopN = 10;
        public bool ClearOnUnload = true;  // NEW: Cleanup on unload
        public bool LogPatchErrors = true; // NEW: Show Harmony patch errors

        public void LoadDefaults()
        {
            SampleIntervalMs = 5;
            WindowSeconds = 60;
            SpikeThresholdMs = 50f;
            AutoPrint = false;
            TopN = 10;
            ClearOnUnload = true;
            LogPatchErrors = true;
        }
    }

    public class Sampler
    {
        private volatile bool _running;
        private readonly ConcurrentDictionary<(string, TrackerCategory), int> _counts = new();
        private readonly Dictionary<string, TrackerCategory> _categoryRegistry = new();
        private int _totalSamples;

        // Stack trace caching for performance - reduces allocations!
        private static volatile Thread? _cachedMainStackTraceThread;
        private static volatile StackTrace? _cachedStackTrace;

        public void Start() => _running = true;
        public void Stop() => _running = false;
        public bool Running => _running;

        public void Sample()
        {
            if (!_running) return;

            var mainThread = MainThreadRef.MainThread;
            if (mainThread == null || mainThread != Thread.CurrentThread)
            {
                // Only log once per error, not every sample!
                if (_stackTraceErrorLogged)
                    return;
                Logger.Log("[LagTrace] Warning: Stack trace on non-current thread — ignoring");
                _stackTraceErrorLogged = true;
                return;
            }

            StackTrace stackTrace;
            try
            {
                // Cache when we're actually sampling the current thread
                if (_cachedMainStackTraceThread == mainThread && _cachedStackTrace != null)
                {
                    stackTrace = _cachedStackTrace;
                }
                else
                {
                    _cachedStackTrace = new StackTrace(mainThread, false);
                    _cachedMainStackTraceThread = mainThread;
                    stackTrace = _cachedStackTrace;
                }
            }
            catch (Exception ex)
            {
                // Only warn once! Not on every sample!
                if (_stackTraceErrorLogged)
                    return;

                Logger.Log($"[LagTrace] StackTrace error (logged once): {ex.Message}");
                _stackTraceErrorLogged = true;
                return;
            }

            var frame = stackTrace.GetFrame(0);
            var method = frame?.GetMethod();
            if (method == null) return;

            string assembly = method.DeclaringType?.Assembly?.GetName()?.Name ?? "unknown";
            var category = Classify(assembly);

            var key = (assembly, category);
            _counts.AddOrUpdate(key, 1, (_, v) => v + 1);
            Interlocked.Increment(ref _totalSamples);
        }

        // Add these fields to Sampler class:
        private bool _stackTraceErrorLogged = false;

        public string[] GetLastStackSafe()
        {
            try
            {
                if (_cachedMainStackTraceThread == MainThreadRef.MainThread && _cachedStackTrace != null)
                    return new[] { _cachedStackTrace.GetFrame(0)?.GetMethod()?.DeclaringType?.FullName + "." + _cachedStackTrace.GetFrame(0)?.GetMethod()?.Name };

                var st = new StackTrace(MainThreadRef.MainThread, false);
                return st.GetFrame(0)?.GetMethod() == null ? Array.Empty<string>() : new[] { st.GetFrame(0)?.GetMethod()?.DeclaringType?.FullName + "." + st.GetFrame(0)?.GetMethod()?.Name };
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public List<SampleEntry> GetTop(int n, TrackerCategory? filter = null)
        {
            int total = _totalSamples;
            if (total == 0) return new List<SampleEntry>();

            return _counts
                .Select(kv => new SampleEntry
                {
                    DisplayName = kv.Key.Item1,
                    Category = kv.Key.Item2,
                    Samples = kv.Value,
                    Pct = (kv.Value * 100.0) / total
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

        private TrackerCategory Classify(string assembly)
        {
            if (string.IsNullOrEmpty(assembly)) return TrackerCategory.Core;
            if (_categoryRegistry.TryGetValue(assembly, out var cat)) return cat;
            if (assembly.StartsWith("Unity") || assembly.StartsWith("System") || assembly.StartsWith("mscorlib"))
                return TrackerCategory.Core;
            return TrackerCategory.Plugin;
        }

        public void RegisterAssembly(string assemblyName, TrackerCategory category)
        {
            if (string.IsNullOrEmpty(assemblyName)) return;
            _categoryRegistry[assemblyName] = category;
        }

        public void BuildRegistry()
        {
            _categoryRegistry.Clear();
            _categoryRegistry["Assembly-CSharp"] = TrackerCategory.Core;
            _categoryRegistry["UnityEngine"] = TrackerCategory.Core;
            _categoryRegistry["System"] = TrackerCategory.Core;
            _categoryRegistry["mscorlib"] = TrackerCategory.Core;
        }

        public void Reset()
        {
            _counts.Clear();
            _totalSamples = 0;
            _cachedStackTrace = null; // Clear cache on reset
        }
    }

    public class SampleEntry
    {
        public string DisplayName;
        public int Samples;
        public double Pct;
        public TrackerCategory Category;
    }

    public static class MainThreadRef
    {
        public static Thread MainThread;
        public static void Capture() => MainThread = Thread.CurrentThread;
    }

    public class SpikeDetector
    {
        private const int MaxSpikes = 20;
        private readonly SpikeRecord[] _ring = new SpikeRecord[MaxSpikes];
        private int _head = 0;
        private int _count = 0;
        private readonly object _lock = new object();

        public void Feed(float frameMs)
        {
            if (frameMs < LagTracePlugin.Instance.Configuration.Instance.SpikeThresholdMs) return;

            var record = new SpikeRecord
            {
                TimestampUtc = DateTime.UtcNow,
                FrameMs = frameMs,
                Stack = LagTracePlugin.Sampler.GetLastStackSafe()
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

    public enum TrackerCategory { Plugin, Engine, Core }

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

        public static void PatchLoadedPlugins(Harmony h)
        {
            foreach (var plugin in Rocket.Core.R.Plugins.GetPlugins())
            {
                if (plugin.GetType().Assembly == typeof(LagTracePlugin).Assembly) continue;

                var asm = plugin.GetType().Assembly;
                var pName = plugin.Name;
                var asmName = asm.GetName().Name;

                foreach (var m in TargetMethods) TryPatch(h, plugin.GetType(), m);
            }

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
                if (t == null) continue;

                foreach (var m in TargetMethods) TryPatch(h, t, m);
            }

            Logger.Log("[LagTrace] Engine manager patches applied.");
        }

        private static void TryPatch(Harmony h, Type t, string methodName)
        {
            var method = t.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) return;

            try
            {
                var prefix = new HarmonyMethod(typeof(HarmonyPatcher).GetMethod(nameof(TimingPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = new HarmonyMethod(typeof(HarmonyPatcher).GetMethod(nameof(TimingPostfix), BindingFlags.Static | BindingFlags.NonPublic));

                h.Patch(method, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                if (LagTracePlugin.Instance.Configuration.Instance.LogPatchErrors)
                    Logger.Log($"[LagTrace] Could not patch {t.Name}.{methodName}: {ex.Message}");
            }
        }

        private static void TimingPrefix(ref object __state) => __state = Stopwatch.StartNew();
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

    public static class Timings
    {
        private static readonly Dictionary<string, TimingBucket> _buckets = new(64);
        private static readonly ReaderWriterLockSlim _rwl = new();

        public static void Record(string name, long ticks)
        {
            _rwl.EnterWriteLock();
            try
            {
                if (!_buckets.TryGetValue(name, out var b)) _buckets[name] = b = new TimingBucket();
                b.TotalTicks += ticks;
                b.Calls++;
                if (ticks > b.MaxTicks) b.MaxTicks = ticks;
            }
            finally { _rwl.ExitWriteLock(); }
        }

        public static IDisposable Start(string name) => new TimerScope(name);

        public static List<TimingEntry> GetTop(int n, bool perCall = false)
        {
            _rwl.EnterReadLock();
            try
            {
                return _buckets.Select(kv =>
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
                }).OrderByDescending(e => perCall ? e.AvgMs : e.TotalMs).Take(n).ToList();
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

    public class FrameTimingComponent : MonoBehaviour
    {
        private float _lastTime;

        private void Awake()
        {
            MainThreadRef.Capture(); // Belt-and-suspenders thread capture
        }

        private void LateUpdate()
        {
            float now = Time.realtimeSinceStartup;
            float delta = (now - _lastTime) * 1000f;
            _lastTime = now;

            if (delta > 0f) LagTracePlugin.OnFrameComplete(delta);
        }
    }

    /// <summary> /lag — quick server health snapshot </summary>
    public class CommandLag : IRocketCommand
    {
        public string Name => "lag";
        public string Help => "Show current TPS and top methods.";
        public string Syntax => "/lag";
        public List<string> Aliases => new();
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new() { "lagtrace.lag" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            float tps = 1f / Time.smoothDeltaTime;
            var top = LagTracePlugin.Sampler.GetTop(5);

            var sb = new StringBuilder();
            sb.AppendLine($"[LagTrace] TPS: {tps:F1}  FrameTime: {Time.smoothDeltaTime * 1000f:F1}ms");
            sb.AppendLine("Top 5 methods (self%):");
            if (top.Count == 0) { sb.AppendLine("  (no data yet — wait a few seconds)"); }
            else foreach (var e in top) sb.AppendLine($"  {CommandHelpers.Truncate(e.DisplayName, 55)}  {e.Pct:F1}%");

            CommandHelpers.Reply(caller, sb.ToString());
        }
    }

    /// <summary> /lagtop [n] [avg] — top methods, optionally sorted by avg time </summary>
    public class CommandLagTop : IRocketCommand
    {
        public string Name => "lagtop";
        public string Help => "Show top N heaviest methods. Usage: /lagtop [n] [avg]";
        public string Syntax => "/lagtop [n] [avg]";
        public List<string> Aliases => new() { "lt" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new() { "lagtrace.lagtop" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = LagTracePlugin.Instance.Configuration.Instance.TopN;
            bool byAvg = false;

            foreach (var a in args)
            {
                if (int.TryParse(a, out int parsed)) n = Math.Max(1, Math.Min(parsed, 50));
                if (a.Equals("avg", StringComparison.OrdinalIgnoreCase)) byAvg = true;
            }

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
            if (timed.Count == 0 && sampled.Count == 0) sb.AppendLine("  (no data yet)");

            CommandHelpers.Reply(caller, sb.ToString());
        }
    }

    /// <summary> /lagplugins [n] [engine|plugins] — per-source breakdown with category grouping </summary>
    public class CommandLagPlugins : IRocketCommand
    {
        public string Name => "lagplugins";
        public string Help => "Show CPU cost per plugin and engine manager. Filter: engine / plugins.";
        public string Syntax => "/lagplugins [n] [engine|plugins]";
        public List<string> Aliases => new() { "lp" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new() { "lagtrace.lagplugins" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            int n = 20;
            TrackerCategory? filter = null;

            foreach (var a in args)
            {
                if (int.TryParse(a, out int parsed)) n = Math.Max(1, Math.Min(parsed, 50));
                else if (a.Equals("engine", StringComparison.OrdinalIgnoreCase)) filter = TrackerCategory.Engine;
                else if (a.Equals("plugins", StringComparison.OrdinalIgnoreCase)) filter = TrackerCategory.Plugin;
            }

            var entries = LagTracePlugin.Sampler.GetTop(n, filter);

            if (entries.Count == 0)
            {
                CommandHelpers.Reply(caller, "[LagTrace] No attribution data yet — wait a few seconds.");
                return;
            }

            var sb = new StringBuilder();
            string filterLabel = filter == null ? "all" : filter == TrackerCategory.Engine ? "engine only" : "plugins only";

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
                    sb.AppendLine(e.Category == TrackerCategory.Engine ? "  ── Unturned engine ──" : "  ── Rocket plugins ──");
                    lastCat = e.Category;
                }

                int filled = (int)Math.Round(Math.Min(e.Pct, 100.0) / 5.0);
                string bar = new string('█', filled) + new string('░', 20 - filled);

                sb.AppendLine($"  {bar}  {e.Pct,5:F1}%  {CommandHelpers.Truncate(e.DisplayName, 38)}  ({e.Samples} samples)");
            }

            CommandHelpers.Reply(caller, sb.ToString());
        }
    }

    /// <summary> /lagspike — show last recorded lag spike </summary>
    public class CommandLagSpike : IRocketCommand
    {
        public string Name => "lagspike";
        public string Help => "Show the most recent lag spike call stack.";
        public string Syntax => "/lagspike [list]";
        public List<string> Aliases => new() { "ls" };
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new() { "lagtrace.lagspike" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            bool list = args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase);

            if (list)
            {
                var all = LagTracePlugin.Spikes.GetAll();
                if (all.Count == 0) { CommandHelpers.Reply(caller, "[LagTrace] No spikes recorded yet."); return; }
                var sb = new StringBuilder();
                sb.AppendLine("[LagTrace] Recent spikes:");
                foreach (var s in all) sb.AppendLine($"  {s.TimestampUtc:HH:mm:ss}  {s.FrameMs:F1}ms");
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

    /// <summary> /lagreset — clear all accumulated data </summary>
    public class CommandLagReset : IRocketCommand
    {
        public string Name => "lagreset";
        public string Help => "Reset all LagTrace timing data.";
        public string Syntax => "/lagreset";
        public List<string> Aliases => new();
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Permissions => new() { "lagtrace.reset" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            LagTracePlugin.Sampler.Reset();
            Timings.Reset();
            CommandHelpers.Reply(caller, "[LagTrace] All data cleared.");
        }
    }

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

    public abstract class CommandBase
    {
        protected static void Reply(IRocketPlayer p, string msg) => CommandHelpers.Reply(p, msg);
        protected static string Truncate(string s, int n) => CommandHelpers.Truncate(s, n);
    }
}
