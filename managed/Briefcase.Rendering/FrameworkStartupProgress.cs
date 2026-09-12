namespace Briefcase.Rendering;

/// <summary>An immutable view of Briefcase's current managed startup phase.</summary>
public readonly record struct FrameworkStartupSnapshot(
    double Progress,
    string Stage,
    string Detail,
    bool IsComplete,
    bool HasFailed);

/// <summary>
/// Thread-safe progress channel shared by the managed bootstrap and UI backends.
/// Progress is monotonic so a late or concurrent report cannot move the bar backwards.
/// </summary>
public sealed class FrameworkStartupProgress
{
    private readonly object _gate = new();
    private FrameworkStartupSnapshot _snapshot = new(
        0.01, "Starting Briefcase", "Preparing the managed runtime...", false, false);
    private Action<FrameworkStartupSnapshot>? _changed;

    public FrameworkStartupSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public event Action<FrameworkStartupSnapshot> Changed
    {
        add { lock (_gate) _changed += value; }
        remove { lock (_gate) _changed -= value; }
    }

    public void Report(double progress, string stage, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (!double.IsFinite(progress))
            throw new ArgumentOutOfRangeException(nameof(progress));

        Action<FrameworkStartupSnapshot>? changed;
        FrameworkStartupSnapshot snapshot;
        lock (_gate)
        {
            if (_snapshot.IsComplete) return;
            snapshot = new FrameworkStartupSnapshot(
                Math.Max(_snapshot.Progress, Math.Clamp(progress, 0, 0.99)),
                stage.Trim(),
                detail?.Trim() ?? "",
                false,
                false);
            _snapshot = snapshot;
            changed = _changed;
        }
        changed?.Invoke(snapshot);
    }

    public void Complete()
    {
        Publish(new FrameworkStartupSnapshot(
            1, "Briefcase is ready", "Mods and Core services are loaded.", true, false));
    }

    public void Fail(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Publish(new FrameworkStartupSnapshot(
            1, "Briefcase could not finish loading", message.Trim(), true, true));
    }

    private void Publish(FrameworkStartupSnapshot snapshot)
    {
        Action<FrameworkStartupSnapshot>? changed;
        lock (_gate)
        {
            if (_snapshot.IsComplete) return;
            _snapshot = snapshot;
            changed = _changed;
        }
        changed?.Invoke(snapshot);
    }
}
