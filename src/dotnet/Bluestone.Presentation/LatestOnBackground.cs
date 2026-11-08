namespace Bluestone.Presentation;

/// <summary>
/// Hands values to <c>send</c> on a background thread, so the caller (the UI thread) never waits for it. Only the latest
/// value matters: one posted while an earlier one is being sent replaces any still waiting, and values are sent in order.
/// </summary>
public sealed class LatestOnBackground<T>(Action<T> send)
{
    private readonly Action<T> _send = send ?? throw new ArgumentNullException(nameof(send));
    private readonly Lock _gate = new();
    private T? _pending;
    private bool _hasPending;
    private bool _sending;

    public void Post(T value)
    {
        lock (_gate)
        {
            _pending = value;
            _hasPending = true;
            if (_sending)
            {
                return;
            }

            _sending = true;
        }

        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    _sending = false;
                    return;
                }

                value = _pending!;
                _pending = default;
                _hasPending = false;
            }

            _send(value);
        }
    }
}
