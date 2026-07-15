namespace VideoSplitJoiner.Core.Ffmpeg;

/// <summary>
/// Fixed-capacity ring buffer that keeps only the last N lines fed to it — used to
/// retain the tail of a subprocess's stderr without unbounded memory growth.
/// </summary>
internal sealed class RollingTail
{
    private readonly int _capacity;
    private readonly Queue<string> _lines;

    public RollingTail(int capacity)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _lines = new Queue<string>(_capacity);
    }

    /// <summary>Add a line, evicting the oldest if at capacity.</summary>
    public void Add(string line)
    {
        if (_lines.Count >= _capacity)
        {
            _lines.Dequeue();
        }

        _lines.Enqueue(line);
    }

    /// <summary>An immutable snapshot of the retained lines, oldest first.</summary>
    public IReadOnlyList<string> Snapshot() => _lines.ToArray();
}
