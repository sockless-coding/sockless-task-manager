using System.Collections.ObjectModel;
using System.Globalization;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>One kind of exception: a type and message, how often it was thrown, and when last.</summary>
public sealed class ThrownException(string type, string message, long count, DateTime lastThrown)
{
    /// <summary>"InvalidOperationException".</summary>
    public string Name { get; } = TypeName.Short(type);

    public string FullName { get; } = TypeName.Full(type);

    /// <summary>On one line; the whole message is the tooltip.</summary>
    public string Message { get; } = message.ReplaceLineEndings(" ");

    public string FullMessage { get; } = message;

    public string CountText { get; } = Format.Number(count);

    public string LastThrownText { get; } = lastThrown.ToString("T", CultureInfo.CurrentCulture);

    internal string Type => type;

    internal long Count => count;

    internal DateTime LastThrown => lastThrown;
}

/// <summary>
/// Exceptions as the process throws them, caught or not (first chance), from the runtime's own
/// ExceptionThrown event, grouped by type and message. Each throw costs the process an event and
/// a stack walk, so it runs only while the .NET page is open.
/// </summary>
public sealed class ExceptionFeed : RuntimeFeed
{
    private const ulong ExceptionKeyword = 0x8000;
    private const uint ErrorLevel = 2;
    private const int ExceptionThrownEvent = 80;

    /// <summary>A process throwing ever-different messages would otherwise grow this without end.</summary>
    private const int MaxKinds = 500;

    private const int MaxMessageLength = 1000;

    private readonly Dictionary<(string Type, string Message), (long Count, DateTime Last)> _kinds = [];
    private long _total;
    private string _summary = "None thrown since this page opened.";
    private string _tabText = "Exceptions";

    internal ExceptionFeed(int processId)
        : base(processId, new EventPipeProvider(RuntimeProvider, ExceptionKeyword, ErrorLevel), "its exceptions")
    {
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<ThrownException> Kinds { get; } = [];

    public bool HasKinds => Kinds.Count > 0;

    /// <summary>"34 thrown since this page opened, of 3 kinds".</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>"Exceptions (34)", for the switch that shows them.</summary>
    public string TabText
    {
        get => _tabText;
        private set => Set(ref _tabText, value);
    }

    /// <summary>The list as shown, as tab-separated text with a header line.</summary>
    internal string AsText() =>
        "Type\tMessage\tCount\tLast thrown" + Environment.NewLine
        + string.Concat(Kinds.Select(k => $"{k.FullName}\t{k.Message.Replace('\t', ' ')}\t{k.Count}\t{k.LastThrown:O}{Environment.NewLine}"));

    private protected override void Reset()
    {
        _kinds.Clear();
        _total = 0;
    }

    private protected override void Show()
    {
        List<ThrownException> kinds;
        long total;
        lock (Gate)
        {
            total = _total;
            kinds = _kinds
                .Select(k => new ThrownException(k.Key.Type, k.Key.Message, k.Value.Count, k.Value.Last))
                .OrderByDescending(k => k.LastThrown)
                .ToList();
        }

        Kinds.Clear();
        foreach (var kind in kinds)
            Kinds.Add(kind);
        OnPropertyChanged(nameof(HasKinds));

        Summary = total == 0
            ? "None thrown since this page opened, or the list was cleared."
            : $"{Format.Number(total)} thrown, of {Format.Number(kinds.Count)} {(kinds.Count == 1 ? "kind" : "kinds")}";
        TabText = total == 0 ? "Exceptions" : $"Exceptions ({Format.Number(total)})";
    }

    /// <summary>ExceptionThrown: type, message, then the throwing address, HRESULT and flags (unused).</summary>
    private protected override void OnEvent(EventMetadata metadata, ReadOnlySpan<byte> payload, int pointerSize)
    {
        if (metadata.EventId != ExceptionThrownEvent)
            return;

        var reader = new SpanReader(payload);
        var type = reader.String();
        var message = reader.String();
        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength] + "…";

        var now = DateTime.Now;
        lock (Gate)
        {
            _total++;
            Changed();
            var key = (type, message);
            if (_kinds.TryGetValue(key, out var seen))
            {
                _kinds[key] = (seen.Count + 1, now);
                return;
            }

            if (_kinds.Count >= MaxKinds)
                _kinds.Remove(_kinds.MinBy(k => k.Value.Last).Key);
            _kinds[key] = (1, now);
        }
    }
}
