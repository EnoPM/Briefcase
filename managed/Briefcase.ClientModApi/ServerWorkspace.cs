namespace Briefcase.ClientModApi;

/// <summary>
/// Private bridge shared by Briefcase's built-in server directory, remote
/// administration client, and Avalonia menu. Keeping it in the shared client
/// API assembly gives every built-in the same process-local selection without
/// exposing menu routing as a public mod API.
/// </summary>
internal static class ServerWorkspace
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, Action<ServerWorkspaceEvent>> Observers = [];
    private static long _nextObserverId;
    private static ServerWorkspaceProfile? _selected;

    public static IDisposable Subscribe(
        Action<ServerWorkspaceEvent> observer,
        bool replaySelection = false)
    {
        ArgumentNullException.ThrowIfNull(observer);
        long id;
        ServerWorkspaceProfile? selected;
        lock (Gate)
        {
            id = ++_nextObserverId;
            Observers.Add(id, observer);
            selected = _selected;
        }

        if (replaySelection && selected is not null)
            observer(new ServerWorkspaceEvent(selected, OpenAdministration: false));
        return new Subscription(id);
    }

    public static void Select(ServerWorkspaceProfile profile) =>
        Publish(profile, openAdministration: false);

    public static void OpenAdministration(ServerWorkspaceProfile profile) =>
        Publish(profile, openAdministration: true);

    public static void ClearSelection()
    {
        Action<ServerWorkspaceEvent>[] observers;
        lock (Gate)
        {
            _selected = null;
            observers = Observers.Values.ToArray();
        }
        Notify(observers, new ServerWorkspaceEvent(null, OpenAdministration: false));
    }

    private static void Publish(ServerWorkspaceProfile profile, bool openAdministration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Action<ServerWorkspaceEvent>[] observers;
        lock (Gate)
        {
            _selected = profile;
            observers = Observers.Values.ToArray();
        }
        Notify(observers, new ServerWorkspaceEvent(profile, openAdministration));
    }

    private static void Notify(
        IEnumerable<Action<ServerWorkspaceEvent>> observers,
        ServerWorkspaceEvent notification)
    {
        foreach (var observer in observers)
        {
            try { observer(notification); }
            catch
            {
                // A stale UI observer must not stop the selected server from
                // reaching the other built-ins. Its owner reports UI errors.
            }
        }
    }

    private sealed class Subscription(long id) : IDisposable
    {
        private long _id = id;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _id, 0);
            if (current == 0) return;
            lock (Gate) Observers.Remove(current);
        }
    }
}

internal sealed record ServerWorkspaceProfile(
    string Id,
    string Name,
    string GameEndpoint,
    string GamePassword,
    string AdministrationEndpoint,
    string AdministrationPassword);

internal sealed record ServerWorkspaceEvent(
    ServerWorkspaceProfile? Profile,
    bool OpenAdministration);
