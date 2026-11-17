namespace Sockless.TaskManager.Features.Apps;

/// <summary>One row of <c>winget upgrade</c>: an installed package with a newer version in a source.</summary>
internal sealed record WingetRow(string Name, string Id, string Version, string Available, string Source);

/// <summary>
/// Reads the tables <c>winget upgrade</c> prints. Redirected, winget writes them at full width
/// without truncation; the column titles are translated, so columns are found by where the titles
/// start (the line above a line of dashes), in winget's fixed order: name, id, version, available,
/// source. A name with East Asian wide characters takes two columns per character, so the rest of
/// its row is shifted back by that many.
/// </summary>
internal static class WingetTable
{
    public static List<WingetRow> Parse(string output)
    {
        var rows = new List<WingetRow>();
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(Clean).ToArray();
        for (var i = 1; i < lines.Length; i++)
        {
            if (!IsRule(lines[i]))
                continue;

            var starts = ColumnStarts(lines[i - 1]);
            if (starts.Count < 4)
                continue;

            for (var j = i + 1; j < lines.Length && lines[j].Trim().Length > 0 && !IsRule(lines[j]); j++)
            {
                if (Row(lines[j], starts) is { } row)
                    rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>What's left after carriage returns: progress spinners redraw a line in place.</summary>
    private static string Clean(string line)
    {
        var at = line.LastIndexOf('\r');
        return (at >= 0 ? line[(at + 1)..] : line).TrimEnd();
    }

    private static bool IsRule(string line) => line.Length >= 20 && line.All(c => c == '-');

    /// <summary>Where each title starts: after two or more spaces, or at the start.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != ' ' && (i == 0 || (i >= 2 && header[i - 1] == ' ' && header[i - 2] == ' ') || (i == 1 && header[0] == ' ')))
                starts.Add(i);
        }

        return starts;
    }

    private static WingetRow? Row(string line, List<int> starts)
    {
        // The id never has spaces or wide characters: find the shift from the name's wide characters.
        var shift = 0;
        for (var i = 0; i < Math.Min(starts[1], line.Length) - shift; i++)
        {
            if (IsWide(line[i]))
                shift++;
        }

        string Cell(int column)
        {
            var start = starts[column] - shift;
            var end = column + 1 < starts.Count ? starts[column + 1] - shift : line.Length;
            if (start >= line.Length)
                return string.Empty;
            return line[start..Math.Min(end, line.Length)].Trim();
        }

        var name = line[..Math.Min(Math.Max(starts[1] - shift, 0), line.Length)].Trim();
        var id = Cell(1);
        var available = Cell(3);
        if (name.Length == 0 || id.Length == 0 || id.Contains(' ') || available.Length == 0)
            return null;

        return new WingetRow(name, id, Cell(2), available, starts.Count > 4 ? Cell(4) : string.Empty);
    }

    /// <summary>East Asian wide and full-width characters, which a console draws two columns wide.</summary>
    private static bool IsWide(char c) =>
        c is (>= 'ᄀ' and <= 'ᅟ') or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣')
            or (>= '豈' and <= '﫿') or (>= '︰' and <= '﹏') or (>= '＀' and <= '｠') or (>= '￠' and <= '￦');
}
