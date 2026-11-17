using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Permanent WMI event subscriptions in root\subscription: a filter (a WMI query) bound to a
/// consumer that runs a command or script whenever the query matches, with no process of its own
/// until then. Read through WMI's COM interfaces by vtable, since System.Management isn't
/// compatible with Native AOT. Only administrators can read them.
/// </summary>
internal static unsafe class WmiSubscriptions
{
    private const ushort VtBstr = 8;
    private const int FlagForwardOnly = 0x20;
    private const int FlagReturnImmediately = 0x10;
    private const int Infinite = -1;

    private const uint AuthnWinNt = 10;
    private const uint AuthzNone = 0;
    private const uint AuthnLevelCall = 3;
    private const uint ImpersonateLevel = 3;

    private static readonly Guid ClsidLocator = new("4590f811-1d3a-11d0-891f-00aa004b2e24");
    private static readonly Guid IidLocator = new("dc12a687-737f-11cf-884d-00aa004b2e24");

    private const string About = "A permanent WMI event subscription: whenever its query matches (at startup, on a timer, when a program starts…), WMI runs the consumer, as Local System, with no program of its own running until then.";

    /// <summary>Known Windows subscriptions with nothing to run of their own.</summary>
    private static readonly HashSet<string> WindowsConsumers = new(StringComparer.OrdinalIgnoreCase) { "SCM Event Log Consumer" };

    public static IEnumerable<AutostartItem> Scan()
    {
        List<AutostartItem> items;
        try
        {
            items = Com.OnThread(Read);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        return items;
    }

    /// <summary>Removes the binding, then the consumer and filter if nothing else uses them.</summary>
    public static string? Delete(WmiTarget target) => Com.OnThread(() =>
    {
        var services = Connect(out var hr);
        if (services == 0)
            return Com.Describe(hr);

        try
        {
            hr = DeleteInstance(services, target.Binding);
            if (hr < 0)
                return Com.Describe(hr);

            var remaining = Query(services, "SELECT * FROM __FilterToConsumerBinding", ["Consumer", "Filter"]);
            if (!remaining.Any(b => Same(b["Consumer"], target.Consumer)))
                DeleteInstance(services, target.Consumer);
            if (!remaining.Any(b => Same(b["Filter"], target.Filter)))
                DeleteInstance(services, target.Filter);
            return null;
        }
        finally
        {
            Com.Release(services);
        }
    });

    private static List<AutostartItem> Read()
    {
        var items = new List<AutostartItem>();
        var services = Connect(out _);
        if (services == 0)
            return items;

        try
        {
            var bindings = Query(services, "SELECT * FROM __FilterToConsumerBinding", ["__PATH", "Consumer", "Filter"]);
            if (bindings.Count == 0)
                return items;

            var consumers = Query(services, "SELECT * FROM __EventConsumer",
                ["__PATH", "__RELPATH", "__CLASS", "Name", "CommandLineTemplate", "ExecutablePath", "ScriptFileName", "ScriptText", "ScriptingEngine"]);
            var filters = Query(services, "SELECT * FROM __EventFilter", ["__PATH", "__RELPATH", "Name", "Query"]);

            foreach (var binding in bindings)
            {
                var consumer = consumers.FirstOrDefault(c => Same(c["__RELPATH"], binding["Consumer"]));
                var filter = filters.FirstOrDefault(f => Same(f["__RELPATH"], binding["Filter"]));
                var name = consumer?["Name"] is { Length: > 0 } consumerName ? consumerName : Relative(binding["Consumer"]);

                string command;
                string? image = null;
                if (consumer?["CommandLineTemplate"] is { Length: > 0 } template)
                {
                    command = template;
                    image = SignInSources.Image(template);
                }
                else if (consumer?["ExecutablePath"] is { Length: > 0 } executable)
                {
                    command = executable;
                    image = SignInSources.Image(executable);
                }
                else if (consumer?["ScriptFileName"] is { Length: > 0 } script)
                {
                    command = script;
                    image = script;
                }
                else if (consumer?["ScriptText"] is { Length: > 0 } text)
                {
                    command = $"{consumer["ScriptingEngine"]} script: {(text.Length > 400 ? text[..400] + "…" : text)}";
                }
                else
                {
                    command = string.Empty;
                }

                var facts = new List<AutostartFact>
                {
                    new("Consumer", consumer?["__CLASS"] is { Length: > 0 } kind ? $"{name} ({kind})" : name),
                    new("Filter", filter?["Name"] is { Length: > 0 } filterName ? filterName : Relative(binding["Filter"])),
                };
                if (filter?["Query"] is { Length: > 0 } query)
                    facts.Add(new AutostartFact("Query", query));

                items.Add(new AutostartItem
                {
                    Id = $"wmi|{binding["__PATH"]}",
                    Category = AutostartCategory.Wmi,
                    Name = name,
                    Location = @"WMI root\subscription",
                    Command = command,
                    Image = image,
                    About = About,
                    Facts = facts,
                    Removal = new WmiTarget(binding["__PATH"], consumer?["__PATH"] ?? string.Empty, filter?["__PATH"] ?? string.Empty) { NeedsAdmin = true },
                    IsKnownWindows = WindowsConsumers.Contains(name) && command.Length == 0,
                });
            }
        }
        finally
        {
            Com.Release(services);
        }

        return items;
    }

    private static nint Connect(out int hr)
    {
        var clsid = ClsidLocator;
        var iid = IidLocator;
        nint locator;
        hr = Com.CoCreateInstance(&clsid, 0, Com.ClsctxInprocServer, &iid, &locator);
        if (hr < 0)
            return 0;

        try
        {
            // IWbemLocator: ConnectServer = 3.
            var connect = (delegate* unmanaged<nint, nint, nint, nint, nint, int, nint, nint, nint*, int>)(*(nint**)locator)[3];
            var path = Com.Bstr(@"ROOT\subscription");
            nint services;
            try
            {
                hr = connect(locator, path, 0, 0, 0, 0, 0, 0, &services);
            }
            finally
            {
                Com.SysFreeString(path);
            }

            if (hr < 0)
                return 0;

            Com.CoSetProxyBlanket(services, AuthnWinNt, AuthzNone, 0, AuthnLevelCall, ImpersonateLevel, 0, 0);
            return services;
        }
        finally
        {
            Com.Release(locator);
        }
    }

    /// <summary>Each instance's named properties, as text (missing or non-text ones empty).</summary>
    private static List<Dictionary<string, string>> Query(nint services, string wql, string[] properties)
    {
        var rows = new List<Dictionary<string, string>>();

        // IWbemServices: ExecQuery = 20.
        var execQuery = (delegate* unmanaged<nint, nint, nint, int, nint, nint*, int>)(*(nint**)services)[20];
        var language = Com.Bstr("WQL");
        var text = Com.Bstr(wql);
        nint enumerator;
        int hr;
        try
        {
            hr = execQuery(services, language, text, FlagForwardOnly | FlagReturnImmediately, 0, &enumerator);
        }
        finally
        {
            Com.SysFreeString(language);
            Com.SysFreeString(text);
        }

        if (hr < 0)
            return rows;

        try
        {
            Com.CoSetProxyBlanket(enumerator, AuthnWinNt, AuthzNone, 0, AuthnLevelCall, ImpersonateLevel, 0, 0);

            // IEnumWbemClassObject: Next = 4.
            var next = (delegate* unmanaged<nint, int, uint, nint*, uint*, int>)(*(nint**)enumerator)[4];
            while (true)
            {
                nint instance;
                uint returned;
                if (next(enumerator, Infinite, 1, &instance, &returned) < 0 || returned == 0)
                    break;

                try
                {
                    // IWbemClassObject: Get = 4.
                    var get = (delegate* unmanaged<nint, char*, int, Variant*, int*, int*, int>)(*(nint**)instance)[4];
                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var property in properties)
                    {
                        Variant value;
                        fixed (char* name = property)
                        {
                            if (get(instance, name, 0, &value, null, null) < 0)
                            {
                                row[property] = string.Empty;
                                continue;
                            }
                        }

                        row[property] = value.Type == VtBstr && value.Value != 0 ? new string((char*)value.Value) : string.Empty;
                        Com.VariantClear(&value);
                    }

                    rows.Add(row);
                }
                finally
                {
                    Com.Release(instance);
                }
            }
        }
        finally
        {
            Com.Release(enumerator);
        }

        return rows;
    }

    private static int DeleteInstance(nint services, string path)
    {
        if (path.Length == 0)
            return 0;

        // IWbemServices: DeleteInstance = 16.
        var delete = (delegate* unmanaged<nint, nint, int, nint, nint, int>)(*(nint**)services)[16];
        var bstr = Com.Bstr(path);
        try
        {
            return delete(services, bstr, 0, 0, 0);
        }
        finally
        {
            Com.SysFreeString(bstr);
        }
    }

    /// <summary>References may carry the server and namespace (\\HOST\ROOT\subscription:Class.Name="x"); compare what follows.</summary>
    private static string Relative(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return path;

        var colon = path.IndexOf(':');
        return colon >= 0 ? path[(colon + 1)..] : path;
    }

    private static bool Same(string a, string b) => a.Length > 0 && string.Equals(Relative(a), Relative(b), StringComparison.OrdinalIgnoreCase);
}
