using System.Collections.Concurrent;
using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Bridges the administration worker thread and Unreal's game thread. Only the
/// game thread touches player UObjects or invokes RPCs; the TCP endpoint sees
/// immutable managed snapshots and opaque tokens.
/// </summary>
internal sealed class ServerPlayerRegistry
{
    private readonly Dictionary<UnrealObjectHandle, TrackedPlayer> _byController = [];
    private readonly HashSet<UnrealObjectHandle> _kickedControllers = [];
    private readonly ConcurrentQueue<KickRequest> _pendingKicks = new();
    private readonly List<ScheduledKick> _scheduledKicks = [];
    private ServerPlayerEnvelope[] _snapshot = [];
    private long _nextPublish;
    private bool _pumpReported;

    public IReadOnlyList<ServerPlayerEnvelope> Snapshot() =>
        Volatile.Read(ref _snapshot);

    public void QueueKick(string? playerToken, string? reason)
    {
        playerToken = ValidateText(playerToken, "Player token", 64, allowEmpty: false);
        reason = ValidateText(reason, "Kick message", 256, allowEmpty: true);
        if (reason.Length == 0) reason = "Removed by the server administrator.";

        var player = Snapshot().FirstOrDefault(candidate =>
            candidate.PlayerToken.Equals(playerToken, StringComparison.Ordinal));
        if (player is null)
            throw new InvalidOperationException("The selected player is no longer connected.");
        if (player.IsBot)
            throw new InvalidOperationException("Bots cannot be kicked through player administration.");
        _pendingKicks.Enqueue(new KickRequest(playerToken, reason));
    }

    /// <summary>
    /// Registers a controller delivered by GameModeBase.K2_PostLogin. The
    /// PlayerState can still be incomplete, so later game-thread pumps refresh
    /// the same controller before publishing its final name and attributes.
    /// </summary>
    public void Register(
        UnrealApi unreal,
        UnrealObjectReference controllerReference,
        Action<string> info,
        Action<string> warning)
    {
        if (controllerReference.IsNull) return;
        try
        {
            var controller = unreal.FromReference<DeceiveIncPlayerController>(controllerReference);
            // A genuine PostLogin means this controller represents a new server
            // admission. This also permits a reconnect if Unreal happens to reuse
            // the same object handle after an earlier kick.
            _kickedControllers.Remove(controller.Handle);
            var added = EnsureTracked(controller.Handle);
            Observe(unreal, controller, warning);
            PublishSnapshot();
            if (added)
                info("Registered a connected player controller from K2_PostLogin.");
        }
        catch (Exception exception)
        {
            warning($"Could not register a connected player: {exception.Message}");
        }
    }

    /// <summary>
    /// Refreshes a controller supplied by a player event and provides a safe
    /// game-thread opportunity to execute queued administration requests.
    /// </summary>
    public void ObserveAndPump(
        UnrealApi unreal,
        DeceiveIncPlayerController currentController,
        Action<string> info,
        Action<string> warning)
    {
        ProcessPendingKicks(unreal, info, warning);
        // ClientReturnToMainMenu is asynchronous. The kicked controller can keep
        // receiving events briefly, but it must not reappear in the public list.
        if (!_kickedControllers.Contains(currentController.Handle))
        {
            EnsureTracked(currentController.Handle);
            Observe(unreal, currentController, warning);
        }
        PublishIfDue();
    }

    /// <summary>
    /// Called by the authoritative GameMode tick. TCP workers only enqueue
    /// requests; every UObject read and RPC invocation remains on this thread.
    /// </summary>
    public void Pump(
        UnrealApi unreal,
        Action<string> info,
        Action<string> warning)
    {
        if (!_pumpReported)
        {
            _pumpReported = true;
            info("Player administration game-thread pump is active.");
        }

        ProcessPendingKicks(unreal, info, warning);
        foreach (var handle in _byController.Keys.ToArray())
        {
            try
            {
                var reference = new UnrealObjectReference(handle);
                var controller = unreal.FromReference<DeceiveIncPlayerController>(reference);
                Observe(unreal, controller, warning);
            }
            catch (UnrealApiException exception) when (
                exception.Result is NativeUnrealResult.StaleHandle or NativeUnrealResult.NotFound)
            {
                _byController.Remove(handle);
            }
            catch (Exception exception)
            {
                warning($"Could not refresh a connected player: {exception.Message}");
            }
        }
        PublishIfDue();
    }

    private void PublishIfDue()
    {
        var now = Environment.TickCount64;
        if (now < _nextPublish) return;
        _nextPublish = now + 250;
        PublishSnapshot();
    }

    public void Remove(UnrealObjectReference controllerReference)
    {
        if (controllerReference.IsNull) return;
        _byController.Remove(controllerReference.Handle);
        _kickedControllers.Remove(controllerReference.Handle);
        _scheduledKicks.RemoveAll(kick => kick.Player.ControllerHandle == controllerReference.Handle);
        PublishSnapshot();
    }

    public void Clear()
    {
        _byController.Clear();
        _kickedControllers.Clear();
        _scheduledKicks.Clear();
        while (_pendingKicks.TryDequeue(out _)) { }
        _pumpReported = false;
        Volatile.Write(ref _snapshot, []);
    }

    /// <summary>
    /// Re-enters the game's own server RPC after a successful live profile
    /// reload. Vanilla code owns compression, hashing, transmission and client
    /// acknowledgement; Briefcase never manufactures those protocol values.
    /// </summary>
    public int BroadcastCommunityBalanceProfile(
        UnrealApi unreal,
        Action<string> warning)
    {
        var sent = 0;
        foreach (var tracked in _byController.Values.ToArray())
        {
            if (tracked.IsBot || _kickedControllers.Contains(tracked.ControllerHandle))
                continue;
            try
            {
                var reference = new UnrealObjectReference(tracked.ControllerHandle);
                var controller = unreal.FromReference<DeceiveIncPlayerController>(reference);
                controller.Server_RequestCommunityBalanceProfile();
                sent++;
            }
            catch (UnrealApiException exception) when (
                exception.Result is NativeUnrealResult.StaleHandle or NativeUnrealResult.NotFound)
            {
                _byController.Remove(tracked.ControllerHandle);
            }
            catch (Exception exception)
            {
                warning(
                    $"Could not refresh community balance for {tracked.DisplayName}: " +
                    exception.Message);
            }
        }
        PublishSnapshot();
        return sent;
    }

    private void Observe(
        UnrealApi unreal,
        DeceiveIncPlayerController controller,
        Action<string> warning)
    {
        if (_kickedControllers.Contains(controller.Handle)) return;
        try
        {
            var playerStateReference = controller.PlayerState;
            if (playerStateReference.IsNull) return;
            var playerState = unreal.FromReference<DIPlayerState>(playerStateReference);
            if (playerState.bIsInactive)
            {
                _byController.Remove(controller.Handle);
                return;
            }

            var displayName = playerState.PlayerDisplayName.Trim();
            if (displayName.Length == 0)
                displayName = playerState.bIsABot
                    ? $"Bot {playerState.PlayerID}"
                    : $"Player {playerState.PlayerID}";

            EnsureTracked(controller.Handle);
            var tracked = _byController[controller.Handle];
            tracked.DisplayName = displayName;
            tracked.PlayerId = playerState.PlayerID;
            tracked.IsBot = playerState.bIsABot;
            tracked.PlatformType = playerState.PlatformType;
            tracked.FactionId = playerState.FactionID;
            tracked.LastSeen = Environment.TickCount64;
        }
        catch (UnrealApiException exception) when (
            exception.Result is NativeUnrealResult.StaleHandle or NativeUnrealResult.NotFound)
        {
            _byController.Remove(controller.Handle);
        }
        catch (Exception exception)
        {
            warning($"Could not observe a connected player: {exception.Message}");
        }
    }

    private bool EnsureTracked(UnrealObjectHandle controllerHandle)
    {
        if (_byController.ContainsKey(controllerHandle)) return false;
        _byController.Add(controllerHandle, new TrackedPlayer(
            Guid.NewGuid().ToString("N"), controllerHandle)
        {
            DisplayName = "Connecting player",
            LastSeen = Environment.TickCount64
        });
        return true;
    }

    private void ProcessPendingKicks(
        UnrealApi unreal, Action<string> info, Action<string> warning)
    {
        while (_pendingKicks.TryDequeue(out var request))
        {
            var tracked = _byController.Values.FirstOrDefault(player =>
                player.Token.Equals(request.PlayerToken, StringComparison.Ordinal));
            if (tracked is null) continue;
            var reference = new UnrealObjectReference(tracked.ControllerHandle);
            var controller = unreal.FromReference<DeceiveIncPlayerController>(reference);
            try
            {
                // ClientMessage is a standard reliable client RPC using FString.
                // Give the unmodified game a short opportunity to render the
                // administrator message before returning it to the main menu.
                controller.ClientMessage(request.Reason, UnrealName.None, 4f);
            }
            catch (Exception exception)
            {
                // Message delivery is best effort. A UI incompatibility must not
                // prevent the actual administration action.
                warning($"Could not show the kick message to {tracked.DisplayName}: {exception.Message}");
            }

            _kickedControllers.Add(tracked.ControllerHandle);
            _byController.Remove(tracked.ControllerHandle);
            _scheduledKicks.Add(new ScheduledKick(
                tracked, request.Reason, Environment.TickCount64 + 1_500));
            PublishSnapshot();
            info($"Sent kick notice to {tracked.DisplayName} ({tracked.PlayerId}); " +
                 "return to menu scheduled in 1.5 seconds.");
        }

        var now = Environment.TickCount64;
        for (var index = _scheduledKicks.Count - 1; index >= 0; index--)
        {
            var scheduled = _scheduledKicks[index];
            if (now < scheduled.ExecuteAt) continue;
            _scheduledKicks.RemoveAt(index);
            try
            {
                var reference = new UnrealObjectReference(scheduled.Player.ControllerHandle);
                var controller = unreal.FromReference<DeceiveIncPlayerController>(reference);
                // The FString overload only performs the travel. Its FText
                // counterpart feeds Unreal's disconnect/error presentation,
                // which gives the client UI the administrator-provided reason.
                controller.ClientReturnToMainMenuWithTextReason(scheduled.Reason);
                info($"Kicked player {scheduled.Player.DisplayName} " +
                     $"({scheduled.Player.PlayerId}): {scheduled.Reason}");
            }
            catch (Exception exception)
            {
                // Restore management if the final RPC could not be sent. A failed
                // action must not leave a connected player hidden indefinitely.
                _kickedControllers.Remove(scheduled.Player.ControllerHandle);
                _byController[scheduled.Player.ControllerHandle] = scheduled.Player;
                PublishSnapshot();
                warning($"Could not kick {scheduled.Player.DisplayName}: {exception.Message}");
            }
        }
    }

    private void PublishSnapshot() => Volatile.Write(ref _snapshot,
        _byController.Values
            .OrderBy(player => player.IsBot)
            .ThenBy(player => player.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(player => new ServerPlayerEnvelope(
                player.Token,
                player.DisplayName,
                player.PlayerId,
                player.IsBot,
                player.PlatformType,
                player.FactionId))
            .ToArray());

    private static string ValidateText(
        string? value, string name, int maximumLength, bool allowEmpty)
    {
        value = value?.Trim() ?? "";
        if (!allowEmpty && value.Length == 0)
            throw new InvalidOperationException($"{name} is required.");
        if (value.Length > maximumLength)
            throw new InvalidOperationException(
                $"{name} cannot exceed {maximumLength} characters.");
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException($"{name} contains an unsupported character.");
        return value;
    }

    private sealed class TrackedPlayer(string token, UnrealObjectHandle controllerHandle)
    {
        public string Token { get; } = token;
        public UnrealObjectHandle ControllerHandle { get; } = controllerHandle;
        public string DisplayName { get; set; } = "";
        public int PlayerId { get; set; }
        public bool IsBot { get; set; }
        public byte PlatformType { get; set; }
        public byte FactionId { get; set; }
        public long LastSeen { get; set; }
    }

    private sealed record KickRequest(string PlayerToken, string Reason);
    private sealed record ScheduledKick(TrackedPlayer Player, string Reason, long ExecuteAt);
}
