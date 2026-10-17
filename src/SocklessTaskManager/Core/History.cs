namespace Sockless.TaskManager.Core;

/// <summary>
/// A fixed-length ring of samples for a history graph. Oldest first when indexed.
/// Push from the UI thread; <see cref="Changed"/> fires there so graphs can redraw.
/// </summary>
public sealed class History
{
    public const int DefaultLength = 60;

    private readonly double[] _values;
    private int _start;

    public History(int length = DefaultLength)
    {
        _values = new double[length];
    }

    public event EventHandler? Changed;

    public int Length => _values.Length;

    /// <summary>Number of real samples pushed so far (up to <see cref="Length"/>).</summary>
    public int Count { get; private set; }

    public double this[int index] => _values[(_start + index) % _values.Length];

    public double Latest => Count == 0 ? 0 : this[_values.Length - 1];

    public double Max()
    {
        var max = 0d;
        foreach (var v in _values)
            max = Math.Max(max, v);
        return max;
    }

    public void Push(double value)
    {
        _values[_start] = double.IsFinite(value) ? value : 0;
        _start = (_start + 1) % _values.Length;
        Count = Math.Min(Count + 1, _values.Length);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
