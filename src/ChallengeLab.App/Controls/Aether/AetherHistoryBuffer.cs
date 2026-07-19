namespace ChallengeLab.App.Controls.Aether;

/// <summary>Fixed-capacity ring of recent energy samples for sparkline drawing.</summary>
internal sealed class AetherHistoryBuffer
{
    private readonly double?[] _ias;
    private readonly double?[] _vs;
    private readonly double?[] _pathError;
    private int _write;
    private int _count;
    private long _lastSequence = -1;

    public AetherHistoryBuffer(int capacity = 48)
    {
        capacity = Math.Clamp(capacity, 8, 128);
        _ias = new double?[capacity];
        _vs = new double?[capacity];
        _pathError = new double?[capacity];
    }

    public int Count => _count;
    public int Capacity => _ias.Length;

    public void Clear()
    {
        Array.Clear(_ias);
        Array.Clear(_vs);
        Array.Clear(_pathError);
        _write = 0;
        _count = 0;
        _lastSequence = -1;
    }

    public void Push(AetherSnapshot snapshot)
    {
        if (snapshot.Sequence == _lastSequence)
            return;

        if (!snapshot.IsConnected || !snapshot.IsFlightActive)
        {
            Clear();
            return;
        }

        _lastSequence = snapshot.Sequence;
        _ias[_write] = snapshot.Energy.IasKts;
        _vs[_write] = snapshot.Energy.VerticalSpeedFpm;
        _pathError[_write] = snapshot.Path.PathErrorDeg;
        _write = (_write + 1) % Capacity;
        if (_count < Capacity)
            _count++;
    }

    public void CopyIas(Span<double?> destination) => Copy(_ias, destination);

    public void CopyVerticalSpeed(Span<double?> destination) => Copy(_vs, destination);

    public void CopyPathError(Span<double?> destination) => Copy(_pathError, destination);

    private void Copy(double?[] source, Span<double?> destination)
    {
        var n = Math.Min(_count, destination.Length);
        if (n == 0)
            return;

        var start = (_write - _count + Capacity) % Capacity;
        for (var i = 0; i < n; i++)
            destination[i] = source[(start + i) % Capacity];
    }
}
