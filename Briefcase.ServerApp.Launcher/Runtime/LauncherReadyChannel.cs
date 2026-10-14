using System.Net.Sockets;

namespace Briefcase.ServerApp.Launcher;

/// <summary>Receives the app's ready signal only after its main window opens.</summary>
internal sealed class LauncherReadyChannel : IDisposable
{
    private readonly EventWaitHandle? _windowsEvent;
    private readonly Socket? _unixListener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<bool>? _unixReady;
    public string Name { get; }

    public LauncherReadyChannel()
    {
        if (OperatingSystem.IsWindows())
        {
            Name = "Local\\Briefcase.ServerApp.Ready." + Guid.NewGuid().ToString("N");
            _windowsEvent = new EventWaitHandle(false, EventResetMode.ManualReset, Name);
            return;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Name = "/tmp/briefcase-ready-" + Guid.NewGuid().ToString("N");
        _unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _unixListener.Bind(new UnixDomainSocketEndPoint(Name));
            File.SetUnixFileMode(Name, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _unixListener.Listen(1);
            _unixReady = WaitForAppAsync(_unixListener, _stop.Token);
        }
        catch
        {
            _unixListener.Dispose();
            if (File.Exists(Name)) File.Delete(Name);
            throw;
        }
    }

    public bool IsReady => _windowsEvent?.WaitOne(0) == true || _unixReady is { IsCompletedSuccessfully: true, Result: true };

    private static async Task<bool> WaitForAppAsync(Socket listener, CancellationToken cancellationToken)
    {
        try
        {
            using var connected = await listener.AcceptAsync(cancellationToken);
            var value = new byte[1];
            return await connected.ReceiveAsync(value, SocketFlags.None, cancellationToken) == 1 && value[0] == 1;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        { return false; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _windowsEvent?.Dispose();
        _unixListener?.Dispose();
        if (_unixListener is not null && File.Exists(Name)) File.Delete(Name);
        _stop.Dispose();
    }
}
