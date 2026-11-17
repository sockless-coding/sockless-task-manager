using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>An event as read from a log: who wrote it, when, and its data values.</summary>
/// <param name="Values">EventData's values in order, with their names when the event has them ("DeviceInstanceId").</param>
internal sealed record LogEvent(string Provider, int Id, DateTime Time, IReadOnlyList<(string? Name, string Value)> Values)
{
    public string? this[string name] => Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    public string? At(int index) => index < Values.Count ? Values[index].Value : null;
}

/// <summary>
/// Reads Windows event logs through wevtapi: a channel and an XPath filter in, newest events first.
/// Each event is rendered as XML and the few parts needed are read by hand, which keeps System.Xml
/// (large under Native AOT) out of the app. Reads only when asked; nothing is followed live.
/// </summary>
internal static partial class EventLog
{
    private const uint QueryChannelPath = 0x1;
    private const uint QueryReverseDirection = 0x200;
    private const uint QueryTolerateErrors = 0x1000;
    private const uint RenderEventXml = 1;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>Up to <paramref name="max"/> events matching <paramref name="query"/>, newest first. Empty when the log can't be read.</summary>
    public static List<LogEvent> Query(string channel, string query, int max)
    {
        var events = new List<LogEvent>();
        var results = EvtQuery(0, channel, query, QueryChannelPath | QueryReverseDirection | QueryTolerateErrors);
        if (results == 0)
            return events;

        try
        {
            var handles = new nint[64];
            var buffer = new char[4096];
            while (events.Count < max)
            {
                if (!EvtNext(results, (uint)handles.Length, handles, 2000, 0, out var returned) || returned == 0)
                    break;

                for (var i = 0; i < returned; i++)
                {
                    try
                    {
                        if (events.Count < max && Render(handles[i], ref buffer) is { } xml && Parse(xml) is { } parsed)
                            events.Add(parsed);
                    }
                    finally
                    {
                        EvtClose(handles[i]);
                    }
                }
            }
        }
        finally
        {
            EvtClose(results);
        }

        return events;
    }

    /// <summary>"*[System[TimeCreated[timediff(@SystemTime) &lt;= …]]]": events no older than <paramref name="age"/>.</summary>
    public static string Within(TimeSpan age) => $"timediff(@SystemTime) <= {(long)age.TotalMilliseconds}";

    private static unsafe string? Render(nint handle, ref char[] buffer)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint used, count;
            bool ok;
            fixed (char* p = buffer)
                ok = EvtRender(0, handle, RenderEventXml, (uint)(buffer.Length * 2), p, &used, &count);

            if (ok)
                return new string(buffer, 0, Math.Max(0, (int)(used / 2) - 1));
            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                return null;

            buffer = new char[used / 2 + 64];
        }

        return null;
    }

    private static LogEvent? Parse(string xml)
    {
        var provider = Attribute(xml, "<Provider ", "Name");
        var id = Element(xml, "EventID");
        var time = Attribute(xml, "<TimeCreated ", "SystemTime");
        if (provider is null || id is null || time is null
            || !int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId)
            || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
            return null;

        var values = new List<(string?, string)>();
        var data = xml.IndexOf("<EventData", StringComparison.Ordinal);
        if (data < 0)
            data = xml.IndexOf("<UserData", StringComparison.Ordinal);

        for (var at = data; at >= 0;)
        {
            at = xml.IndexOf("<Data", at, StringComparison.Ordinal);
            if (at < 0)
                break;

            var close = xml.IndexOf('>', at);
            if (close < 0)
                break;

            var tag = xml[at..close];
            var name = Attribute(tag, "<Data", "Name");
            if (xml[close - 1] == '/')
            {
                values.Add((name, string.Empty));
                at = close;
                continue;
            }

            var end = xml.IndexOf("</Data>", close, StringComparison.Ordinal);
            if (end < 0)
                break;

            values.Add((name, WebUtility.HtmlDecode(xml[(close + 1)..end])));
            at = end;
        }

        return new LogEvent(provider, eventId, when, values);
    }

    /// <summary>The value of <paramref name="attribute"/> in the first element starting with <paramref name="tag"/>.</summary>
    private static string? Attribute(string xml, string tag, string attribute)
    {
        var at = xml.IndexOf(tag, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var end = xml.IndexOf('>', at);
        var key = xml.IndexOf(attribute + "=", at, StringComparison.Ordinal);
        if (key < 0 || (end >= 0 && key > end))
            return null;

        var quote = xml[key + attribute.Length + 1];
        var start = key + attribute.Length + 2;
        var stop = xml.IndexOf(quote, start);
        return stop < 0 ? null : WebUtility.HtmlDecode(xml[start..stop]);
    }

    private static string? Element(string xml, string name)
    {
        var at = xml.IndexOf("<" + name, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var close = xml.IndexOf('>', at);
        var end = xml.IndexOf("</" + name + ">", StringComparison.Ordinal);
        return close < 0 || end < close ? null : xml[(close + 1)..end];
    }

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint EvtQuery(nint session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(nint results, uint size, [Out] nint[] events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EvtRender(nint context, nint fragment, uint flags, uint bufferSize, char* buffer, uint* used, uint* propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(nint handle);
}
