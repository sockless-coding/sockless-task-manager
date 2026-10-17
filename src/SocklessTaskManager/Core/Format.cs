using System.Globalization;

namespace Sockless.TaskManager.Core;

/// <summary>Instrument-readout formatting shared by every slice.</summary>
public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] BitRateUnits = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Percent(double value) => $"{Math.Round(Math.Clamp(value, 0, 100)).ToString("0", Culture)}%";

    /// <summary>Binary units, like Task Manager: 16.0 GB, 512 MB.</summary>
    public static string Bytes(double bytes, int decimals = 1)
    {
        var unit = 0;
        while (Math.Abs(bytes) >= 1024 && unit < ByteUnits.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        var digits = unit == 0 ? 0 : decimals;
        return $"{bytes.ToString("F" + digits, Culture)} {ByteUnits[unit]}";
    }

    public static string BytesPerSecond(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    /// <summary>Decimal bit rates, like network adapters report them: 8.4 Mbps.</summary>
    public static string BitsPerSecond(double bitsPerSecond)
    {
        var unit = 0;
        while (Math.Abs(bitsPerSecond) >= 1000 && unit < BitRateUnits.Length - 1)
        {
            bitsPerSecond /= 1000;
            unit++;
        }

        var digits = unit == 0 || bitsPerSecond >= 100 ? 0 : 1;
        return $"{bitsPerSecond.ToString("F" + digits, Culture)} {BitRateUnits[unit]}";
    }

    /// <summary>Two rates sharing the larger one's unit, like Task Manager: "S: 0.1 R: 2.4 Mbps".</summary>
    public static string BitsPerSecondPair(string firstLabel, double first, string secondLabel, double second)
    {
        var unit = 0;
        var divisor = 1d;
        while (Math.Max(first, second) / divisor >= 1000 && unit < BitRateUnits.Length - 1)
        {
            divisor *= 1000;
            unit++;
        }

        string Number(double v) => (v / divisor).ToString(unit == 0 ? "F0" : "F1", Culture);
        return $"{firstLabel}: {Number(first)} {secondLabel}: {Number(second)} {BitRateUnits[unit]}";
    }

    public static string Gigahertz(double megahertz) => $"{(megahertz / 1000).ToString("F2", Culture)} GHz";

    public static string Milliseconds(double ms) => $"{ms.ToString(ms < 10 ? "F1" : "F0", Culture)} ms";

    public static string Number(long value) => value.ToString("N0", Culture);

    public static string Uptime(TimeSpan span) =>
        $"{(int)span.TotalDays}:{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";

    /// <summary>Rounds a value up to a 1/2/5 step so graph scales read cleanly.</summary>
    public static double NiceCeiling(double value, double minimum)
    {
        value = Math.Max(value, minimum);
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in (ReadOnlySpan<double>)[1, 2, 5, 10])
        {
            if (value <= step * magnitude)
                return step * magnitude;
        }

        return 10 * magnitude;
    }
}
