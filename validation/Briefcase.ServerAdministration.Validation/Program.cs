using System.Security.Cryptography;
using ServerAdminControl.Server;
using ServerAdminControl.Protocol;

var root = Path.Combine(Path.GetTempPath(), $"Briefcase.ServerAdmin.{Guid.NewGuid():N}");
try
{
    var executable = Path.Combine(root, "DeceiveIncServer-Win64-Shipping.exe");
    var configuration = Path.Combine(root, "TripwireServer.ini");
    var framework = Path.Combine(root, "Briefcase");
    Directory.CreateDirectory(root);
    File.WriteAllText(configuration, """
        ; preserved comment
        [/Script/DeceiveInc.TripwireServerSettings]
        ServerName=Before
        !MapRotation=ClearArray
        +MapRotation=DI_DS
        +MapRotation=DI_SE
        GamePort=50000
        QueryPort=50001
        UnknownFutureSetting=Preserved

        [Future.Section]
        Value=Preserved
        """);

    var service = new ServerConfigurationService(executable, configuration, framework);
    var initial = service.Snapshot();
    Assert(initial.ServerName == "Before", "Initial server name was not parsed.");
    Assert(initial.GamePort == 50000, "Initial game port was not parsed.");
    Assert(initial.MapRotation.SequenceEqual(["DI_DS", "DI_SE"]),
        "The Unreal map rotation array was not parsed.");

    var updated = service.Update(initial with
    {
        ServerName = "After",
        Region = "eu",
        GameMode = "Solo",
        MapRotation = ["DI_Hardsell", "DI_FSN"],
        Password = "test password",
        AdminPassword = "admin password",
        Crossplay = false,
        EnableUpnp = false,
        AutoShutdownEmptyMinutes = 12.5f,
        FillWithBots = true,
        BotsDifficulty = "Difficult",
        BotsAmount = 7,
        MaxPlayers = 8,
        CivilianHeatPercent = 20,
        StaffHeatPercent = 21,
        GuardHeatPercent = 22,
        TechnicianHeatPercent = 23,
        VipHeatPercent = 24,
        ScoldHeatPerSecond = 1.75f,
        SpyHitHeatDelaySeconds = 2.25f,
        PassiveHeatGainDelaySeconds = 3.5f,
        AggroAfterCoverHeatDelaySeconds = 4.5f,
        HeatDecayDelaySeconds = 1.25f,
        HeatDecayRate = 1.5f
    });
    Assert(updated.ServerName == "After", "Updated server name was not persisted.");
    Assert(updated.AdminPassword.Length == 0,
        "The unencrypted remote configuration path accepted an AdminPassword rotation.");
    var text = File.ReadAllText(configuration);
    Assert(text.Contains("UnknownFutureSetting=Preserved"),
        "An unknown game setting was removed.");
    Assert(text.Contains("[Future.Section]"), "An unknown INI section was removed.");
    Assert(File.Exists(configuration + ".briefcase.bak"), "The INI backup was not created.");
    Assert(text.Contains("!MapRotation=ClearArray"),
        "The saved rotation does not clear inherited Unreal array values.");
    Assert(text.Contains("+MapRotation=DI_Hardsell") &&
           text.Contains("+MapRotation=DI_FSN"),
        "The saved map rotation is incomplete.");
    Assert(text.Contains("bCrossplay=False") && text.Contains("bEnableUPnP=False"),
        "Crossplay or UPnP was not saved.");
    Assert(text.Contains("HeatDelayToDecay=1.25") && text.Contains("HeatDecayRate=1.5"),
        "Heat settings were not written with invariant decimal formatting.");

    try
    {
        service.Update(updated with { ServerName = "Invalid\nName" });
        throw new InvalidOperationException("A line break was accepted in an INI value.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("line break", StringComparison.OrdinalIgnoreCase)) { }

    AssertRejected(
        () => service.Update(updated with { MaxPlayers = 9 }),
        "Maximum players for Solo");
    AssertRejected(
        () => service.Update(updated with { GameMode = "Duo", MaxPlayers = 11 }),
        "Maximum players for Duo");
    AssertRejected(
        () => service.Update(updated with { GameMode = "Trio", MaxPlayers = 13 }),
        "Maximum players for Trio");
    AssertRejected(
        () => service.Update(updated with { BotsAmount = 9 }),
        "Bot amount");
    AssertRejected(
        () => service.Update(updated with { HeatDecayRate = float.NaN }),
        "Heat decay rate");
    AssertRejected(
        () => service.Update(updated with { MapRotation = [] }),
        "at least one map");

    var restart = ServerConfigurationService.CreateRestartScript(
        1234, root, executable, 50123);
    Assert(restart.Contains("PID eq 1234"), "Restart helper does not wait for the old PID.");
    Assert(restart.Contains("-Port=50123"), "Restart helper does not use the saved game port.");
    Assert(restart.Contains(executable), "Restart helper does not use the server executable.");

    var handshake = new ModHandshakeClientHello(
        ModHandshakeProtocol.Version,
        "request-1",
        "client-1",
        "0.7.0",
        new ModHandshakeBuild(0x12345678, 0x01000000),
        [new ModHandshakeMod(
            "sample.mod", "Sample.dll", "Sample", "1.2.3",
            new string('A', 64), true, true, [], "Client")]);
    using var handshakeFrame = new MemoryStream();
    ModHandshakeProtocol.WriteAsync(
        handshakeFrame, handshake, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    Assert(handshakeFrame.Length > sizeof(int), "The handshake frame has no payload.");
    handshakeFrame.Position = 0;
    var decoded = ModHandshakeProtocol.ReadAsync<ModHandshakeClientHello>(
            handshakeFrame, CancellationToken.None)
        .AsTask().GetAwaiter().GetResult();
    Assert(decoded is not null &&
           decoded.Channel == BriefcaseChannels.Handshake &&
           decoded.ClientInstanceId == "client-1" &&
           decoded.Mods.Count == 1 && decoded.Mods[0].Id == "sample.mod",
        "The framed handshake manifest did not round-trip.");

    var adminRequest = new ServerAdminRequest(
        ServerAdminProtocol.Version, "request-2", ServerAdminOperations.Status);
    Assert(adminRequest.Channel == BriefcaseChannels.Administration,
        "Administration requests must select the administration channel.");

    using var authenticator = new ServerAdminAuthenticator("correct horse battery staple");
    var challengeResponse = authenticator.CreateChallenge(
        new ServerAdminChallengeRequest(
            ServerAdminProtocol.Version,
            adminRequest.RequestId,
            adminRequest.Operation),
        out var challenge);
    Assert(challengeResponse.Success && challenge is not null,
        "The configured server did not issue an authentication challenge.");
    var clientKey = ServerAdminAuthentication.DeriveKey(
        "correct horse battery staple",
        challengeResponse.Salt!,
        challengeResponse.Iterations);
    var proof = ServerAdminAuthentication.CreateProof(
        clientKey,
        adminRequest.ProtocolVersion,
        adminRequest.RequestId,
        adminRequest.Operation,
        challengeResponse.ChallengeId!,
        challengeResponse.Nonce!);
    var authenticatedRequest = adminRequest with
    {
        AuthenticationChallengeId = challengeResponse.ChallengeId,
        AuthenticationProof = proof
    };
    Assert(authenticator.Authenticate(authenticatedRequest, challenge!),
        "A valid administration proof was rejected.");

    var wrongKey = ServerAdminAuthentication.DeriveKey(
        "wrong password", challengeResponse.Salt!, challengeResponse.Iterations);
    var wrongProof = ServerAdminAuthentication.CreateProof(
        wrongKey,
        adminRequest.ProtocolVersion,
        adminRequest.RequestId,
        adminRequest.Operation,
        challengeResponse.ChallengeId!,
        challengeResponse.Nonce!);
    Assert(!authenticator.Authenticate(
            authenticatedRequest with { AuthenticationProof = wrongProof }, challenge!),
        "A proof made with the wrong password was accepted.");
    Assert(!authenticator.Authenticate(
            authenticatedRequest with { Operation = ServerAdminOperations.RestartServer },
            challenge!),
        "A proof was replayed for a different administration operation.");
    CryptographicOperations.ZeroMemory(clientKey);
    CryptographicOperations.ZeroMemory(wrongKey);

    using var disabledAuthenticator = new ServerAdminAuthenticator("");
    var disabledResponse = disabledAuthenticator.CreateChallenge(
        new ServerAdminChallengeRequest(
            ServerAdminProtocol.Version, "request-3", ServerAdminOperations.Status),
        out var disabledChallenge);
    Assert(!disabledResponse.Success && disabledChallenge is null,
        "An empty server AdminPassword enabled administration.");

    Console.WriteLine(
        "[OK] Typed INI, challenge-response authentication, and framed handshake validation.");
    return 0;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertRejected(Action action, string expectedMessage)
{
    try
    {
        action();
        throw new InvalidOperationException(
            $"Expected validation failure containing '{expectedMessage}'.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase)) { }
}
