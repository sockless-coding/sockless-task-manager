# Sockless Task Manager

A lightweight alternative to the Windows Task Manager, in the Sockless Workshop theme. Built with .NET 10 and Avalonia, published as a single Native AOT executable.

## What it does

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

Data comes from Windows APIs: performance counters via PDH, `GlobalMemoryStatusEx`, SMBIOS, storage IOCTLs and DXGI, plus `NtQuerySystemInformation` and process working-set queries for the Memory page. No WMI, no admin rights; run as administrator only to inspect elevated processes' address spaces.

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
