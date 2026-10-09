using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Briefcase.ServerApp;

// The protocol is UTF-8, one bounded JSON message per line. Consumers do not
// depend on named pipes, so a Unix socket or authenticated network stream can
// implement the same contract later.
public interface IServerTransport
{
    Task<Stream> ConnectAsync(int processId, CancellationToken cancellationToken);
}

public sealed class WindowsNamedPipeTransport : IServerTransport
{
    public async Task<Stream> ConnectAsync(int processId, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", $"Briefcase.Server.{processId}",
            PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1000, cancellationToken);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }
}

public sealed class ServerLiveClient : IDisposable
{
    private readonly IServerTransport _transport;
    private readonly string? _expectedInstanceId;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Ok, string Message)>> _pending = new();
    private Stream? _connected;
    private ServerSnapshot? _latest;
    private long _receivedTicks;
    private long _rejectedSnapshots;
    private readonly long _createdTicks = Environment.TickCount64;
    public int ProcessId { get; }
    public event Action? StateChanged;
    public TimeSpan ConnectionAge => TimeSpan.FromMilliseconds(Environment.TickCount64 - _createdTicks);

    public ServerLiveClient(int processId, IServerTransport transport, Action? onStateChanged = null,
        string? expectedInstanceId = null)
    {
        ProcessId = processId;
        _transport = transport;
        _expectedInstanceId = expectedInstanceId;
        StateChanged += onStateChanged;
        _reader = Task.Run(ReadAsync);
    }

    public ServerSnapshot? Latest =>
        Environment.TickCount64 - Interlocked.Read(ref _receivedTicks) <= 5000
            ? Volatile.Read(ref _latest) : null;

    public LiveChannelStatus Status
    {
        get
        {
            if (Volatile.Read(ref _connected) is null) return LiveChannelStatus.Disconnected;
            var received = Interlocked.Read(ref _receivedTicks);
            if (received == 0) return Interlocked.Read(ref _rejectedSnapshots) > 0
                ? LiveChannelStatus.InvalidData : LiveChannelStatus.Connected;
            return Environment.TickCount64 - received <= 5000
                ? LiveChannelStatus.Connected : LiveChannelStatus.Stale;
        }
    }

    public async Task<(bool Ok, string Message)> KickAsync(string playerToken, CancellationToken cancellationToken)
    {
        if (playerToken.Length is < 1 or > 64 || !playerToken.All(char.IsAsciiLetterOrDigit))
            return (false, "Invalid player selection.");
        return await SendCommandAsync("kick", playerToken, null, null, null, cancellationToken);
    }

    public Task<(bool Ok, string Message)> MoveTeamAsync(string playerToken, int team, int teamSize,
        CancellationToken cancellationToken) =>
        SendCommandAsync("moveTeam", playerToken, null, team, teamSize, cancellationToken);

    public Task<(bool Ok, string Message)> SwapTeamAsync(string playerToken, string otherToken, int teamSize,
        CancellationToken cancellationToken) =>
        SendCommandAsync("swapTeam", playerToken, otherToken, null, teamSize, cancellationToken);

    private async Task<(bool Ok, string Message)> SendCommandAsync(string command, string playerToken,
        string? otherToken, int? team, int? teamSize, CancellationToken cancellationToken)
    {
        if (command is not ("kick" or "moveTeam" or "swapTeam") ||
            playerToken.Length is < 1 or > 64 || !playerToken.All(char.IsAsciiLetterOrDigit) ||
            otherToken is { } other && (other.Length is < 1 or > 64 ||
                !other.All(char.IsAsciiLetterOrDigit)) ||
            team is < 0 or >= 32 || teamSize is not null and not (2 or 3))
            return (false, "Invalid team selection.");
        var stream = Volatile.Read(ref _connected);
        if (stream is null || Latest is null) return (false, "The live server connection is unavailable.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) return (false, "Unable to queue the command.");
        try
        {
            // These identifiers are validated as ASCII alphanumerics and the command
            // names are fixed above. Building this bounded message avoids AOT reflection.
            var message = new StringBuilder(180)
                .Append("{\"schemaVersion\":2,\"type\":\"command\",\"command\":\"")
                .Append(command).Append("\",\"requestId\":\"").Append(requestId)
                .Append("\",\"playerToken\":\"").Append(playerToken).Append('"');
            if (otherToken is not null) message.Append(",\"otherToken\":\"").Append(otherToken).Append('"');
            if (team is not null) message.Append(",\"team\":").Append(team.Value);
            if (teamSize is not null) message.Append(",\"teamSize\":").Append(teamSize.Value);
            message.Append("}\n");
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                if (!ReferenceEquals(Volatile.Read(ref _connected), stream))
                    return (false, "The server connection changed.");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(message.ToString()), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally { _writeGate.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        { return (false, "The server did not acknowledge the command."); }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private void ReadResult(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("type").GetString() != "result") return;
            var requestId = root.GetProperty("requestId").GetString();
            if (requestId is not null && _pending.TryRemove(requestId, out var completion))
                completion.TrySetResult((root.GetProperty("ok").GetBoolean(),
                    root.GetProperty("message").GetString() ?? ""));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { /* Other messages and malformed results cannot complete a command. */ }
    }

    private void FailPending()
    {
        foreach (var (key, completion) in _pending)
            if (_pending.TryRemove(key, out _))
                completion.TrySetResult((false, "The server connection closed."));
    }

    private async Task ReadAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var stream = await _transport.ConnectAsync(ProcessId, _stop.Token);
                Interlocked.Exchange(ref _receivedTicks, 0);
                Interlocked.Exchange(ref _rejectedSnapshots, 0);
                Volatile.Write(ref _latest, null);
                Volatile.Write(ref _connected, stream);
                StateChanged?.Invoke();
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                string? previousSnapshot = null;
                while (!_stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) break;
                    if (line.Length > 65536) break;
                    if (line.Contains("\"type\":\"result\"", StringComparison.Ordinal))
                    {
                        ReadResult(line);
                        continue;
                    }
                    var wasStale = Environment.TickCount64 - Interlocked.Read(ref _receivedTicks) > 5000;
                    if (string.Equals(previousSnapshot, line, StringComparison.Ordinal))
                    {
                        Interlocked.Exchange(ref _receivedTicks, Environment.TickCount64);
                        if (wasStale) StateChanged?.Invoke();
                        continue;
                    }
                    var parsed = ServerSnapshotReader.ParseLive(line, ProcessId, _expectedInstanceId);
                    if (parsed is null)
                    {
                        if (Interlocked.Increment(ref _rejectedSnapshots) == 1)
                            StateChanged?.Invoke();
                        continue;
                    }
                    Volatile.Write(ref _latest, parsed);
                    Interlocked.Exchange(ref _receivedTicks, Environment.TickCount64);
                    previousSnapshot = line;
                    StateChanged?.Invoke();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                OperationCanceledException or InvalidOperationException or TimeoutException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested) break;
            }
            var disconnected = Interlocked.Exchange(ref _connected, null);
            FailPending();
            Volatile.Write(ref _latest, null);
            if (disconnected is not null && !_stop.IsCancellationRequested)
                StateChanged?.Invoke();
            try { await Task.Delay(1000, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        var connected = Interlocked.Exchange(ref _connected, null);
        try { connected?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        FailPending();
        // Release the server's single pipe slot immediately so another app session can reconnect.
        _ = _reader.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
