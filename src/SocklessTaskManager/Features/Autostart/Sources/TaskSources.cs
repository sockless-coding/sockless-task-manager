using System.Net;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Scheduled tasks: one entry per action (a program, or a COM handler's DLL), with the triggers
/// that start it. The definitions are small and regular, so a few string searches read them
/// rather than an XML library the app otherwise wouldn't carry.
/// </summary>
internal static class TaskSources
{
    private const string About = "A scheduled task: Task Scheduler starts it on the triggers listed, signed in or not, as the account it names.";

    public static IEnumerable<AutostartItem> Scan()
    {
        List<ScheduledTask> tasks;
        try
        {
            tasks = TaskScheduler.List();
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        foreach (var task in tasks)
        {
            var split = task.Path.LastIndexOf('\\');
            var folder = split <= 0 ? @"\" : task.Path[..split];
            var name = task.Path[(split + 1)..];

            var triggers = Triggers(task.Xml);
            var (account, isMine) = Account(task.Xml);
            var facts = new List<AutostartFact>
            {
                new("Triggers", triggers.Count > 0 ? string.Join("\n", triggers) : "None: it runs only when started by hand or by a program"),
                new("Runs as", account),
            };
            if (Text(task.Xml, "Author") is { Length: > 0 } author)
                facts.Add(new AutostartFact("Author", AutostartRegistry.Indirect(author)));
            if (Text(task.Xml, "Description") is { Length: > 0 } description)
                facts.Add(new AutostartFact("Description", AutostartRegistry.Indirect(description)));
            if (Text(task.Xml, "Hidden") == "true")
                facts.Add(new AutostartFact("Hidden", "Yes: Task Scheduler doesn't list it unless asked to"));

            var actions = Actions(task.Xml);
            if (actions.Count == 0)
                actions.Add((string.Empty, null, "No program to run"));

            var admin = !isMine;
            for (var i = 0; i < actions.Count; i++)
            {
                var (command, image, kind) = actions[i];
                yield return new AutostartItem
                {
                    Id = $"task|{task.Path}|{i}",
                    Category = AutostartCategory.ScheduledTasks,
                    Name = actions.Count > 1 ? $"{name} ({i + 1} of {actions.Count})" : name,
                    Location = folder,
                    Command = command,
                    Image = image,
                    IsEnabled = task.Enabled,
                    About = About,
                    Facts = [new AutostartFact("Action", kind), .. facts],
                    Toggle = new TaskTarget(task.Path, task.Xml) { NeedsAdmin = admin },
                    Removal = new TaskTarget(task.Path, task.Xml) { NeedsAdmin = admin },
                };
            }
        }
    }

    /// <summary>Each Exec's command line and program, and each COM handler's class and DLL.</summary>
    private static List<(string Command, string? Image, string Kind)> Actions(string xml)
    {
        var actions = new List<(string, string?, string)>();
        foreach (var exec in Elements(xml, "Exec"))
        {
            var program = Text(exec, "Command") ?? string.Empty;
            var arguments = Text(exec, "Arguments") ?? string.Empty;
            var quoted = program.Contains(' ') && !program.StartsWith('"') ? $"\"{program}\"" : program;
            var command = arguments.Length > 0 ? $"{quoted} {arguments}" : quoted;
            actions.Add((command, SignInSources.Image(command), "Start a program"));
        }

        foreach (var handler in Elements(xml, "ComHandler"))
        {
            var clsid = Text(handler, "ClassId") ?? string.Empty;
            var server = clsid.Length > 0 ? AutostartRegistry.ClassServer(clsid) : null;
            var image = server is null ? null : SignInSources.Image(server);
            var name = clsid.Length > 0 ? AutostartRegistry.ClassName(clsid) : null;
            actions.Add((server ?? clsid, image, name is null ? $"COM handler {clsid}" : $"COM handler {name} {clsid}"));
        }

        return actions;
    }

    private static List<string> Triggers(string xml)
    {
        var list = new List<string>();
        if (Elements(xml, "Triggers").FirstOrDefault() is not { } triggers)
            return list;

        var at = 0;
        while ((at = triggers.IndexOf('<', at)) >= 0)
        {
            var end = triggers.IndexOfAny([' ', '>', '/'], at + 1);
            if (end < 0)
                break;

            var tag = triggers[(at + 1)..end];
            at = end;
            if (!tag.EndsWith("Trigger", StringComparison.Ordinal))
                continue;

            var body = Elements(triggers[(at - tag.Length - 1)..], tag).FirstOrDefault() ?? string.Empty;
            var text = tag switch
            {
                "LogonTrigger" => Text(body, "UserId") is { Length: > 0 } user ? $"At sign-in of {Name(user)}" : "At sign-in of any user",
                "BootTrigger" => "At startup",
                "IdleTrigger" => "When the computer is idle",
                "RegistrationTrigger" => "When the task is created or changed",
                "EventTrigger" => "On an event log entry",
                "TimeTrigger" => Text(body, "StartBoundary") is { Length: > 0 } start ? $"Once, at {Time(start)}" : "Once",
                "CalendarTrigger" => Schedule(body),
                "SessionStateChangeTrigger" => Text(body, "StateChange") switch
                {
                    "ConsoleConnect" => "On connecting to the computer",
                    "RemoteConnect" => "On connecting by Remote Desktop",
                    "SessionLock" => "When the computer is locked",
                    "SessionUnlock" => "When the computer is unlocked",
                    _ => "On a session change",
                },
                "WnfStateChangeTrigger" => "On a Windows notification",
                _ => tag[..^7],
            };

            if (Text(body, "Repetition") is not null && Text(body, "Interval") is { Length: > 0 } interval)
                text += $", repeating every {Duration(interval)}";
            if (Text(body, "Enabled") == "false")
                text += " (off)";
            list.Add(text);
        }

        return list;
    }

    private static string Schedule(string body)
    {
        var start = Text(body, "StartBoundary") is { Length: > 0 } boundary ? $" from {Time(boundary)}" : string.Empty;
        if (body.Contains("<ScheduleByDay>", StringComparison.Ordinal))
            return Text(body, "DaysInterval") is { } days and not "1" ? $"Every {days} days{start}" : $"Daily{start}";
        if (body.Contains("<ScheduleByWeek>", StringComparison.Ordinal))
            return $"Weekly{start}";
        if (body.Contains("<ScheduleByMonth", StringComparison.Ordinal))
            return $"Monthly{start}";
        return $"On a schedule{start}";
    }

    private static string Time(string boundary) =>
        DateTime.TryParse(boundary, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
            ? time.ToString("g", System.Globalization.CultureInfo.CurrentCulture)
            : boundary;

    /// <summary>"PT1H" → "1 hour", "P1D" → "1 day".</summary>
    private static string Duration(string iso)
    {
        var seconds = 0L;
        var number = 0L;
        var inTime = false;
        foreach (var c in iso)
        {
            if (char.IsAsciiDigit(c))
            {
                number = number * 10 + (c - '0');
                continue;
            }

            seconds += c switch
            {
                'D' => number * 86400,
                'W' => number * 604800,
                'H' => number * 3600,
                'M' when inTime => number * 60,
                'M' => number * 2592000,
                'S' => number,
                _ => 0,
            };
            if (c == 'T')
                inTime = true;
            number = 0;
        }

        return seconds <= 0 ? iso
            : seconds % 86400 == 0 ? Plural((int)(seconds / 86400), "day")
            : seconds % 3600 == 0 ? Plural((int)(seconds / 3600), "hour")
            : seconds % 60 == 0 ? Plural((int)(seconds / 60), "minute")
            : Plural((int)seconds, "second");
    }

    private static string Plural(int count, string noun) => $"{Format.Number(count)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static readonly string? CurrentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;

    /// <summary>Who it runs as, and whether that's the user running this app (who can change it without elevation).</summary>
    private static (string Name, bool IsMine) Account(string xml)
    {
        var principal = Elements(xml, "Principal").FirstOrDefault() ?? string.Empty;
        var who = Text(principal, "UserId") ?? Text(principal, "GroupId");
        var name = who is { Length: > 0 } ? Name(who) : "The user who created it";
        var isMine = who is null
            || who.Equals(CurrentSid, StringComparison.OrdinalIgnoreCase)
            || who.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase)
            || who.EndsWith('\\' + Environment.UserName, StringComparison.OrdinalIgnoreCase);
        return (Text(principal, "RunLevel") == "HighestAvailable" ? $"{name}, with highest privileges" : name, isMine);
    }

    private static string Name(string user) => user switch
    {
        "S-1-5-18" or "SYSTEM" => "Local System",
        "S-1-5-19" => "Local Service",
        "S-1-5-20" => "Network Service",
        "S-1-5-32-545" => "Users",
        "S-1-5-32-544" => "Administrators",
        "S-1-5-4" => "Interactive users",
        "S-1-5-11" => "Authenticated users",
        _ when Accounts.FromSid(user) is { } account => account.FullName,
        _ => user,
    };

    /// <summary>The inner text of each &lt;name&gt; element, in order. Self-closing ones are empty.</summary>
    internal static List<string> Elements(string xml, string name)
    {
        var found = new List<string>();
        var open = "<" + name;
        var close = "</" + name + ">";
        var at = 0;
        while ((at = xml.IndexOf(open, at, StringComparison.Ordinal)) >= 0)
        {
            var after = at + open.Length;
            if (after >= xml.Length || xml[after] is not ('>' or ' ' or '/' or '\r' or '\n' or '\t'))
            {
                at = after;
                continue;
            }

            var tagEnd = xml.IndexOf('>', after);
            if (tagEnd < 0)
                break;

            if (xml[tagEnd - 1] == '/')
            {
                found.Add(string.Empty);
                at = tagEnd + 1;
                continue;
            }

            var end = xml.IndexOf(close, tagEnd, StringComparison.Ordinal);
            if (end < 0)
                break;

            found.Add(xml[(tagEnd + 1)..end]);
            at = end + close.Length;
        }

        return found;
    }

    /// <summary>The first &lt;name&gt; element's text, entities decoded; null when there's none.</summary>
    internal static string? Text(string xml, string name) =>
        Elements(xml, name) is [var first, ..] ? WebUtility.HtmlDecode(first).Trim() : null;
}
