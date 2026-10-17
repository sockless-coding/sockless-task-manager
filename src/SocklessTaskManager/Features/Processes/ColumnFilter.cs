using System.Globalization;
using System.Text.RegularExpressions;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// A column's filter, parsed from what the user typed into the filter row.
/// Text columns: <c>chrome</c> (contains), <c>=SYSTEM</c> (exactly), <c>!svchost</c> (doesn't
/// contain), <c>chrome|edge</c> (either). Number columns: <c>&gt; 5</c>, <c>&lt;= 2</c>,
/// <c>10-50</c>, and a plain number means "at least" (or "exactly" for ids). Byte columns take
/// units (<c>&gt; 100 MB</c>) and default to MB.
/// </summary>
internal abstract partial class ColumnFilter
{
    public abstract bool Matches(TableColumn column, ProcessReading process);

    /// <summary>Null for an empty filter; throws <see cref="FormatException"/> for one that can't be read.</summary>
    public static ColumnFilter? Parse(string text, ColumnKind kind)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        return kind switch
        {
            ColumnKind.Text or ColumnKind.Label => TextFilter.From(text),
            _ => NumberFilter.From(text, kind),
        };
    }

    private sealed class TextFilter(string[] alternatives, bool exact, bool negate) : ColumnFilter
    {
        public static TextFilter From(string text)
        {
            var negate = text.StartsWith('!');
            if (negate)
                text = text[1..].TrimStart();
            var exact = text.StartsWith('=');
            if (exact)
                text = text[1..].TrimStart();

            var alternatives = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (alternatives.Length == 0)
                throw new FormatException("Nothing to match.");
            return new TextFilter(alternatives, exact, negate);
        }

        public override bool Matches(TableColumn column, ProcessReading process)
        {
            var value = column.MatchText(process);
            var any = false;
            foreach (var alternative in alternatives)
            {
                if (exact
                        ? string.Equals(value, alternative, StringComparison.OrdinalIgnoreCase)
                        : value.Contains(alternative, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    break;
                }
            }

            return any != negate;
        }
    }

    private sealed partial class NumberFilter(double low, double high, bool lowInclusive, bool highInclusive) : ColumnFilter
    {
        public static NumberFilter From(string text, ColumnKind kind)
        {
            var range = Range().Match(text);
            if (range.Success)
            {
                // "10-50 MB": a unit written once applies to both ends.
                var unit = range.Groups["bu"].Value;
                var from = Number(range.Groups["a"].Value, range.Groups["au"].Value is { Length: > 0 } first ? first : unit, kind);
                var to = Number(range.Groups["b"].Value, unit, kind);
                return new NumberFilter(Math.Min(from, to), Math.Max(from, to), true, true);
            }

            var comparison = Comparison().Match(text);
            if (!comparison.Success)
                throw new FormatException("Not a number or comparison.");

            var value = Number(comparison.Groups["n"].Value, comparison.Groups["u"].Value, kind);
            return comparison.Groups["op"].Value switch
            {
                ">" => new NumberFilter(value, double.PositiveInfinity, false, true),
                ">=" or "≥" => new NumberFilter(value, double.PositiveInfinity, true, true),
                "<" => new NumberFilter(double.NegativeInfinity, value, true, false),
                "<=" or "≤" => new NumberFilter(double.NegativeInfinity, value, true, true),
                "=" => new NumberFilter(value, value, true, true),
                _ when kind == ColumnKind.Id => new NumberFilter(value, value, true, true),
                _ => new NumberFilter(value, double.PositiveInfinity, true, true),
            };
        }

        public override bool Matches(TableColumn column, ProcessReading process)
        {
            var value = column.Number(process);
            return (lowInclusive ? value >= low : value > low) && (highInclusive ? value <= high : value < high);
        }

        /// <summary>Accepts a comma or a point for decimals, whatever the culture.</summary>
        private static double Number(string digits, string unit, ColumnKind kind)
        {
            if (!double.TryParse(digits.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException("Not a number.");

            unit = unit.Trim().ToUpperInvariant();
            if (unit.EndsWith("/S", StringComparison.Ordinal))
                unit = unit[..^2];

            var isBytes = kind is ColumnKind.Bytes or ColumnKind.BytesPerSecond;
            if (unit.Length > 0 && !isBytes && unit != "%")
                throw new FormatException("This column has no units.");

            return !isBytes ? value : value * unit switch
            {
                "" or "MB" or "M" or "MIB" => 1024d * 1024,
                "B" => 1,
                "KB" or "K" or "KIB" => 1024,
                "GB" or "G" or "GIB" => 1024d * 1024 * 1024,
                "TB" or "T" or "TIB" => 1024d * 1024 * 1024 * 1024,
                _ => throw new FormatException("Unknown unit."),
            };
        }

        [GeneratedRegex(@"^(?<op>>=|<=|≥|≤|>|<|=)?\s*(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[a-zA-Z%/]*)$")]
        private static partial Regex Comparison();

        [GeneratedRegex(@"^(?<a>\d+(?:[.,]\d+)?)\s*(?<au>[a-zA-Z/]*)\s*(?:-|\.\.)\s*(?<b>\d+(?:[.,]\d+)?)\s*(?<bu>[a-zA-Z/]*)$")]
        private static partial Regex Range();
    }
}
