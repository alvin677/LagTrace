# LagTrace — Unturned Rocketmod Profiler

A Spark-inspired profiling plugin for Unturned dedicated servers.  
Tells you **which code is eating your tick budget** without touching a production plugin's source.

---

## How it works

LagTrace runs two complementary collection strategies in parallel:

### 1 · Stack sampler (like Spark / async-profiler)
A background thread suspends the main Unity thread every **5 ms** (configurable),
captures a `StackTrace`, and increments hit counters per method.

- **Self %** = method was at the top of the stack (the leaf — actually executing).
- **Total %** = method appeared anywhere (including as a caller).

No instrumentation needed. Works on all code — Unturned engine, RocketMod core,
and every plugin simultaneously.

### 2 · Instrumented timing (Harmony patches)
LagTrace auto-patches the `FixedUpdate`, `Update`, and `LateUpdate` of every
`RocketPlugin` it finds at load time using Harmony prefix/postfix pairs.
These give you exact **wall-clock milliseconds** per call — more precise than
sampling but limited to patched methods.

You can also manually instrument your own code:
```csharp
using (Timings.Start("MyPlugin.HeavyLoop"))
{
    // ... expensive work ...
}
```

### 3 · Plugin attribution
Each sample is attributed to a plugin by checking whether the leaf frame's
namespace matches any registered plugin assembly. This powers `/lagplugins`.

### 4 · Spike detector
After every `LateUpdate`, the measured frame time is compared against
`SpikeThresholdMs`. When exceeded, the last sampled stack snapshot is saved
into a 20-slot ring buffer — inspectable with `/lagspike`.

---

## Commands

| Command | Permission | Description |
|---|---|---|
| `/lag` | `lagtrace.lag` | Quick snapshot: TPS, frame time, top 5 methods |
| `/lagtop [n] [avg]` | `lagtrace.lagtop` | Top N methods. Add `avg` to sort by average call time instead of total |
| `/lagplugins [n]` | `lagtrace.lagplugins` | CPU % attributed to each plugin |
| `/lagspike` | `lagtrace.lagspike` | Last spike call stack |
| `/lagspike list` | `lagtrace.lagspike` | List all recorded spikes (timestamp + ms) |
| `/lagreset` | `lagtrace.reset` | Clear all accumulated data |

---

## Configuration (`LagTrace.xml`)

```xml
<SampleIntervalMs>5</SampleIntervalMs>      <!-- sampler resolution -->
<WindowSeconds>60</WindowSeconds>           <!-- auto-print interval -->
<SpikeThresholdMs>50</SpikeThresholdMs>     <!-- ms to trigger spike capture -->
<AutoPrint>false</AutoPrint>                <!-- auto-log top 10 each window -->
<TopN>10</TopN>                             <!-- default rows -->
```

---

## Installation

1. Build with `dotnet build -c Release`.
2. Drop `LagTrace.dll` + `0Harmony.dll` into your Rocket `Plugins` folder.
3. Restart the server. Permissions are added automatically to `Permissions.config.xml`.

> **Harmony**: grab `0Harmony.dll` from the  
> [Harmony GitHub releases](https://github.com/pardeike/Harmony/releases) (2.x, net48 build).  
> If another plugin already ships Harmony 2.x you may not need a second copy — check first.

---

## Known limitations

- `Thread.Suspend` / `Thread.Resume` are deprecated in .NET and may warn at build
  time. They remain functional on .NET Framework 4.x and Mono (which Unturned uses).  
  A future version may switch to a cooperative safepoint approach.
- The sampler adds ~0.1–0.3% CPU overhead at 5 ms intervals — negligible on any
  server that can run Unturned.
- Plugin attribution works on namespace prefix matching. Plugins without a unique
  root namespace may be mis-attributed; rename your namespace if this occurs.

---

## Roadmap

- [ ] `/lag` TPS graph (last 60 s rolling)
- [ ] Web UI (local HTTP server, similar to Spark's flame graph)
- [ ] Per-player command timings (track cost of heavy commands)
- [ ] Configurable sampler depth cap
