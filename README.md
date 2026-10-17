# Sockless Task Manager

A lightweight alternative to the Windows Task Manager, in the Sockless Workshop theme. Built with .NET 10 and Avalonia, published as a single Native AOT executable.

## What it does

The **Processes** page lists every running process with its PID, user, CPU, memory (private working set) and I/O rate:

- **List** or **Tree**: one sorted list, or each process under the process that started it, with collapsible branches.
- **Search** by name, path or PID (Ctrl+F; Esc clears). In the tree, a match's ancestors stay visible in quieter text so you can see what started it.
- **Filters**: only followed processes, or only processes in your own Windows session (hides services and system processes).
- **Columns**: add threads, handles, working set, commit, priority, session, start time, file description, path or command line from the Columns button or by right-clicking the column titles. The choice and the sort are remembered. When they don't fit, the table scrolls sideways. Without elevation the User column only names your own processes.
- **Column filters** (the funnel): a filter box under each column. Text: `chrome`, `=SYSTEM`, `!svchost`, `chrome|edge`. Numbers: `> 5`, `<= 2`, `10-50`; memory and I/O take units (`> 100 MB`, `> 1 MB/s`, MB if none). Turning the filter row off clears them.
- **Sort** by any column; the order holds for a few seconds at a time so rows don't jump under the pointer.
- **Follow** a process with the star: it gets a card below the table with a minute of CPU and memory history. Cards keep recording while you're on other pages, and show "Exited" when the process ends.
- **Details**: select a row (or move with the arrow keys) to open its details beside the table: live CPU and memory, path, command line, parent, user, start time, threads, handles and memory figures.
- **Actions** from the details or a row's right-click menu: end task (Delete) or end the whole process tree, suspend and resume, change priority or processor affinity, open the file location. Ending a process always asks first, and names what it will end; an id that now belongs to a different process is never touched.
- **Ended**: processes that have exited, newest first, with how long they ran and their exit code; filter to short-lived ones. Run as administrator and every process is caught by a kernel trace, even ones that last a few milliseconds, all the time. Without it, only processes that ran for at least a second while the page was sampling are listed.

The **Performance** page shows live graphs and figures for each device, the way Task Manager's Performance tab does:

| Device | Graphs | Figures |
|---|---|---|
| CPU | Overall utilisation, or one graph per logical processor | Utilisation, speed, processes, threads, handles, up time; base speed, sockets, cores, caches, virtualisation |
| Memory | Memory in use | In use, available, committed, cached, paged and non-paged pool; speed, slots, form factor, hardware reserved |
| Disk (each) | Active time, read/write transfer rate | Response time, read and write speed; capacity, type (SSD/HDD, NVMe/SATA/USB), system disk |
| Network (each adapter) | Receive/send throughput | Rates; link speed, connection type, IPv4/IPv6 addresses |
| GPU (each) | 3D, Copy, Video Encode, Video Decode engines; dedicated memory | Utilisation, dedicated and shared memory; driver version |

Data is sampled once a second. Pause from the top bar. Disks and adapters that are plugged in or removed appear and disappear within a few seconds.

The **Memory** page shows what the RAM is holding and lets you drill into each part:

- **In use**: processes (grouped by name, then each process's private data, EXE/DLL images and mapped files), compressed memory, files shared between processes, kernel pools by tag (and the drivers that use each tag), driver code, and the system file cache.
- **Modified**, **Standby** (by priority) and **Free** (zeroed or not).

It only reads what the level on screen needs, and only while the page is visible. Address-space and driver-file scans run once when opened, with a Refresh button.

Data comes from Windows APIs: performance counters via PDH, `GlobalMemoryStatusEx`, SMBIOS, storage IOCTLs and DXGI, plus `NtQuerySystemInformation` and process working-set queries for the Processes and Memory pages. No WMI, no admin rights; run as administrator to end or change other users' processes, to trace every process that starts and ends (ETW), and to inspect elevated processes' address spaces.

## Footprint

Measured on a Ryzen 7 9800X3D, Windows 11, release AOT build:

- Publish folder: about 28 MB (15 MB executable plus Skia and HarfBuzz).
- About 0.3% CPU and 50 MB private memory while sampling.

The UI uses Avalonia's software renderer. Its GPU renderer loads ANGLE and the graphics driver, which cost about 170 MB more memory for no visible gain at one update a second.

## Requirements

- Windows 10 or 11, x64.
- To build: .NET 10 SDK. To publish with Native AOT: Visual Studio C++ build tools.

## Build

```
dotnet run --project src/SocklessTaskManager
dotnet publish src/SocklessTaskManager -c Release -r win-x64 -o artifacts/publish
```

## Project layout

The code is organised in vertical slices: each feature, and each device kind inside Performance, keeps its native calls, sensor, view model and view together. [AGENTS.md](AGENTS.md) describes the structure, the Native AOT rules and how to add a feature or device kind.

## Credits

- Fonts: [Oswald](https://github.com/googlefonts/OswaldFont), [Raleway](https://github.com/impallari/Raleway) and [Roboto Mono](https://github.com/googlefonts/robotomono), SIL Open Font License 1.1 (licence texts in `src/SocklessTaskManager/Assets/Fonts`).
- Icons: [Heroicons](https://heroicons.com) v2.2.0, MIT License.
- UI framework: [Avalonia](https://avaloniaui.net), MIT License.
