using Briefcase.ModApi;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Handles administration messages after the unified endpoint has framed,
/// classified, and authorized the connection.
/// </summary>
internal sealed class ServerAdminServer
{
    private readonly CommunityBalanceStore _store;
    private readonly ModManagementApi _mods;
    private readonly ServerConfigurationService _configuration;
    private readonly ServerPlayerRegistry _players;
    private readonly Func<ModHandshakeClientSnapshot[]> _handshakeClients;
    private readonly Action<string> _info;

    public ServerAdminServer(
        CommunityBalanceStore store,
        ModManagementApi mods,
        ServerConfigurationService configuration,
        ServerPlayerRegistry players,
        Func<ModHandshakeClientSnapshot[]> handshakeClients,
        Action<string> info)
    {
        _store = store;
        _mods = mods;
        _configuration = configuration;
        _players = players;
        _handshakeClients = handshakeClients;
        _info = info;
    }

    public ServerAdminResponse Handle(ServerAdminRequest request, bool authenticated)
    {
        if (!authenticated)
            return AuthenticationFailure(request);
        if (!string.Equals(
                request.Channel, BriefcaseChannels.Administration, StringComparison.Ordinal))
            return Failure(request, "The request is not an administration message.");
        if (request.ProtocolVersion != ServerAdminProtocol.Version)
            return Failure(request, $"Unsupported protocol version {request.ProtocolVersion}.");
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return Failure(request, "RequestId is required.");

        try
        {
            return request.Operation switch
            {
                ServerAdminOperations.Status => Success(
                    request, _store.Snapshot() is null
                        ? "Waiting for the game to send its first community profile."
                        : "A community profile is available."),
                ServerAdminOperations.GetProfile => GetProfile(request),
                ServerAdminOperations.StageProfile => Stage(request),
                ServerAdminOperations.ClearStagedProfile => Clear(request),
                ServerAdminOperations.GetMods => Success(request, "Server mods returned."),
                ServerAdminOperations.RefreshMods => RefreshMods(request),
                ServerAdminOperations.SetModEnabled => SetModEnabled(request),
                ServerAdminOperations.LoadMod => RunModAction(
                    request, "loaded", _mods.Load),
                ServerAdminOperations.ReloadMod => RunModAction(
                    request, "reloaded", _mods.Reload),
                ServerAdminOperations.UnloadMod => RunModAction(
                    request, "unloaded", _mods.Unload),
                ServerAdminOperations.SetModConfiguration => SetModConfiguration(request),
                ServerAdminOperations.GetServerConfiguration => Success(
                    request, "Server configuration returned."),
                ServerAdminOperations.UpdateServerConfiguration =>
                    UpdateServerConfiguration(request),
                ServerAdminOperations.RestartServer => RestartServer(request),
                ServerAdminOperations.GetPlayers => Success(
                    request, "Connected players returned."),
                ServerAdminOperations.KickPlayer => KickPlayer(request),
                ServerAdminOperations.GetModHandshakes => Success(
                    request, "Player mod handshakes returned."),
                _ => Failure(request, $"Unknown operation: {request.Operation}")
            };
        }
        catch (Exception exception)
        {
            return Failure(request, exception.Message);
        }
    }

    private ServerAdminResponse GetProfile(ServerAdminRequest request)
    {
        var profile = _store.Snapshot();
        return Success(
            request,
            profile is null ? "No profile is available yet." : "Profile returned.",
            profile);
    }

    private ServerAdminResponse Stage(ServerAdminRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileJson))
            return Failure(request, "ProfileJson is required for stage-profile.");
        var profile = _store.Stage(
            request.ProfileJson, request.GameProfileHash, request.ExpectedRevision);
        _info(
            $"Community balance profile saved to {_store.ProfilePath} " +
            $"({profile.UncompressedBytes:N0} bytes). Restart required.");
        return Success(
            request,
            "Profile saved. Restart the server to apply it.",
            profile);
    }

    private ServerAdminResponse Clear(ServerAdminRequest request)
    {
        _store.ClearStaged(request.ExpectedRevision);
        _info("The in-memory persisted profile snapshot was cleared.");
        return Success(request, "Persisted profile snapshot cleared.");
    }

    private ServerAdminResponse RefreshMods(ServerAdminRequest request)
    {
        _mods.Refresh();
        return Success(request, "Server mod directory refreshed.");
    }

    private ServerAdminResponse SetModEnabled(ServerAdminRequest request)
    {
        var fileName = ValidateModFileName(request);
        if (request.ModEnabled is null)
            return Failure(request, "ModEnabled is required for set-mod-enabled.");
        _mods.SetEnabled(fileName, request.ModEnabled.Value);
        var state = request.ModEnabled.Value ? "enabled" : "disabled";
        _info($"Server mod {fileName} was {state} by Server Admin Control.");
        return Success(request, $"{fileName} was {state}.");
    }

    private ServerAdminResponse RunModAction(
        ServerAdminRequest request,
        string pastTense,
        Action<string> action)
    {
        var fileName = ValidateModFileName(request);
        action(fileName);
        _info($"Server mod {fileName} was {pastTense} by Server Admin Control.");
        return Success(request, $"{fileName} was {pastTense}.");
    }

    private ServerAdminResponse SetModConfiguration(ServerAdminRequest request)
    {
        var fileName = ValidateModFileName(request);
        if (string.IsNullOrWhiteSpace(request.ModSettingSection) ||
            string.IsNullOrWhiteSpace(request.ModSettingKey) ||
            request.ModSettingValue is null)
            return Failure(request,
                "ModSettingSection, ModSettingKey and ModSettingValue are required.");
        _mods.SetConfiguration(
            fileName,
            request.ModSettingSection,
            request.ModSettingKey,
            request.ModSettingValue.Value);
        _info($"Server mod setting {fileName}/" +
              $"{request.ModSettingSection}/{request.ModSettingKey} was updated remotely.");
        return Success(request, "Server mod setting saved.");
    }

    private ServerAdminResponse UpdateServerConfiguration(ServerAdminRequest request)
    {
        if (request.ServerConfiguration is null)
            return Failure(
                request, "ServerConfiguration is required for update-server-configuration.");
        _configuration.Update(request.ServerConfiguration);
        _info("The Deceive Inc. server configuration was updated remotely.");
        return Success(request, "Server configuration saved. Restart to apply all changes.");
    }

    private ServerAdminResponse RestartServer(ServerAdminRequest request)
    {
        _configuration.ScheduleRestart(_info);
        return Success(
            request,
            "Server restart scheduled. The administration connection will close briefly.",
            restartScheduled: true);
    }

    private ServerAdminResponse KickPlayer(ServerAdminRequest request)
    {
        _players.QueueKick(request.PlayerToken, request.KickReason);
        return Success(request,
            "Kick queued on the server game thread. Refresh the player list shortly.");
    }

    private string ValidateModFileName(ServerAdminRequest request)
    {
        var fileName = request.ModFileName?.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("ModFileName is required.");
        if (!_mods.GetInstalled().Any(mod =>
                mod.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"The server mod '{fileName}' is not installed.");
        return fileName;
    }

    private ServerModEnvelope[] SnapshotMods() => _mods.GetInstalled()
        .Select(mod => new ServerModEnvelope(
                mod.FileName,
                mod.DisplayName,
                mod.Version,
                mod.Description,
                mod.Enabled,
                mod.Loaded,
                mod.LastError,
                CanManage: true,
                ManagementNote: null)
            {
                Dependencies = mod.Dependencies,
                ActiveDependents = mod.ActiveDependents,
                Configuration = _mods.GetConfiguration(mod.FileName)
                    .Select(entry => new ServerModConfigurationEntry(
                        entry.Section, entry.Key, entry.Description, entry.ValueType,
                        entry.Value, entry.Minimum, entry.Maximum, entry.Choices))
                    .ToArray()
            })
        .ToArray();

    private ServerAdminResponse Success(
        ServerAdminRequest request,
        string status,
        BalanceProfileEnvelope? profile = null,
        bool restartScheduled = false) =>
        new(ServerAdminProtocol.Version, request.RequestId, true, status, _store.Revision,
            Profile: profile,
            Mods: SnapshotMods(),
            ServerConfiguration: SnapshotConfiguration(),
            Players: _players.Snapshot(),
            RestartScheduled: restartScheduled,
            HandshakeClients: _handshakeClients());

    private ServerAdminResponse Failure(ServerAdminRequest request, string error) =>
        new(ServerAdminProtocol.Version, request.RequestId ?? "", false, "Request rejected.",
            _store.Revision, error, Mods: SnapshotMods(),
            ServerConfiguration: SnapshotConfiguration(),
            Players: _players.Snapshot(),
            HandshakeClients: _handshakeClients());

    // Never attach privileged snapshots to an unauthenticated response. The
    // ordinary Failure helper is used only after a valid proof was accepted.
    private ServerAdminResponse AuthenticationFailure(ServerAdminRequest request) =>
        new(ServerAdminProtocol.Version, request.RequestId ?? "", false,
            "Request rejected.", _store.Revision,
            "Administration authentication failed.");

    private ServerConfigurationEnvelope SnapshotConfiguration() =>
        _configuration.Snapshot() with { AdminPassword = "" };
}
