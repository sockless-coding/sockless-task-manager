# Agent instructions

Sockless Task Manager is a lightweight Windows Task Manager alternative: .NET 11 (C# 15), Avalonia 12, published with Native AOT. Read this before changing code.

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
  Core/                 the small shared kernel: Feature, SampleClock, Navigator, History, Format, interop used by several slices
  Controls/             reusable visuals: HistoryGraph, GraphFrame, Icon, CapsText, CompositionBar, ShareBar, Treemap
  Theme/                Tokens.axaml (design tokens), Icons.axaml (Heroicons + gear), Controls.axaml (component styles)
  Shell/                main window: sidebar, top bar, page host
  Features/
    Processes/          a top-level feature: process list and tree, search, filters, followed processes,
                        details and actions (end, suspend, priority, affinity, full or heap dump file), ended processes (ETW when elevated),
                        a column chooser and column filters (TableColumn.cs, ColumnFilter.cs). Home of a process's details:
                        other pages open a process (or a user's processes) here with a ProcessQuery through the Navigator
                        (ProcessList.Show), and the page offers a link back to where they came from. A line in
                        the details pane opens the memory makeup page (ProcessMemoryMap: private data, DLLs, mapped files),
                        which takes the page's place until Esc, as the .NET runtime page does.
      Dotnet/           .NET processes: GC and runtime counters, heap-by-type snapshots (retained sizes, compared
                        with the one before), allocations and thrown exceptions, over the diagnostics IPC pipe and a
                        hand-written nettrace reader that keeps Native AOT green (TraceEvent relies on reflection).
                        The sampler lists the pipes every few seconds to mark .NET rows. A line in the details pane
                        opens ManagedMemoryView (".NET runtime"), which takes the page's place until Esc;
                        allocations and exceptions (RuntimeFeed) are followed only while it's open. Two traps:
                        - DiagnosticsPort sends one command per process at a time, and a session closes its pipe
                          before it's stopped. Overlapping starts and stops hang the runtime's diagnostics server
                          for good, and a stop with a full, unread pipe blocks it.
                        - A heap walk needs the session buffer sized to the heap, or the runtime drops the rest
                          silently: NettraceReader.LostEvents is how that's noticed.
    Performance/        a top-level feature (one sidebar entry)
      PerformanceModule.cs   the list of device scanners for this feature
      Cpu/ Memory/ Disk/ Network/ Gpu/   one sub-slice per device kind
    Memory/             a top-level feature: drill-down of physical memory (BreakdownTree.cs maps paths to levels).
                        Processes are listed by share of memory; opening one shows it on the Processes page.
    Users/              a top-level feature: signed-in sessions (WTS, Sessions.cs) with their processes' CPU and
                        memory, disconnect and sign out; or every account that owns a process (token owners, read
                        once per process). The details pane lists their processes; each opens on the Processes page, and
                        "Show in Processes" opens the page filtered to that user. Without elevation, other users' and services' owners can't be read and
                        are listed as "Not readable".
    Storage/            a top-level feature: drives, and a scan of one as a treemap (Controls/Treemap) over a table to
                        drill into. DiskScan lists folders with GetFileInformationByHandleEx (allocation sizes, no
                        junctions followed) on a few below-normal threads; elevated, it enables SeBackupPrivilege.
                        To stay light, a folder under ~1/20,000 of the drive's used space keeps only its totals once
                        counted (IsSummary) and is read again when opened; only the 50 largest files per folder are
                        kept by name. "Rescan folder" re-reads one folder and splices it into the tree.
                        Treemap boxes and rows share a context menu (StorageTarget); deletes go through SHFileOperationW
                        on an STA thread after a Confirmation, then come out of the tree without a rescan.
    Network/            a top-level feature: open connections, listening ports (with who can reach them), a history of
                        every connection seen while monitoring, and processes with their traffic. NetworkMonitor keeps
                        the record on the sampling thread (TCP/UDP tables via GetExtendedTcpTable/UdpTable, the
                        "owner module" classes) and publishes only what changed; NetworkPage mirrors it on the UI
                        thread and renders the view on screen (at most 1,000 rows). Elevated, NetworkTrace adds
                        traffic, sub-second connections and UDP (Microsoft-Windows-Kernel-Network), DNS answers
                        (DNS-Client 3008) and process starts. Things to know:
                        - Kernel-Network doesn't reliably say which end is local; the monitor orients each flow
                          against the socket table, then this computer's addresses.
                        - Some Windows builds report TCP state 0 for many sockets: no state and no remote end
                          means listening.
                        - Host names: DNS answers (cache via the undocumented DnsGetCacheDataTable, or the trace)
                          beat reverse lookups, which are optional because they send queries.
                        - Geo data never ships. GeoDatabase is a hand-written, memory-mapped .mmdb reader
                          (DB-IP, GeoLite2, IPinfo); GeoLocator downloads DB-IP Lite or imports a file into
                          %LOCALAPPDATA%\Sockless Task Manager\GeoIP. Show DB-IP's attribution where its data is shown.
                        - The Map view (only with a location database) draws WorldMap.bin, Natural Earth country
                          outlines and label points embedded as a resource (tools/New-WorldMap.ps1 rebuilds it). It's
                          not IP data; city coordinates come from the database, country dots from the label points.
                        - Monitoring runs only while the page is shown, or in the background for watched process
                          names or everything (network.ini); otherwise it costs nothing.
    Services/           a top-level feature: Windows services (and drivers on request), grouped by publisher or in one
                        list. ServiceManager wraps the SCM (enumerate, config, start/stop/pause, change, delete);
                        actions run off the UI thread and wait on the service's state. ServicePublisher groups by
                        the company in the program's version resource (svchost services by their ServiceDll):
                        Microsoft files under the Windows folder are "Windows", elsewhere "Microsoft"; a missing
                        program is its own group. Configs are read once per service, then a few per tick.
                        Stopping a service whose dependents run asks first and stops them too, as services.msc does.
                        The edit form has services.msc's pages: General, Log on (a person's account is granted
                        SeServiceLogonRight through the LSA, or the service won't start) and Recovery (failure
                        actions; Windows repeats the last one, and a computer restart needs SeShutdownPrivilege).
    Drivers/            a top-level feature, in five tabs. Devices: every device Windows knows (CfgMgr32, Sources/
                        DeviceTree.cs: CM_Get_Device_ID_List and DEVPKEY properties) by class, with Device Manager's
                        problem codes in words (DriverText). Kernel modules: what's loaded now (Core/Interop/
                        KernelModules), matched to driver services (the Services key) and signature-checked. Updates:
                        Windows Update's driver offers (Updates/WindowsUpdate.cs, the WUA COM API by vtable; slots
                        from wuapi.h), matched to devices by hardware id; an INF from disk installs with newdev
                        (DriverPackages: DiInstallDriver, or UpdateDriverForPlugAndPlayDevices for one device, which
                        asks before forcing a driver Windows ranks lower). Installing needs an administrator.
                        Changes: the Kernel-PnP configuration log and the System log (EventLog.cs, wevtapi, events
                        rendered as XML and read by hand to keep System.Xml out), plus DriverMonitor, which follows
                        CM_Register_Notification from app start and diffs only the devices that changed.
                        Crashes (CrashHistory): stop errors, unexpected restarts, display driver resets and live
                        kernel events from the System and Application logs, with the driver blamed by WER's bucket
                        or, elevated, by the dump (KernelDump: header by offset, then a minidump's driver list and
                        stack). Things to know:
                        - WER logs a report again on every upload retry: one row per report id, dated by its dump.
                        - The dump stack loads storage drivers as dump_<name>.sys; the file is <name>.sys.
                        - Drivers that passed Microsoft's hardware program are signed "Microsoft Windows Hardware
                          Compatibility Publisher": not Windows' own.
                        - Nothing runs per tick: scans on first show, Refresh and device changes; Windows Update is
                          asked on first show and "Check again".
    Autostart/          a top-level feature: everything Windows starts or loads on its own, as Autoruns lists it.
                        Sources/ reads each kind: Run keys, Startup folders and packaged apps' startup tasks
                        (SignInSources), scheduled tasks (Task Scheduler COM by vtable, TaskScheduler.cs: the
                        Tasks folder and TaskCache are admin-only, the service isn't), services and drivers (the
                        Services key, not the Services slice), Explorer and browser add-ins and per-user COM class
                        overrides, Office add-ins, Winlogon, boot, program-launch, AppInit, LSA, network, print and
                        time providers, cmd and PowerShell start-up, and WMI subscriptions (WMI COM by vtable).
                        AutostartScanner reads the files' version info, then checks signatures (SignatureChecker:
                        WinVerifyTrust, embedded then catalog, never online) and publishes as it goes. It scans when
                        first shown, on Refresh and after a change; nothing runs per tick. Things to know:
                        - Off is always undoable (AutostartChanges): Windows' own switch where there is one
                          (StartupApproved, a task's Enabled, a packaged task's State, a service's startup type,
                          remembered under HKLM\SOFTWARE\Sockless Task Manager), else the value or key moved into a
                          "Sockless disabled" key beside it, which every source skips when listing.
                        - Removing saves first (.reg, task XML, or the file) in %LOCALAPPDATA%\Sockless Task
                          Manager\Autostart backups. Services and drivers are uninstalled on the Services page.
                        - "Windows' own" (hidden by default) means signed by Microsoft as part of Windows, not
                          just a Microsoft company name; Windows' own entries in critical categories (drivers,
                          Winlogon, boot, LSA, network, print, AppInit) can't be changed here.
    Apps/               a top-level feature: everything installed, however it was installed. Sources/ reads each
                        way: the uninstall registry (InstalledPrograms: HKLM 64/32-bit and HKCU, told apart as
                        Windows Installer, Inno Setup, NSIS, InstallShield, WiX bundle, Squirrel, ClickOnce, Steam,
                        winget portable), MSIX packages (PackagedApps: Windows.Management.Deployment by vtable,
                        WinRt.cs; slots and IIDs from the SDK's winrt headers), Scoop and Chocolatey from their
                        folders (PackageManagers), and optional features and Windows features (OptionalFeatures:
                        dismapi.dll, administrators only, structs read by offset). AppScanner publishes as each
                        source answers and keeps DISM's answer until Refresh, since it's slow and loads a lot.
                        Each app is uninstalled, changed or repaired the way it was installed (AppActions): msiexec
                        with the product code, the bundle's /modify and /repair, RemovePackageAsync, DISM, or the
                        package manager in a console. Things to know:
                        - Many uninstallers hand over to a copy of themselves and exit at once: AppWatcher
                          (RegNotifyChangeKeyValue, thread-agnostic, on the pool) is how the list learns they
                          finished. It follows the registry from first show; changes while away rescan on return.
                        - Frameworks, system components, Windows' own packages and updates are hidden behind
                          "Components"; Windows' own packages and frameworks can't be uninstalled here.
                        - An entry whose uninstaller is gone can be removed (a .reg backup first, Core/Backups).
                        - Only http(s) links from the registry reach the shell.
                        - The selected app's running processes and size on disk (AppFolder) are read once, on
                          select; "Show in Processes" uses ProcessQuery.Folder.
                        - Updates (Updates/): winget's `upgrade` table (WingetTable: redirected, it's full width;
                          columns found where the translated titles start, wide characters shifted), matched to
                          apps by name then version; `choco outdated -r`; Scoop against its local buckets. Asked
                          online on first show, Refresh and after updating, never per tick. An update holds while
                          the app's version is the one it had when checked. Updating runs the updater in a window;
                          "Update all" does them one by one and can stop between. Not the top bar's self-update.
    Updates/            not a page: the top bar's update offer. Reads the latest tag from GitHub's /releases/latest
                        redirect, downloads the release zip, checks its .sha256, renames running files to .old and
                        moves the new ones in, then restarts. Local builds are version 0.0.0 and never check;
                        the asset names must match release.yml.
```

A slice owns everything it needs: native calls, sensor, view model and view. Slices don't reference each other. Code moves into `Core/` only when a second slice needs it (PDH, `GetPerformanceInfo` and `NtQuerySystemInformation` with its process-list parser are there for that reason, as are `Accounts` (process token owners), `Elevation` and `Confirmation`, shared by Processes and Users, `AddressSpace`, shared by Processes and Memory, `EtwSession` (a real-time ETW session) with the kernel's process-start event, shared by Processes and Network, and `Privileges` (enable a token privilege), shared by Storage and Services, `CommandLine` (the program in a
registered command line), shared by Services, Autostart and Apps, `Com` (COM by vtable) and `SignatureChecker`, shared by
Autostart and Drivers, `KernelModules`, shared by Memory and Drivers, and `Backups` (.reg exports and the backups folders,
opening Registry Editor at a key), shared by Autostart and Apps).

When one slice needs to show another's page, it goes through `Core/Navigator.cs`: Users and Memory call `ShowProcesses(new ProcessQuery(...))`, the Processes feature applies it and calls `Open(this)`, and the shell selects it. Pages may list processes in their own terms (shares of memory, a user's processes), but a process's details, actions and drill-downs live only on the Processes page: a row elsewhere opens it there rather than growing details of its own. If a page needs more than `ProcessQuery` offers, add it there.

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
