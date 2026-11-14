using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Users;

internal enum SessionState
{
    Active,
    Locked,
    Disconnected,
    Other,
}

/// <summary>A Windows session someone is signed in to.</summary>
/// <param name="Station">The window station: "Console", "RDP-Tcp#3".</param>
/// <param name="Client">The remote computer's name, for a remote desktop session.</param>
/// <param name="SignedIn">Local time.</param>
/// <param name="Disconnected">Local time, while the session is disconnected.</param>
/// <param name="Idle">Since the last keyboard or mouse input, where Windows says.</param>
internal sealed record Session(
    int Id,
    string UserName,
    string Domain,
    string Station,
    string? Client,
    bool IsRemote,
    SessionState State,
    DateTime? SignedIn,
    DateTime? Disconnected,
    TimeSpan? Idle)
{
    public string FullName => Domain.Length > 0 ? $"{Domain}\\{UserName}" : UserName;
}

/// <summary>
/// The sessions on this computer from the Remote Desktop Services API (WTS), which also covers
/// the console and fast user switching. Listing them works without elevation; disconnecting or
/// signing out another user's session needs it. Buffers are parsed by offset (64-bit layouts).
/// </summary>
internal static unsafe partial class Sessions
{
    private const nint CurrentServer = 0;

    // WTS_INFO_CLASS
    private const int ClientName = 10;
    private const int ClientProtocolType = 16;
    private const int SessionInfoEx = 25;

    // WTS_CONNECTSTATE_CLASS
    private const int StateActive = 0;
    private const int StateConnected = 1;
    private const int StateDisconnected = 4;

    // WTS_SESSION_INFOW (x64): SessionId, then a pointer, then State.
    private const int SessionInfoSize = 24;
    private const int SessionInfoState = 16;

    // WTSINFOEXW (x64): a DWORD level, then WTSINFOEX_LEVEL1_W at 8.
    private const int ExBase = 8;
    private const int ExSessionFlags = ExBase + 8;
    private const int ExWinStationName = ExBase + 12;
    private const int ExWinStationNameChars = 33;
    private const int ExUserName = ExBase + 78;
    private const int ExUserNameChars = 21;
    private const int ExDomainName = ExBase + 120;
    private const int ExDomainNameChars = 18;
    private const int ExLogonTime = ExBase + 160;
    private const int ExDisconnectTime = ExBase + 176;
    private const int ExLastInputTime = ExBase + 184;
    private const int ExCurrentTime = ExBase + 192;
    private const int ExSize = ExBase + 224;

    private const int SessionLocked = 0;
    private const ushort ProtocolRdp = 2;

    /// <summary>Every session with someone signed in to it. Session 0 (services) and idle listeners are left out.</summary>
    public static List<Session> Read()
    {
        var sessions = new List<Session>();
        if (!WTSEnumerateSessions(CurrentServer, 0, 1, out var list, out var count))
            return sessions;

        try
        {
            var all = new ReadOnlySpan<byte>((void*)list, (int)count * SessionInfoSize);
            for (var i = 0; i < count; i++)
            {
                var entry = all[(i * SessionInfoSize)..];
                var id = BinaryPrimitives.ReadInt32LittleEndian(entry);
                var state = BinaryPrimitives.ReadInt32LittleEndian(entry[SessionInfoState..]);
                if (state is StateActive or StateConnected or StateDisconnected && Describe(id) is { } session)
                    sessions.Add(session);
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }

        return sessions;
    }

    private static Session? Describe(int id)
    {
        if (!WTSQuerySessionInformation(CurrentServer, (uint)id, SessionInfoEx, out var buffer, out var bytes))
            return null;

        try
        {
            if (bytes < ExSize)
                return null;

            var info = new ReadOnlySpan<byte>((void*)buffer, (int)bytes);
            var user = Text(info, ExUserName, ExUserNameChars);
            if (user.Length == 0)
                return null;

            var connectState = BinaryPrimitives.ReadInt32LittleEndian(info[(ExBase + 4)..]);
            var flags = BinaryPrimitives.ReadInt32LittleEndian(info[ExSessionFlags..]);
            var state = connectState switch
            {
                StateDisconnected => SessionState.Disconnected,
                StateActive when flags == SessionLocked => SessionState.Locked,
                StateActive => SessionState.Active,
                _ => SessionState.Other,
            };

            var lastInput = BinaryPrimitives.ReadInt64LittleEndian(info[ExLastInputTime..]);
            var now = BinaryPrimitives.ReadInt64LittleEndian(info[ExCurrentTime..]);
            TimeSpan? idle = lastInput > 0 && now > lastInput ? TimeSpan.FromTicks(now - lastInput) : null;

            var station = Text(info, ExWinStationName, ExWinStationNameChars);
            var remote = Protocol(id) == ProtocolRdp;
            return new Session(
                id,
                user,
                Text(info, ExDomainName, ExDomainNameChars),
                station,
                remote ? QueryText(id, ClientName) : null,
                remote,
                state,
                LocalTime(BinaryPrimitives.ReadInt64LittleEndian(info[ExLogonTime..])),
                state == SessionState.Disconnected ? LocalTime(BinaryPrimitives.ReadInt64LittleEndian(info[ExDisconnectTime..])) : null,
                idle);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// Since the last input in this app's own session. WTS often reports no input time for the
    /// console, so the caller prefers this for its own session.
    /// </summary>
    public static TimeSpan? OwnIdle()
    {
        var info = new LastInputInfo { Size = (uint)sizeof(LastInputInfo) };
        if (!GetLastInputInfo(ref info))
            return null;

        return TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time);
    }

    /// <summary>Ends the connection; the session and its apps keep running until someone signs in to it again.</summary>
    public static bool Disconnect(int id, out int error) => Done(WTSDisconnectSession(CurrentServer, (uint)id, false), out error);

    /// <summary>Signs the session out, closing everything it runs.</summary>
    public static bool SignOut(int id, out int error) => Done(WTSLogoffSession(CurrentServer, (uint)id, false), out error);

    private static bool Done(bool ok, out int error)
    {
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    private static ushort Protocol(int id)
    {
        if (!WTSQuerySessionInformation(CurrentServer, (uint)id, ClientProtocolType, out var buffer, out var bytes))
            return 0;

        try
        {
            return bytes >= 2 ? *(ushort*)buffer : (ushort)0;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? QueryText(int id, int infoClass)
    {
        if (!WTSQuerySessionInformation(CurrentServer, (uint)id, infoClass, out var buffer, out var bytes))
            return null;

        try
        {
            var text = new string((char*)buffer, 0, (int)(bytes / 2)).TrimEnd('\0');
            return text.Length > 0 ? text : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>A fixed-length, null-terminated WCHAR array.</summary>
    private static string Text(ReadOnlySpan<byte> info, int offset, int chars)
    {
        var text = MemoryMarshal.Cast<byte, char>(info.Slice(offset, chars * 2));
        var end = text.IndexOf('\0');
        return new string(end < 0 ? text : text[..end]);
    }

    private static DateTime? LocalTime(long fileTime) =>
        fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime() : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessions(nint server, uint reserved, uint version, out nint sessions, out uint count);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSDisconnectSession(nint server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSLogoffSession(nint server, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
