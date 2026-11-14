using System.Globalization;
using System.Text;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// One on/off filter in the chip row. Chips in a group widen each other (TCP or UDP); groups narrow
/// each other (TCP and incoming). A group with nothing on lets everything through.
/// </summary>
public sealed class FilterChip : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    internal FilterChip(string key, string label, string tip, Action changed)
    {
        Key = key;
        Label = label;
        Tip = tip;
        _changed = changed;
    }

    /// <summary>Stable name for the settings file.</summary>
    internal string Key { get; }

    public string Label { get; }

    public string Tip { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                _changed();
        }
    }

    /// <summary>Restores a saved state without re-rendering.</summary>
    internal void Restore(bool on)
    {
        if (_isChecked == on)
            return;
        _isChecked = on;
        OnPropertyChanged(nameof(IsChecked));
    }

    internal Func<ConnectionInfo, bool>? Connection { get; init; }

    internal Func<ListenerInfo, bool>? Listener { get; init; }

    internal Func<ProcessFilterFacts, bool>? Process { get; init; }
}

/// <summary>A titled set of chips: Protocol, Direction, Remote end…</summary>
public sealed class FilterGroup(string title, IReadOnlyList<FilterChip> chips)
{
    public string Title { get; } = title;

    public IReadOnlyList<FilterChip> Chips { get; } = chips;

    public bool IsActive => Chips.Any(c => c.IsChecked);

    /// <summary>Nothing on, or one of the chips that are on says yes.</summary>
    internal bool Passes(Func<FilterChip, bool> test)
    {
        var any = false;
        foreach (var chip in Chips)
        {
            if (!chip.IsChecked)
                continue;
            any = true;
            if (test(chip))
                return true;
        }

        return !any;
    }
}

/// <summary>What the process filters look at.</summary>
internal readonly record struct ProcessFilterFacts(bool Running, bool HasTraffic, int Listening, int Open);

internal enum SearchField
{
    Any,
    Pid,
    Process,
    Port,
    Ip,
    Host,
    Country,
    Org,
    Protocol,
    State,
    Direction,
}

/// <param name="Low">For pid and port: the number, or the start of a range.</param>
/// <param name="High">For a port range, its end; otherwise the same as <paramref name="Low"/>.</param>
internal readonly record struct SearchTerm(SearchField Field, string Value, bool Negated, long? Low, long? High);

/// <summary>
/// The search box, read as terms that must all hold: free words, or <c>field:value</c> filters,
/// each excluded with a leading <c>-</c>. Quotes keep spaces in a value.
/// </summary>
internal sealed class NetworkSearch
{
    public const string Help =
        "Words must all match. Narrow with pid:1234, process:chrome, port:443 or port:1-1024, ip:10.0., host:google, "
        + "country:SE, org:Amazon or org:AS16509, proto:udp, state:connected, dir:in. Put - in front to exclude: -svchost.";

    public static readonly NetworkSearch Empty = new([]);

    private NetworkSearch(IReadOnlyList<SearchTerm> terms) => Terms = terms;

    public IReadOnlyList<SearchTerm> Terms { get; }

    public bool IsEmpty => Terms.Count == 0;

    public static NetworkSearch Parse(string text)
    {
        var terms = new List<SearchTerm>();
        foreach (var token in Tokens(text))
        {
            var negated = token.Length > 1 && token[0] == '-';
            var body = negated ? token[1..] : token;
            var (field, value) = Split(body);
            if (value.Length == 0)
                continue;

            long? low = null, high = null;
            if (field is SearchField.Pid or SearchField.Port)
            {
                var dash = value.IndexOf('-', 1);
                if (dash > 0 && long.TryParse(value.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                    && long.TryParse(value.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var to))
                    (low, high) = (Math.Min(from, to), Math.Max(from, to));
                else if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    (low, high) = (number, number);
                else
                    (field, value) = (SearchField.Any, body);
            }

            terms.Add(new SearchTerm(field, value, negated, low, high));
        }

        return terms.Count == 0 ? Empty : new NetworkSearch(terms);
    }

    /// <summary>A known <c>field:</c> prefix, or the whole token as free text (an IPv6 address has colons too).</summary>
    private static (SearchField, string) Split(string token)
    {
        var colon = token.IndexOf(':');
        if (colon <= 0)
            return (SearchField.Any, token);

        SearchField? field = token[..colon].ToLowerInvariant() switch
        {
            "pid" => SearchField.Pid,
            "process" or "name" or "app" => SearchField.Process,
            "port" => SearchField.Port,
            "ip" or "addr" or "address" => SearchField.Ip,
            "host" => SearchField.Host,
            "country" or "cc" => SearchField.Country,
            "org" or "asn" or "owner" => SearchField.Org,
            "proto" or "protocol" => SearchField.Protocol,
            "state" => SearchField.State,
            "dir" or "direction" => SearchField.Direction,
            _ => null,
        };
        return field is { } f ? (f, token[(colon + 1)..]) : (SearchField.Any, token);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
