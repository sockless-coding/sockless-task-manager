using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Services;

/// <summary>SERVICE_STATUS_PROCESS.dwCurrentState.</summary>
internal enum ServiceState
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

/// <summary>How a service starts. Automatic (delayed) is Automatic plus the delayed-start flag.</summary>
internal enum StartMode
{
    Boot,
    System,
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

/// <summary>One entry of the service list: what <c>EnumServicesStatusExW</c> says, every sample.</summary>
/// <param name="Type">SERVICE_WIN32_OWN_PROCESS and friends.</param>
/// <param name="Controls">SERVICE_ACCEPT_* bits the service takes right now.</param>
/// <param name="ProcessId">0 while it isn't running.</param>
internal readonly record struct ServiceStatus(
    string Name,
    string DisplayName,
    uint Type,
    ServiceState State,
    uint Controls,
    int ProcessId,
    int ExitCode);

/// <summary>What Windows does when a service fails: SC_ACTION_*.</summary>
internal enum RecoveryAction
{
    None = 0,
    Restart = 1,
    Reboot = 2,
    RunCommand = 3,
}

/// <summary>
/// SERVICE_FAILURE_ACTIONS and its flag: what the Service Control Manager does after the first,
/// second and later failures, and when it forgets them. A failure is the process ending without the
/// service saying it stopped; with <paramref name="OnErrorStops"/>, also a stop with an error code.
/// </summary>
/// <param name="ResetSeconds">Failures are counted afresh after this long without one; <see cref="Never"/> keeps the count.</param>
/// <param name="RestartDelay">Milliseconds before a restart.</param>
/// <param name="RebootDelay">Milliseconds before the computer restarts.</param>
/// <param name="CommandDelay">Milliseconds before the program runs. The form keeps what Windows has.</param>
/// <param name="Command">The program a run-a-program action starts, with its arguments.</param>
/// <param name="RebootMessage">Sent to signed-in users before a restart; empty for Windows' own.</param>
internal sealed record ServiceRecovery(
    RecoveryAction First,
    RecoveryAction Second,
    RecoveryAction Subsequent,
    uint ResetSeconds,
    uint RestartDelay,
    uint RebootDelay,
    uint CommandDelay,
    string Command,
    string RebootMessage,
    bool OnErrorStops)
{
    /// <summary>INFINITE: the failure count is never reset.</summary>
    public const uint Never = uint.MaxValue;

    /// <summary>A minute, as the Services console suggests.</summary>
    public const uint DefaultDelay = 60_000;

    /// <summary>What a service has when nothing was set up.</summary>
    public static ServiceRecovery Nothing { get; } = new(RecoveryAction.None, RecoveryAction.None, RecoveryAction.None,
        0, DefaultDelay, DefaultDelay, 0, string.Empty, string.Empty, false);

    public bool Uses(RecoveryAction action) => First == action || Second == action || Subsequent == action;
}

/// <summary>What <c>QueryServiceConfigW</c> and <c>QueryServiceConfig2W</c> say: read once per service, and again after a change.</summary>
/// <param name="Type">SERVICE_WIN32_OWN_PROCESS and friends, with SERVICE_INTERACTIVE_PROCESS.</param>
/// <param name="ImagePath">The command line as registered, arguments included.</param>
/// <param name="Account">Who it runs as: LocalSystem, NT AUTHORITY\LocalService, .\someone… Empty for drivers.</param>
/// <param name="Dependencies">Services (and "+group" load-order groups) it needs running first.</param>
internal sealed record ServiceConfig(
    uint Type,
    StartMode Start,
    string ImagePath,
    string Account,
    string Description,
    IReadOnlyList<string> Dependencies,
    uint ErrorControl,
    bool HasTriggers,
    ServiceRecovery Recovery)
{
    /// <summary>Allowed to interact with the desktop. Only LocalSystem may, and since Vista it reaches only session 0's.</summary>
    public bool IsInteractive => (Type & ServiceManager.InteractiveProcess) != 0;
}

/// <summary>What the edit form changes; null fields are left as they are.</summary>
/// <param name="Type">The service type, to set or clear SERVICE_INTERACTIVE_PROCESS.</param>
internal sealed record ServiceChange(
    string? DisplayName,
    string? Description,
    StartMode? Start,
    string? ImagePath,
    string? Account,
    string? Password,
    uint? Type,
    ServiceRecovery? Recovery);

/// <summary>
/// The Service Control Manager, through advapi32. Listing and reading need no elevation; starting,
/// stopping, changing and deleting most services do, and Windows says so with error 5. Every call
/// here can block on services.exe (and a stop or start waits for the service itself), so the
/// actions run off the UI thread.
/// </summary>
internal static unsafe partial class ServiceManager
{
    // Access rights.
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceEnumerateDependents = 0x0008;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServicePauseContinue = 0x0040;
    private const uint Delete = 0x00010000;

    // Service types.
    public const uint KernelDriver = 0x01;
    public const uint FileSystemDriver = 0x02;
    public const uint Win32OwnProcess = 0x10;
    public const uint Win32ShareProcess = 0x20;
    public const uint UserService = 0x40;
    public const uint UserServiceInstance = 0x80;
    public const uint InteractiveProcess = 0x100;
    private const uint DriverTypes = KernelDriver | FileSystemDriver | 0x08;
    private const uint Win32Types = Win32OwnProcess | Win32ShareProcess;

    // Controls.
    public const uint AcceptStop = 0x1;
    public const uint AcceptPauseContinue = 0x2;
    private const uint ControlStop = 0x1;
    private const uint ControlPause = 0x2;
    private const uint ControlContinue = 0x3;

    private const uint StateAll = 0x3;
    private const uint StateActive = 0x1;
    private const uint NoChange = 0xFFFFFFFF;
    private const int ConfigDescription = 1;
    private const int ConfigFailureActions = 2;
    private const int ConfigDelayedAutoStart = 3;
    private const int ConfigFailureActionsFlag = 4;
    private const int ConfigTriggerInfo = 8;

    // Errors.
    public const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    public const int ErrorDependentServicesRunning = 1051;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    // ENUM_SERVICE_STATUS_PROCESSW (64-bit): two string pointers, then SERVICE_STATUS_PROCESS.
    private const int EntrySize = 56;
    private const int EntryDisplayName = 8;
    private const int EntryStatus = 16;

    // ENUM_SERVICE_STATUSW (64-bit): two string pointers, then SERVICE_STATUS.
    private const int DependentSize = 48;

    // QUERY_SERVICE_CONFIGW (64-bit).
    private const int ConfigServiceType = 0;
    private const int ConfigStartType = 4;
    private const int ConfigErrorControl = 8;
    private const int ConfigBinaryPath = 16;
    private const int ConfigDependencies = 40;
    private const int ConfigStartName = 48;

    /// <summary>The longest a start, stop, pause or continue is waited for, whatever the service hints.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    /// <summary>Every Win32 service (and with <paramref name="drivers"/>, every driver) with its state. Empty if the SCM can't be reached.</summary>
    public static List<ServiceStatus> List(bool drivers)
    {
        var services = new List<ServiceStatus>(400);
        var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (manager == 0)
            return services;

        try
        {
            var type = drivers ? Win32Types | DriverTypes : Win32Types;
            uint resume = 0;
            var buffer = new byte[128 * 1024];
            while (true)
            {
                bool done;
                uint needed, returned;
                int error;
                fixed (byte* b = buffer)
                {
                    done = EnumServicesStatusEx(manager, 0, type, StateAll, b, (uint)buffer.Length, out needed, out returned, ref resume, null);
                    error = done ? 0 : Marshal.GetLastPInvokeError();
                    for (var i = 0; i < returned; i++)
                    {
                        var entry = b + i * EntrySize;
                        var name = *(char**)entry;
                        var display = *(char**)(entry + EntryDisplayName);
                        var status = (StatusProcess*)(entry + EntryStatus);
                        if (name is null)
                            continue;

                        services.Add(new ServiceStatus(
                            new string(name),
                            display is null ? new string(name) : new string(display),
                            status->ServiceType,
                            (ServiceState)status->CurrentState,
                            status->ControlsAccepted,
                            (int)status->ProcessId,
                            (int)status->Win32ExitCode));
                    }
                }

                if (done || error != ErrorMoreData)
                    break;
                if (returned == 0)
                    buffer = new byte[Math.Max(needed, (uint)buffer.Length * 2)];
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        return services;
    }

    /// <summary>The service's configuration, or null if it can't be opened (gone, or its DACL says no).</summary>
    public static ServiceConfig? ReadConfig(string name)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
            return null;

        try
        {
            var service = OpenService(manager, name, ServiceQueryConfig);
            if (service == 0)
                return null;

            try
            {
                return ReadConfig(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static ServiceConfig? ReadConfig(nint service)
    {
        var config = Query(service, static (h, b, size, out needed) => QueryServiceConfig(h, b, size, out needed));
        if (config is null)
            return null;

        StartMode start;
        string imagePath, account;
        uint type, errorControl;
        List<string> dependencies = [];
        fixed (byte* b = config)
        {
            type = *(uint*)(b + ConfigServiceType);
            var startType = *(uint*)(b + ConfigStartType);
            errorControl = *(uint*)(b + ConfigErrorControl);
            imagePath = Text(*(char**)(b + ConfigBinaryPath));
            account = Text(*(char**)(b + ConfigStartName));
            for (var p = *(char**)(b + ConfigDependencies); p is not null && *p != '\0';)
            {
                var item = new string(p);
                dependencies.Add(item);
                p += item.Length + 1;
            }

            start = startType switch
            {
                0 => StartMode.Boot,
                1 => StartMode.System,
                2 => StartMode.Automatic,
                4 => StartMode.Disabled,
                _ => StartMode.Manual,
            };
        }

        var description = string.Empty;
        if (Query(service, static (h, b, size, out needed) => QueryServiceConfig2(h, ConfigDescription, b, size, out needed)) is { } text)
        {
            fixed (byte* b = text)
                description = Text(*(char**)b);
        }

        if (start == StartMode.Automatic
            && Query(service, static (h, b, size, out needed) => QueryServiceConfig2(h, ConfigDelayedAutoStart, b, size, out needed)) is { } delayed)
        {
            fixed (byte* b = delayed)
            {
                if (*(int*)b != 0)
                    start = StartMode.AutomaticDelayed;
            }
        }

        // SERVICE_TRIGGER_INFO: cTriggers first. A manual service with triggers starts on its own when they fire.
        var hasTriggers = false;
        if (Query(service, static (h, b, size, out needed) => QueryServiceConfig2(h, ConfigTriggerInfo, b, size, out needed)) is { } triggers)
        {
            fixed (byte* b = triggers)
                hasTriggers = *(uint*)b > 0;
        }

        return new ServiceConfig(type, start, imagePath, account, ExpandResource(description), dependencies, errorControl, hasTriggers,
            ReadRecovery(service));
    }

    /// <summary>
    /// The failure actions as the Services console shows them: first, second and subsequent.
    /// Windows repeats the last action for failures past the list, so a shorter list fills out with it.
    /// </summary>
    private static ServiceRecovery ReadRecovery(nint service)
    {
        var recovery = ServiceRecovery.Nothing;
        if (Query(service, static (h, b, size, out needed) => QueryServiceConfig2(h, ConfigFailureActions, b, size, out needed)) is { } failure)
        {
            fixed (byte* b = failure)
            {
                var info = (FailureActions*)b;
                var count = info->Actions is null ? 0 : (int)info->Count;
                var actions = new RecoveryAction[3];
                uint? restart = null, reboot = null, command = null;
                for (var i = 0; i < count; i++)
                {
                    var action = info->Actions[i];
                    var kind = (RecoveryAction)action.Type;
                    if (i < 3)
                        actions[i] = kind;
                    if (kind == RecoveryAction.Restart)
                        restart ??= action.Delay;
                    else if (kind == RecoveryAction.Reboot)
                        reboot ??= action.Delay;
                    else if (kind == RecoveryAction.RunCommand)
                        command ??= action.Delay;
                }

                for (var i = Math.Max(count, 1); i < 3; i++)
                    actions[i] = actions[i - 1];

                recovery = new ServiceRecovery(actions[0], actions[1], actions[2], info->ResetPeriod,
                    restart ?? ServiceRecovery.DefaultDelay, reboot ?? ServiceRecovery.DefaultDelay, command ?? 0,
                    Text(info->Command), Text(info->RebootMessage), false);
            }
        }

        if (Query(service, static (h, b, size, out needed) => QueryServiceConfig2(h, ConfigFailureActionsFlag, b, size, out needed)) is { } flag)
        {
            fixed (byte* b = flag)
                recovery = recovery with { OnErrorStops = *(int*)b != 0 };
        }

        return recovery;
    }

    /// <summary>The running services that need this one, in the order to stop them (dependents of dependents first).</summary>
    public static List<(string Name, string DisplayName)> ActiveDependents(string name)
    {
        var dependents = new List<(string, string)>();
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
            return dependents;

        try
        {
            var service = OpenService(manager, name, ServiceEnumerateDependents);
            if (service == 0)
                return dependents;

            try
            {
                var buffer = new byte[8 * 1024];
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    fixed (byte* b = buffer)
                    {
                        if (EnumDependentServices(service, StateActive, b, (uint)buffer.Length, out var needed, out var returned))
                        {
                            for (var i = 0; i < returned; i++)
                            {
                                var entry = b + i * DependentSize;
                                dependents.Add((Text(*(char**)entry), Text(*(char**)(entry + 8))));
                            }

                            return dependents;
                        }

                        if (Marshal.GetLastPInvokeError() != ErrorMoreData)
                            return dependents;
                        buffer = new byte[needed + 1024];
                    }
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        return dependents;
    }

    /// <summary>Starts it and waits until it runs. Null when it did; otherwise why not.</summary>
    public static string? Start(string name) => WithService(name, ServiceStart | ServiceQueryStatus, service =>
    {
        if (!StartService(service, 0, null))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceAlreadyRunning)
                return Failure(error);
        }

        return WaitFor(service, ServiceState.Running, ServiceState.StartPending);
    });

    /// <summary>Stops it and waits until it has. Fails with error 1051 while services that need it run.</summary>
    public static string? Stop(string name) => Control(name, ServiceStop, ControlStop, ServiceState.Stopped, ServiceState.StopPending);

    public static string? Pause(string name) => Control(name, ServicePauseContinue, ControlPause, ServiceState.Paused, ServiceState.PausePending);

    public static string? Continue(string name) => Control(name, ServicePauseContinue, ControlContinue, ServiceState.Running, ServiceState.ContinuePending);

    /// <summary>
    /// Applies the edit form. The password goes only with an account, as Windows wants. A person's
    /// account is also given the right to log on as a service, as the Services console does.
    /// </summary>
    public static string? Change(string name, ServiceChange change)
    {
        // A restart action needs the right to start the service; a computer restart, the shutdown privilege.
        var access = ServiceChangeConfig | ServiceQueryConfig;
        if (change.Recovery?.Uses(RecoveryAction.Restart) == true)
            access |= ServiceStart;
        if (change.Recovery?.Uses(RecoveryAction.Reboot) == true && !Privileges.Enable(Privileges.Shutdown))
            return "Restarting the computer after a failure needs administrator rights.";

        return WithService(name, access, service => Change(service, change));
    }

    private static string? Change(nint service, ServiceChange change)
    {
        var startType = change.Start switch
        {
            null => NoChange,
            StartMode.Boot => 0u,
            StartMode.System => 1u,
            StartMode.Automatic or StartMode.AutomaticDelayed => 2u,
            StartMode.Manual => 3u,
            StartMode.Disabled => 4u,
            _ => NoChange,
        };

        if (startType != NoChange || change.ImagePath is not null || change.Account is not null || change.DisplayName is not null
            || change.Type is not null)
        {
            if (!ChangeServiceConfig(service, change.Type ?? NoChange, startType, NoChange, change.ImagePath, null, null, null,
                    change.Account, change.Account is null ? null : change.Password ?? string.Empty, change.DisplayName))
                return Failure(Marshal.GetLastPInvokeError());
        }

        if (change.Description is { } description)
        {
            fixed (char* text = description)
            {
                var info = (nint)text;
                if (!ChangeServiceConfig2(service, ConfigDescription, &info))
                    return Failure(Marshal.GetLastPInvokeError());
            }
        }

        if (change.Start is StartMode.Automatic or StartMode.AutomaticDelayed)
        {
            var delayed = change.Start == StartMode.AutomaticDelayed ? 1 : 0;
            if (!ChangeServiceConfig2(service, ConfigDelayedAutoStart, &delayed))
                return Failure(Marshal.GetLastPInvokeError());
        }

        if (change.Recovery is { } recovery && ChangeRecovery(service, recovery) is { } failure)
            return failure;

        if (change.Account is { } account && ServiceLogonRight.Grant(account) is { } refused)
            return $"Saved, but {account} couldn't be given the right to log on as a service, so it may not start. {refused}";

        return null;
    }

    /// <summary>Writes all three actions, the reset period, the program and the message; empty strings clear the last two.</summary>
    private static string? ChangeRecovery(nint service, ServiceRecovery recovery)
    {
        var actions = stackalloc ScAction[3];
        ReadOnlySpan<RecoveryAction> kinds = [recovery.First, recovery.Second, recovery.Subsequent];
        for (var i = 0; i < 3; i++)
        {
            actions[i] = new ScAction
            {
                Type = (int)kinds[i],
                Delay = kinds[i] switch
                {
                    RecoveryAction.Restart => recovery.RestartDelay,
                    RecoveryAction.Reboot => recovery.RebootDelay,
                    RecoveryAction.RunCommand => recovery.CommandDelay,
                    _ => 0,
                },
            };
        }

        fixed (char* command = recovery.Command)
        fixed (char* message = recovery.RebootMessage)
        {
            var info = new FailureActions
            {
                ResetPeriod = recovery.ResetSeconds,
                RebootMessage = message,
                Command = command,
                Count = 3,
                Actions = actions,
            };
            if (!ChangeServiceConfig2(service, ConfigFailureActions, &info))
                return Failure(Marshal.GetLastPInvokeError());
        }

        var flag = recovery.OnErrorStops ? 1 : 0;
        return ChangeServiceConfig2(service, ConfigFailureActionsFlag, &flag) ? null : Failure(Marshal.GetLastPInvokeError());
    }

    /// <summary>Removes its registration. A running service goes once it stops; its files stay on disk.</summary>
    public static string? Remove(string name) => WithService(name, Delete, service =>
        DeleteService(service) ? null : Failure(Marshal.GetLastPInvokeError()));

    /// <summary>Windows' own words for an error, with its number.</summary>
    public static string Failure(int error) => error switch
    {
        ErrorAccessDenied => "Windows refused: that needs administrator rights.",
        ErrorDependentServicesRunning => "Services that need it are still running.",
        _ => $"{Marshal.GetPInvokeErrorMessage(error).TrimEnd('.', ' ', '\r', '\n')} (error {error.ToString(CultureInfo.InvariantCulture)}).",
    };

    private static string? Control(string name, uint access, uint control, ServiceState target, ServiceState pending) =>
        WithService(name, access | ServiceQueryStatus, service =>
        {
            if (!ControlService(service, control, out _))
            {
                var error = Marshal.GetLastPInvokeError();
                if (!(error == ErrorServiceNotActive && target == ServiceState.Stopped))
                    return Failure(error);
            }

            return WaitFor(service, target, pending);
        });

    /// <summary>Polls the state, as Windows' own tools do: a tenth of the wait hint, between 1/4 s and 2 s, while the checkpoint moves.</summary>
    private static string? WaitFor(nint service, ServiceState target, ServiceState pending)
    {
        var started = Stopwatch.GetTimestamp();
        uint checkpoint = 0;
        var progressed = started;
        while (true)
        {
            StatusProcess status;
            if (!QueryServiceStatusEx(service, 0, (byte*)&status, (uint)sizeof(StatusProcess), out _))
                return Failure(Marshal.GetLastPInvokeError());

            var state = (ServiceState)status.CurrentState;
            if (state == target)
                return null;

            if (state != pending && !(target == ServiceState.Running && state == ServiceState.StartPending))
            {
                return status.Win32ExitCode is 0 or 1077
                    ? $"It went to {Word(state)} instead."
                    : status.Win32ExitCode == 1066
                        ? $"It stopped with its own error {status.ServiceSpecificExitCode.ToString(CultureInfo.InvariantCulture)}."
                        : $"It stopped: {Failure((int)status.Win32ExitCode)}";
            }

            var now = Stopwatch.GetTimestamp();
            if (status.CheckPoint != checkpoint)
            {
                checkpoint = status.CheckPoint;
                progressed = now;
            }

            var hint = TimeSpan.FromMilliseconds(Math.Max(status.WaitHint, 10_000));
            if (Stopwatch.GetElapsedTime(progressed, now) > hint || Stopwatch.GetElapsedTime(started, now) > MaxWait)
                return $"It's still {Word(state)}: it didn't answer in time.";

            Thread.Sleep(Math.Clamp((int)(status.WaitHint / 10), 250, 2000));
        }
    }

    private static string Word(ServiceState state) => state switch
    {
        ServiceState.Stopped => "stopped",
        ServiceState.StartPending => "starting",
        ServiceState.StopPending => "stopping",
        ServiceState.Running => "running",
        ServiceState.ContinuePending => "resuming",
        ServiceState.PausePending => "pausing",
        ServiceState.Paused => "paused",
        _ => "an unknown state",
    };

    private static string? WithService(string name, uint access, Func<nint, string?> action)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == 0)
            return Failure(Marshal.GetLastPInvokeError());

        try
        {
            var service = OpenService(manager, name, access);
            if (service == 0)
                return Failure(Marshal.GetLastPInvokeError());

            try
            {
                return action(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private delegate bool QueryCall(nint service, byte* buffer, uint size, out uint needed);

    /// <summary>
    /// Tries a buffer most answers fit, and asks again with the size Windows wants only when it
    /// doesn't: one call to services.exe instead of two, for each of the settings read per service.
    /// Null when Windows refuses.
    /// </summary>
    private static byte[]? Query(nint service, QueryCall call)
    {
        var buffer = new byte[2048];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint needed;
            fixed (byte* b = buffer)
            {
                if (call(service, b, (uint)buffer.Length, out needed))
                    return buffer;
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || needed <= buffer.Length)
                return null;
            buffer = new byte[needed];
        }

        return null;
    }

    private static string Text(char* p) => p is null ? string.Empty : new string(p);

    /// <summary>Descriptions may be "@%SystemRoot%\system32\foo.dll,-101": a string resource, which SHLoadIndirectString reads.</summary>
    private static string ExpandResource(string text)
    {
        if (!text.StartsWith('@'))
            return text;

        var buffer = stackalloc char[1024];
        return SHLoadIndirectString(text, buffer, 1024, 0) == 0 ? new string(buffer) : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    /// <summary>SERVICE_FAILURE_ACTIONSW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions
    {
        public uint ResetPeriod;
        public char* RebootMessage;
        public char* Command;
        public uint Count;
        public ScAction* Actions;
    }

    /// <summary>SC_ACTION: what, and how many milliseconds after the failure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, char* groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDependentServices(nint service, uint state, byte* services, uint bufferSize, out uint bytesNeeded, out uint returned);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(nint service, byte* config, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2(nint service, int infoLevel, byte* buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint service, int infoLevel, byte* buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(nint service, uint argumentCount, char** arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, out Status status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, uint* tagId, string? dependencies, string? startName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2(nint service, int infoLevel, void* info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(nint service);

    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, char* output, uint outputSize, nint reserved);
}
