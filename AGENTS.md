# Agent instructions

Sockless Task Manager is a lightweight Windows Task Manager alternative: .NET 10, Avalonia 12, published with Native AOT. Read this before changing code.

## Non-negotiables

1. **Native AOT stays green.** `dotnet publish src/SocklessTaskManager -c Release -r win-x64` must succeed with no trim/AOT warnings (`IL2xxx`/`IL3xxx` are errors via `Directory.Build.props`). That means:
   - No reflection-based APIs: no `System.Management`/WMI, no `Activator`/`Type.GetType` lookups, no reflection view locators.
   - P/Invoke with `[LibraryImport]` (source-generated), never `[DllImport]`. Blittable structs; parse variable-length buffers by offset.
   - No built-in COM interop (`BuiltInComInteropSupport=false`). Call COM through vtable function pointers (see `Features/Performance/Gpu/GpuAdapter.cs`).
   - Every XAML file declares `x:DataType`; bindings are compiled.
2. **Sockless Workshop theme**, web-app surface. Guidelines live in the `sockless-guidelines` repo (`design/sockless/`). Read `principles.md` and `surfaces/web-app.md` before UI work; `foundations/*`, `components.md` and `accessibility.md` for exact values.
   - Dark only. No light theme.
   - **Tokens, not hex.** `Theme/Tokens.axaml` mirrors `tokens/workshop.css` and is the only file allowed to contain colour values. Views and styles use `Sl*` roles. If a token is missing, add it there (and say so) rather than inlining a colour.
   - Copper-light (`SlAccent`) means interactive or active. Brass warning, verdigris success, rust danger.
   - Status is never colour alone: pair it with a word, and give a secondary graph series a dash as well as a colour.
   - Uppercase is display-only: use `<ctl:CapsText Value="Sentence case" />`, never type capitals into source.
   - Infinite animations run only when `Motion.IsReduced` is false (Windows "Animation effects").
3. **Lightweight.** The app samples once a second and renders in software mode (see `Program.cs` for why). Measure before adding anything that runs per tick.

## Architecture: vertical slices

```
src/SocklessTaskManager/
  App.axaml(.cs)        composition root: the list of top-level features
  Core/                 the small shared kernel: Feature, SampleClock, History, Format, interop used by several slices
  Controls/             reusable visuals: HistoryGraph, GraphFrame, Icon, CapsText
  Theme/                Tokens.axaml (design tokens), Icons.axaml (Heroicons + gear), Controls.axaml (component styles)
  Shell/                main window: sidebar, top bar, page host
  Features/
    Performance/        a top-level feature (one sidebar entry)
      PerformanceModule.cs   the list of device scanners for this feature
      Cpu/ Memory/ Disk/ Network/ Gpu/   one sub-slice per device kind
    Memory/             a top-level feature: drill-down of physical memory (BreakdownTree.cs maps paths to levels)
```

A slice owns everything it needs: native calls, sensor, view model and view. Slices don't reference each other. Code moves into `Core/` only when a second slice needs it (PDH and `GetPerformanceInfo` are there for that reason).

### Sampling model

`SampleClock` ticks once a second. For each `ISampled` subscriber it calls `Sample()` on a background thread (read the system, store a snapshot, touch nothing bindable), then `Apply()` on the UI thread (push the snapshot into properties and `History` buffers). The next tick waits until `Apply()` has finished.

### Add a device kind to Performance (battery, NPU, sensors…)

1. Create `Features/Performance/<Kind>/`.
2. Add `<Kind>Device : PerformanceDevice` with:
   - `public static IEnumerable<DeviceSlot> Scan()` returning one slot per device present, each with a stable id. Scanners re-run every few seconds; ids that disappear are removed and disposed.
   - `Sample()` / `Apply()` as above, `Sparkline`, `Readouts`, `Details`, `AccentKey` (an `SlSeries*` token).
   - `CreateView()` returning `<Kind>View`.
3. Add `<Kind>View.axaml` (`x:DataType` = the device). Compose `perf:DeviceHeader`, a card with `ctl:GraphFrame` + `ctl:HistoryGraph`, and `perf:ReadoutPanel`.
4. Register the scanner in `PerformanceModule.cs`. List order is sidebar order.

### Add a top-level feature (Processes, Startup, Services…)

1. Create `Features/<Name>/` with a class deriving from `Core.Feature` (title, `IconKey` from `Theme/Icons.axaml`, `CreateView()`).
2. If it needs live data, implement `ISampled` and `clock.Subscribe(this)`.
3. Add it to the `features` array in `App.axaml.cs`.

## Build and run

```
dotnet build
dotnet run --project src/SocklessTaskManager
dotnet publish src/SocklessTaskManager -c Release -r win-x64 -o artifacts/publish
```

Native AOT linking needs the Visual Studio C++ build tools. If publish fails with "'vswhere.exe' is not recognized", add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`.
