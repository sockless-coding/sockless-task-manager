# Sockless Task Manager

A lightweight alternative to the Windows Task Manager, in the Sockless Workshop theme. Built with .NET 11 and Avalonia, published as a single Native AOT executable.

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

The **Storage** page lists the drives and, when you open one, scans it to show what takes the space: a treemap of the folder you're in, with each folder's contents nested inside it two levels deep, above a table of its folders and files, largest first. Point at a box or row for its path, counts and largest contents; select a folder in either (nested ones too) to drill in. Right-click a box or row to open it, show it in File Explorer, copy its path, see its Properties, rescan it, or delete it (to the Recycle Bin, or permanently). Deletes ask first, and core folders (Windows, Program Files, ProgramData, user profiles) can't be deleted from here. Sizes are space on disk, so cloud placeholders and compressed files count for what they really use; space the scan can't attribute (file system metadata, restore points, folders it can't read) is shown as "System and unreadable". Run as administrator to read every folder.

The **Network** page is for finding out what is talking to what. It lists open connections and listening ports with the process (and, for svchost.exe, the services) behind each, where the other end is, and a history of every connection seen while monitoring, closed ones and their first and last sightings included. Listening ports say who can reach them (this computer only, the local network, or every network) and what the port is usually for. A Processes view adds up each process's connections, remote hosts and traffic; selecting a row shows its details, with a link to the process on the Processes page. Narrow a view with filter chips (protocol, direction, whether the other end is on the internet, the local network or this computer, open or closed, short-lived, with traffic; for ports, who can reach them) and a search that takes words and filters together: `port:443 country:SE -svchost`, `pid:`, `process:`, `port:1-1024`, `ip:`, `host:`, `org:` or `org:AS15169`, `proto:`, `state:`, `dir:in`, with `-` to exclude. On the Processes view, `country:RU` finds the processes that talked to Russia. Sort by any column, and export what a view matches as CSV. Reset a connection from its details (IPv4, as administrator).

- **Host names**: the names apps looked up, from Windows' DNS cache (and, as administrator, from tracing lookups as they happen), which sends nothing; reverse lookups for the rest can be turned off.
- **Locations and network owners**: from a MaxMind DB (.mmdb) file on this computer. None ships with the app: download DB-IP's free country, city or ASN database from Settings, or add one you have (GeoLite2, IPinfo Lite).
- **Map**: with a location database installed, a world map shows where the other ends are: a dot per city (or per country, with a country-only database) sized by its connections, countries shaded by how many end there, and a line from your Windows region to each place, solid for outgoing and dashed for incoming. Zoom with the wheel, drag to move; select a place for its connections, processes and networks. The filters and search apply to it too.
- **Recording**: the page records while it's open. Watch a process (by name, so restarts count) to keep recording its connections while the page is closed, or record everything in the background.
- **As administrator**, an ETW trace adds bytes sent and received per connection and per process (with a graph), connections that open and close between two samples, UDP conversations, and names for processes that exit within a second.

The **Autostart** page lists everything Windows starts or loads on its own, grouped by where it starts from: what Task Manager's Startup apps shows (Run keys, Startup folders, packaged apps' startup tasks), and the places it doesn't: RunOnce and policy Run lists, Active Setup, sign-in and Group Policy scripts, scheduled tasks, services and drivers, Explorer and browser add-ins, COM classes your own registry redefines, Office add-ins, Winlogon, boot-time programs, programs started in place of others (Image File Execution Options), DLLs loaded into many programs (AppInit), security, network, print and time providers, Command Prompt and PowerShell start-up, and WMI event subscriptions.

- **Who made it**: each entry's file is checked for a digital signature (its own, or a Windows catalog's), offline. Entries whose file is unsigned, doesn't verify or isn't there are flagged; **To check** shows only those.
- **Windows' own** entries (files Microsoft signed as part of Windows) are hidden until you ask for them. Microsoft's apps, such as OneDrive, Edge and Teams, are listed like any other.
- **Turn off** keeps an entry from starting and can be undone: Run values and Startup folder items are switched the way Task Manager does, tasks are disabled, services and drivers set to Disabled (and back to what they were), other entries moved aside. **Remove** deletes it for good after saving a backup (a .reg file, the task's XML or the file itself). Windows' own entries in places Windows needs to start or sign in can't be changed here.
- Search, sort, list or group, open the file's folder, or open the registry key in Registry Editor. Your own entries can be changed as you are; entries for everyone, services and drivers need administrator rights, and WMI subscriptions and other users' tasks are only listed for an administrator.

Data comes from Windows APIs: performance counters via PDH, `GlobalMemoryStatusEx`, SMBIOS, storage IOCTLs and DXGI, plus `NtQuerySystemInformation` and process working-set queries for the Processes and Memory pages, directory listings (`GetFileInformationByHandleEx`) for the Storage page, and the TCP/UDP tables (`GetExtendedTcpTable`, `GetExtendedUdpTable`) and the DNS client cache for the Network page. No WMI, no admin rights; run as administrator to end or change other users' processes, to trace every process that starts and ends and every connection's traffic (ETW), and to inspect elevated processes' address spaces.

## Footprint

Measured on a Ryzen 7 9800X3D, Windows 11, release AOT build:

- Publish folder: about 32 MB (20 MB executable plus Skia and HarfBuzz).
- About 0.3% CPU and 50 MB private memory while sampling.

The UI uses Avalonia's software renderer. Its GPU renderer loads ANGLE and the graphics driver, which cost about 170 MB more memory for no visible gain at one update a second.

## Requirements

- Windows 10 or 11, x64.
- To build: .NET 11 SDK. To publish with Native AOT: Visual Studio C++ build tools.

## Build

```
dotnet run --project src/SocklessTaskManager
dotnet publish src/SocklessTaskManager -c Release -r win-x64 -o artifacts/publish
```

## Releases

Compiled builds are on the [Releases](https://github.com/sockless-coding/sockless-task-manager/releases) page: unzip and run `SocklessTaskManager.exe`.

A release build checks for a newer release shortly after it starts and twice a day after that. When it finds one, an update button appears in the top bar: it downloads the zip, checks its SHA-256, replaces the files and restarts. If the app's folder can't be written to (Program Files without administrator rights), the button opens the release page instead. Builds made locally are version 0.0.0 and never check.

To make a release, create one on GitHub with a new year.month.rev tag (no `v` prefix), where rev counts releases within the month. The new tag runs the release workflow, which publishes with Native AOT, zips the output and attaches the zip and its `.sha256` to the release. A tag with a hyphen is marked as a prerelease. Pushing a tag on its own, or running the workflow by hand with a tag, creates the release with generated notes:

```
git tag 2026.10.1
git push origin 2026.10.1
```

Every push and pull request also runs the same AOT publish, so a change that breaks Native AOT fails the build.

## Project layout

The code is organised in vertical slices: each feature, and each device kind inside Performance, keeps its native calls, sensor, view model and view together. [AGENTS.md](AGENTS.md) describes the structure, the Native AOT rules and how to add a feature or device kind.

## Credits

- Fonts: [Oswald](https://github.com/googlefonts/OswaldFont), [Raleway](https://github.com/impallari/Raleway) and [Roboto Mono](https://github.com/googlefonts/robotomono), SIL Open Font License 1.1 (licence texts in `src/SocklessTaskManager/Assets/Fonts`).
- Icons: [Heroicons](https://heroicons.com) v2.2.0, MIT License.
- UI framework: [Avalonia](https://avaloniaui.net), MIT License.
- Map outline: [Natural Earth](https://www.naturalearthdata.com), public domain (`tools/New-WorldMap.ps1` builds the embedded copy).
- IP geolocation, when you download it from the Network page's settings: [DB-IP](https://db-ip.com) Lite databases, CC BY 4.0. Not included in the app.
