using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.IO.Pipes;
using System.Diagnostics;
using Briefcase.ServerApp;
using Briefcase.ServerApp.Launcher;

if (args is ["--verify-catalog", var catalogPath])
{
    var mods = ModMarketplace.ParseCatalog(File.ReadAllBytes(catalogPath),
        "https://raw.githubusercontent.com/EnoPM/Briefcase/main/marketplace/catalog.json");
    Check(mods.Count == 3 && mods.Select(mod => mod.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
        "published mod catalog contains three distinct installable entries");
    Console.WriteLine("ServerApp mod catalog passed.");
    return;
}

if (args is ["--verify-update-archive", var archivePath, var installationPath])
{
    var candidate = AppUpdateStore.Prepare(installationPath, archivePath, "0.1.0");
    Check(File.Exists(candidate.Executable) && AppUpdateStore.Current(installationPath) is null,
        "release archive can be installed without activating an untested app");
    AppUpdateStore.Activate(installationPath, candidate);
    Check(AppUpdateStore.Current(installationPath)?.Version == "0.1.0",
        "installed release archive can be activated");
    Console.WriteLine("ServerApp release archive passed.");
    return;
}

if (args is ["--live-pipe-pid", var pidText] && int.TryParse(pidText, out var livePid) && livePid > 0)
{
    for (var attempt = 1; attempt <= 2; attempt++)
    {
        using var client = new ServerLiveClient(livePid, new WindowsNamedPipeTransport());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (client.Latest is null && !timeout.IsCancellationRequested)
            await Task.Delay(100);
        Check(client.Latest is not null && client.Status == LiveChannelStatus.Connected,
            $"live bridge connection {attempt}");
    }
    Console.WriteLine("ServerApp live reconnect passed.");
    return;
}

if (args is ["--verify-shared-install", var serverExecutable])
{
    var testBase = Environment.GetEnvironmentVariable("BRIEFCASE_TEST_DATA_ROOT") ?? Path.GetTempPath();
    var testRoot = Path.Combine(testBase, "Briefcase Shared Instance Probe", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    Process? firstProcess = null, secondProcess = null;
    ServerLocation? firstLocation = null, secondLocation = null;
    try
    {
        var testRegistry = new ServerRegistry(Path.Combine(testRoot, "settings.json"));
        var firstRow = testRegistry.Add(serverExecutable, serverName: "Briefcase Probe A");
        var secondRow = testRegistry.Add(serverExecutable, serverName: "Briefcase Probe B");
        Check(ServerLocation.TryCreate(firstRow, out firstLocation, out _) &&
              ServerLocation.TryCreate(secondRow, out secondLocation, out _), "probe instances resolve");
        foreach (var (instance, name, gamePort, queryPort) in new[]
                 {
                     (firstLocation!, "Briefcase Probe A", 48670, 48671),
                     (secondLocation!, "Briefcase Probe B", 48672, 48673)
                 })
            File.WriteAllText(instance.ConfigFile,
                $"[{ServerIniEditor.Section}]\nServerName={name}\nGameMode=Solo\nMaxPlayers=2\n" +
                $"GamePort={gamePort}\nQueryPort={queryPort}\nbIsPublic=False\nbSandboxMode=True\n" +
                "bEnableUPnP=False\nAutoShutdownEmptyMinutes=1\n");
        firstProcess = ServerProcess.Start(firstLocation!);
        secondProcess = ServerProcess.Start(secondLocation!);
        using var foundA = ServerProcess.Find(firstLocation!);
        using var foundB = ServerProcess.Find(secondLocation!);
        Check(foundA?.Id == firstProcess.Id && foundB?.Id == secondProcess.Id && foundA.Id != foundB.Id,
            "two processes from one executable are matched to separate instance identities");
        using var liveA = new ServerLiveClient(firstProcess.Id, new WindowsNamedPipeTransport(),
            expectedInstanceId: firstLocation!.InstanceId);
        using var liveB = new ServerLiveClient(secondProcess.Id, new WindowsNamedPipeTransport(),
            expectedInstanceId: secondLocation!.InstanceId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!timeout.IsCancellationRequested && (liveA.Latest is null || liveB.Latest is null))
            await Task.Delay(250);
        Check(liveA.Latest is not null && liveB.Latest is not null,
            "both instance bridges report verified live data");
        Console.WriteLine("Shared-install instance launch and reconnect passed.");
    }
    finally
    {
        if (firstLocation is not null) await ServerProcess.StopAsync(firstLocation, CancellationToken.None);
        if (secondLocation is not null) await ServerProcess.StopAsync(secondLocation, CancellationToken.None);
        firstProcess?.Dispose();
        secondProcess?.Dispose();
    }
    return;
}

var temp = Path.Combine(Path.GetTempPath(), "Briefcase.ServerApp.Tests", Guid.NewGuid().ToString("N"));
var win64 = Path.Combine(temp, "DeceiveInc", "Binaries", "Win64");
Directory.CreateDirectory(win64);
try
{
    Check(!ServerLocation.TryCreate(win64, out _, out _), "missing Shipping rejected");
    File.WriteAllBytes(Path.Combine(win64, "DeceiveIncServer-Win64-Shipping.exe"), []);
    Check(ServerLocation.TryCreate(win64, out var location, out _), "valid Win64 accepted");
    Check(location is not null, "location available");
    var localBuildMarker = Path.Combine(win64, "Briefcase", "Core", "Updater", "build.json");
    Directory.CreateDirectory(Path.GetDirectoryName(localBuildMarker)!);
    File.WriteAllText(localBuildMarker, "{\"frameworkVersion\":\"0.8.5-dev.local\"}");
    var localBuildCheck = await new BriefcaseInstallService().CheckVersionAsync(location!, CancellationToken.None);
    Check(localBuildCheck.Installed == "0.8.5-dev.local" && !localBuildCheck.UpdateAvailable,
        "local development runtime does not request a GitHub update");
    var developmentUpdateBlocked = false;
    try
    {
        await new BriefcaseInstallService().InstallOrUpdateAsync(location!, new Progress<string>(), CancellationToken.None);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("development build", StringComparison.OrdinalIgnoreCase))
    {
        developmentUpdateBlocked = true;
    }
    Check(developmentUpdateBlocked, "GitHub release cannot replace a local development runtime");
    File.Delete(localBuildMarker);
    var rootExecutable = Path.Combine(temp, "DeceiveIncServer.exe");
    File.WriteAllBytes(rootExecutable, []);
    Check(ServerLocation.TryCreateFromExecutable(rootExecutable, out var rootLocation, out _) &&
          rootLocation?.Win64 == win64, "installation launcher resolves to the same Win64 server");
    Check(ServerLocation.TryCreateFromExecutable(location!.Shipping, out var shippingLocation, out _) &&
          shippingLocation?.Win64 == win64, "Shipping executable remains selectable");
    Check(!ServerLocation.TryCreateFromExecutable(Path.Combine(temp, "EasyAntiCheat_EOS_Setup.exe"), out _, out _),
        "EAC setup executable is not a dedicated server");
    var oldData = Path.Combine(temp, "briefcase-data-old");
    var newData = Path.Combine(temp, "briefcase-data-new");
    var dataLocator = Path.Combine(temp, "data-location.json");
    var bundledWin64 = Path.Combine(oldData, "servers", "Test Server", "DeceiveInc", "Binaries", "Win64");
    Directory.CreateDirectory(bundledWin64);
    File.WriteAllBytes(Path.Combine(bundledWin64, "DeceiveIncServer-Win64-Shipping.exe"), []);
    var oldRegistry = new ServerRegistry(Path.Combine(oldData, "settings.json"));
    oldRegistry.Add(Path.Combine(bundledWin64, "DeceiveIncServer-Win64-Shipping.exe"), managed: true);
    var existingServer = oldRegistry.Add(rootExecutable);
    var secondInstance = oldRegistry.Add(location.Shipping, serverName: "Second Instance");
    var oldVersionRoot = AppDataStore.VersionInstallRoot(oldData, "smoketest");
    var oldVersionWin64 = Path.Combine(oldVersionRoot, "DeceiveInc", "Binaries", "Win64");
    Directory.CreateDirectory(oldVersionWin64);
    File.WriteAllBytes(Path.Combine(oldVersionWin64, "DeceiveIncServer-Win64-Shipping.exe"), []);
    oldRegistry.AddInstallation("smoketest", oldVersionRoot);
    Check(existingServer.Id != secondInstance.Id && existingServer.Win64 == secondInstance.Win64 &&
          existingServer.InstanceRoot != secondInstance.InstanceRoot && oldRegistry.Servers.Count == 3 &&
          ServerIniEditor.ReadConfiguredServerName(Path.Combine(secondInstance.InstanceRoot, "TripwireServer.ini")) ==
          "Second Instance", "one executable supports separate registered server instances");
    Directory.CreateDirectory(Path.Combine(oldData, "Catalogs"));
    File.WriteAllText(Path.Combine(oldData, "Catalogs", "cached.json"), "{}");
    Check(!AppDataStore.Move(oldData, newData, dataLocator) &&
          AppDataStore.ResolveRoot(oldData, dataLocator) == newData &&
          !Directory.Exists(oldData) && File.Exists(Path.Combine(newData, "Catalogs", "cached.json")),
        "data directory move activates the new location and preserves app data");
    var movedRegistry = new ServerRegistry(Path.Combine(newData, "settings.json"));
    Check(movedRegistry.Servers.Count == 3 &&
          movedRegistry.Installations.Single().InstallRoot == AppDataStore.VersionInstallRoot(newData, "smoketest") &&
          movedRegistry.Servers.Any(row => row.Win64 == Path.Combine(newData, "servers", "Test Server",
              "DeceiveInc", "Binaries", "Win64")) &&
          movedRegistry.Servers.Count(row => row.Win64 == win64) == 2 &&
          movedRegistry.Servers.All(row => AppDataStore.IsInside(row.InstanceRoot, newData)),
        "data migration moves instance data and managed game files but keeps external executable paths");
    var movedSibling = movedRegistry.Servers.Single(row => row.Id == secondInstance.Id);
    Check(ServerLocation.TryCreate(movedSibling, out var isolated, out _) && isolated!.IsInstance &&
          isolated.ConfigFile == Path.Combine(movedSibling.InstanceRoot, "TripwireServer.ini") &&
          isolated.ModsRoot == Path.Combine(movedSibling.InstanceRoot, "ue4ss", "Mods"),
        "instance paths remain separate after moving application data");
    File.WriteAllText(isolated!.BalanceProfileFile,
        "{\"format\":\"DeceiveCommunityBalanceProfile\",\"schemaVersion\":1,\"overrides\":[]}");
    var isolatedStart = ServerProcess.CreateStartInfo(isolated);
    Check(isolatedStart.ArgumentList.Contains("-TripwireServerIni=" + isolated.ConfigFile) &&
          isolatedStart.ArgumentList.Contains("-CommunityBalanceProfile=" + isolated.BalanceProfileFile),
        "the game receives both instance-specific configuration paths");
    var occupiedData = Path.Combine(temp, "occupied-data");
    Directory.CreateDirectory(occupiedData);
    File.WriteAllText(Path.Combine(occupiedData, "keep.txt"), "keep");
    try { AppDataStore.Move(newData, occupiedData, dataLocator); throw new Exception("Occupied destination accepted."); }
    catch (InvalidDataException) { }
    Check(AppDataStore.ResolveRoot(oldData, dataLocator) == newData &&
          File.Exists(Path.Combine(occupiedData, "keep.txt")),
        "occupied destination does not replace current data");
    Check(AppDataStore.ServerInstallRoot(newData, "My Server") ==
          Path.Combine(newData, "game", "dedicated"), "new instances reuse the shared game installation");
    foreach (var invalidName in new[] { "../escape", "CON", "name:", " " })
    {
        try { AppDataStore.ServerInstallRoot(newData, invalidName); throw new Exception("Invalid name accepted."); }
        catch (InvalidDataException) { }
    }
    var namedConfig = Path.Combine(temp, "named-server.ini");
    File.WriteAllText(namedConfig, "[" + ServerIniEditor.Section + "]\nGamePort=51000\nServerName=Old\n");
    ServerIniEditor.SaveServerName(namedConfig, "New Server");
    Check(ServerIniEditor.ReadConfiguredServerName(namedConfig) == "New Server" &&
          File.ReadAllText(namedConfig).Contains("GamePort=51000"),
        "installation sets the server name without changing other INI settings");
    var info = ServerProcess.CreateStartInfo(location!);
    Check(info.FileName == location!.Shipping && info.WorkingDirectory == win64,
        "Shipping starts with Win64 as working directory");
    Check(info.ArgumentList.Contains("-unattended") && info.ArgumentList.Contains("-nullrhi") &&
        info.ArgumentList.Contains("-Port=50000") && info.ArgumentList.Contains("-QueryPort=50001"),
        "headless default command line");
    Check(new SteamCmdService.BuildCheck("100", "101", null).UpdateAvailable &&
          !new SteamCmdService.BuildCheck("101", "100", null).UpdateAvailable &&
          !new SteamCmdService.BuildCheck("100", null, "offline").UpdateAvailable,
        "server update action requires a newer public build, not an unknown or older one");
    var appInfo = """
        "depots" { "5007711" { "manifests" { "public" { "gid" "123" } } } }
        "branches" { "public" { "buildid" "25541576" "timeupdated" "1790390019" } }
        """;
    Check(SteamCmdService.PublicBuildIdFromAppInfo(appInfo) == "25541576" &&
          SteamCmdService.PublicBuildIdFromAppInfo("\"depots\" { \"public\" { \"gid\" \"123\" } }") is null,
        "Steam build check reads branches/public rather than a depot's public manifest");
    var betaCommand = SteamCmdService.AppUpdateArguments(Path.Combine(temp, "beta install"),
        "smoketest", "test-only-password");
    Check(betaCommand.Contains("-beta") && betaCommand.Contains("smoketest") &&
          betaCommand.Contains("-betapassword") && betaCommand.Contains("test-only-password") &&
          betaCommand.Contains("validate") &&
          !SteamCmdService.AppUpdateArguments(temp, "public", "ignored").Contains("-betapassword"),
        "SteamCMD selects a separate branch and optional beta password without changing public installs");
    Check(new BriefcaseInstallService.VersionCheck(null, "2.0.0").InstallNeeded &&
          new BriefcaseInstallService.VersionCheck("1.0.0", "2.0.0").UpdateAvailable &&
          !new BriefcaseInstallService.VersionCheck("2.0.0", "2.0.0").UpdateAvailable &&
          BriefcaseInstallService.IsDevelopmentBuild("0.8.5-dev.local") &&
          !new BriefcaseInstallService.VersionCheck("0.8.5-dev.local", "2.0.0").UpdateAvailable,
        "Briefcase action distinguishes missing, outdated, current and local development installations");
    var catalogUrl = "https://example.test/catalog.json";
    var catalogV2 = $$"""
        {"schemaVersion":2,"mods":[{"id":"player-cap","name":"Player Cap","description":"Adjust player limits",
        "version":"1.2.3","author":"EnoPM","downloadUrl":"https://example.test/player-cap.zip",
        "archiveSha256":"{{new string('a', 64)}}","logoUrl":"https://example.test/player-cap.png",
        "logoSha256":"{{new string('b', 64)}}","documentationUrl":"https://example.test/docs",
        "discordUrl":"https://discord.gg/example"}]}
        """;
    var parsedCatalog = ModMarketplace.ParseCatalog(System.Text.Encoding.UTF8.GetBytes(catalogV2), catalogUrl);
    Check(parsedCatalog.Count == 1 && parsedCatalog[0].Id == "player-cap" &&
          parsedCatalog[0].Author == "EnoPM" && parsedCatalog[0].LogoSha256 == new string('b', 64),
        "marketplace schema v2 keeps identity, author and verified asset metadata");
    try
    {
        ModMarketplace.ParseCatalog(System.Text.Encoding.UTF8.GetBytes(catalogV2.Replace("player-cap", "Player Cap")), catalogUrl);
        throw new Exception("Uppercase or spaced mod ID accepted.");
    }
    catch (InvalidDataException) { }

    Directory.CreateDirectory(Path.GetDirectoryName(location.ConfigFile)!);
    File.WriteAllText(location.ConfigFile, "GamePort=51000\nQueryPort=51001\n");
    info = ServerProcess.CreateStartInfo(location);
    Check(info.ArgumentList.Contains("-Port=51000") && info.ArgumentList.Contains("-QueryPort=51001"),
        "configured ports propagated");

    Directory.CreateDirectory(Path.GetDirectoryName(location.BalanceTemplateFile)!);
    File.WriteAllText(location.BalanceTemplateFile, """
        {"format":"DeceiveCommunityBalanceProfile","schemaVersion":1,"overrides":[
          {"table":"DT_Balancing_HitscanWeapons","row":"Ace_Weapon_Base","field":"MagSize","value":5,"allowedRange":"0 to 30"},
          {"table":"DT_Balancing_HitscanWeapons","row":"DEye_Weapon","field":"MagSize","value":10,"allowedRange":"0 to 20"},
          {"table":"DT_Gadgets_Balancing","row":"BouncingMat","field":"Cooldown","value":60,"allowedRange":"0 to 600"},
          {"table":"DT_Gadgets_Balancing","row":"Scrambler","field":"Cooldown","value":30,"allowedRange":"0 to 600"},
          {"table":"DT_Gadgets_Balancing","row":"UmbrellaShield","field":"Cooldown","value":30,"allowedRange":"0 to 600"},
          {"table":"DT_Chavez_ActivesBalancing","row":"Chavez_Active_Base","field":"OpenLockedDoors","value":true}
        ]}
        """);
    File.WriteAllText(location.BalanceProfileFile, """
        {"format":"DeceiveCommunityBalanceProfile","schemaVersion":1,"custom":"preserve","overrides":[
          {"table":"DT_Balancing_HitscanWeapons","row":"Ace_Weapon_Base","field":"MagSize","value":8}
        ]}
        """);
    var balance = BalanceProfileEditor.Load(location);
    Check(balance.HasTemplate && balance.Fields.Count == 6 &&
        balance.Fields.Single(field => field.Row == "Ace_Weapon_Base").Value == "8",
        "balance template merges with active server overrides");
    Check(BalanceGrouping.Classify(balance.Fields.Single(field => field.Row == "DEye_Weapon")).Path[..1]
            .SequenceEqual(["Gadgets"]) &&
        BalanceGrouping.Classify(balance.Fields.Single(field => field.Row == "Ace_Weapon_Base")).Path
            .SequenceEqual(["Characters", "Ace", "Weapons", "Weapon 1"]),
        "balance hierarchy keeps gadget weapons out of character weapons");
    Check(BalanceGrouping.Classify(balance.Fields.Single(field => field.Row == "Scrambler")).Path[^1] == "Scrambler" &&
        BalanceGrouping.Classify(balance.Fields.Single(field => field.Row == "UmbrellaShield")).Path[^1] == "Umbrella Shield",
        "gadget balance groups use their game names");
    Check(new PlayerGadget("Shieldbrella").CanonicalId == "UmbrellaShield" &&
          new PlayerGadget("RemoteTurret").CanonicalId == "AutoTurret" &&
          new PlayerGadget("SoundMachine").CanonicalId == "Soundbox",
        "server gadget asset IDs resolve to the UI gadget names");
    var treeRoot = new BalanceNode("Characters", "Characters", null, null);
    var treeAgent = new BalanceNode("Ace", "Ace", null, treeRoot);
    var treeWeapon = new BalanceNode("Weapons", "Weapons", null, treeAgent);
    treeRoot.Children.Add(treeAgent);
    treeAgent.Children.Add(treeWeapon);
    var treeField = balance.Fields.Single(field => field.Row == "Ace_Weapon_Base");
    treeWeapon.Fields.Add(treeField);
    var collapsed = BalanceTreeProjection.Build([treeRoot], "", out _);
    Check(collapsed.Select(row => row.Title).SequenceEqual(["Characters", "Ace"]),
        "balance tree starts at category level and keeps deeper folders collapsed");
    var found = BalanceTreeProjection.Build([treeRoot], "MagSize", out var foundCount);
    Check(foundCount == 1 && found.Select(row => row.Title).SequenceEqual(
              ["Characters", "Ace", "Weapons", treeField.Label]) && !treeAgent.IsExpanded,
        "balance search exposes the matching editable leaf without changing expansion state");
    found[^1].Value = "9";
    Check(treeField.Value == "9", "inline tree editor updates the balance profile field");
    treeField.Value = "8";
    foreach (var npcRow in new[] { "Guard_Weapon", "SuperGuard_Weapon", "UltraGuard_Weapon" })
    {
        var npcField = new BalanceField
        {
            Table = "DT_Balancing_HitscanWeapons", Row = npcRow, Field = "Damage", Label = "Damage",
            Description = "", AllowedRange = "", Kind = JsonValueKind.Number, Value = "10", OriginalValue = "10"
        };
        Check(BalanceGrouping.Classify(npcField).Path[..2].SequenceEqual(["Game", "NPCs"]),
            npcRow + " is grouped with NPCs");
    }
    Check(BalanceGameNames.Variant("Ace", "Weapons", "Base") is not null &&
          BalanceGameNames.Variant("Ace", "Passives", "Mod1") is not null &&
          BalanceGameNames.Variant("Ace", "Expertises", "Mod2") is not null &&
          BalanceGameNames.Variant("Hans", "Passives", "Base") is not null &&
          BalanceGameNames.Variant("Cavaliere", "Weapons", "Mod2") is not null,
        "localized game names cover weapons, passives and expertises, including naming exceptions");
    using (var englishFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Translations", "en.json"))))
    using (var frenchFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Translations", "fr.json"))))
    {
        var englishKeys = englishFile.RootElement.EnumerateObject().Select(item => item.Name).ToHashSet();
        var frenchKeys = frenchFile.RootElement.EnumerateObject().Select(item => item.Name).ToHashSet();
        Check(englishKeys.SetEquals(frenchKeys) && englishKeys.Count > 300,
            "shipped English and French translations expose the same UI and game-name keys");
    }
    AppTranslations.Select("fr", AppContext.BaseDirectory);
    Check(AppTranslations.AvailableLanguages().Any(language => language.Code == "fr" && language.Name == "Français") &&
          AppTranslations.AvailableLanguages().Any(language => language.Code == "en" && language.Name == "English"),
        "language choices use names declared by their translation files");
    Check(BalanceGameNames.Variant("Ace", "Weapons", "Base") == "Reine de carreau" &&
          AppTranslations.Get("ui.servers") == "Serveurs",
        "shipped French file translates game names and interface labels");
    Check(GameMapNames.Display("DI_Hardsell") == "Surenchère" &&
          GameMapNames.Display("LVL_Silverreef") == "Espadon" &&
          GameMapNames.Display("/Game/Maps/LVL_Diamondspire.LVL_Diamondspire") == "Icare" &&
          GameMapNames.Display("Fragrant Shore") == "Fragrance" &&
          GameMapNames.Display("DI_SE") == "Sonar" &&
          GameMapNames.Display("DI_FSN") == "Fragrance (Nuit)" &&
          GameMapNames.Display("DI_HSD") == "Surenchère (Matin)",
        "map rotation and live level names use the game's French map labels");
    AppTranslations.Select("en", AppContext.BaseDirectory);
    Check(GameMapNames.Display("DI_SR") == "Silver Reef" &&
          GameMapNames.Display("LVL_Hardsell") == "Hard Sell" &&
          GameMapNames.Display("Surenchère") == "Hard Sell",
        "map display names switch back to English");
    var translations = Path.Combine(temp, "Translations");
    Directory.CreateDirectory(translations);
    File.WriteAllText(Path.Combine(translations, "fr.json"), """
        {"language.name":"Français", "game.characters.Ace.Weapons.Base":"Reine de carreau", "game.maps.DI_Hardsell":"Surenchère", "ui.servers":"Serveurs"}
        """);
    AppTranslations.Select("fr", temp);
    Check(BalanceGameNames.Variant("Ace", "Weapons", "Base") == "Reine de carreau" &&
          BalanceGameNames.Variant("Cavaliere", "Weapons", "Base") == "Dragoons" &&
          AppTranslations.Get("ui.servers") == "Serveurs",
        "French file overrides game names and UI, with embedded English for missing keys");
    File.WriteAllText(Path.Combine(translations, "es.json"), """
        {"language.name":"Español", "ui.servers":"Servidores", "game.maps.DI_Hardsell":"Venta agresiva"}
        """);
    Check(AppTranslations.AvailableLanguages(temp).Any(language => language.Code == "es" && language.Name == "Español"),
        "adding a JSON translation exposes its language in the selector");
    AppTranslations.Select("es", temp);
    Check(AppTranslations.Get("ui.servers") == "Servidores" &&
          GameMapNames.Display("Venta agresiva") == "Venta agresiva" &&
          GameMapNames.Display("LVL_Hardsell") == "Venta agresiva" &&
          GameMapNames.Display("Surenchère") == "Venta agresiva",
        "added language translates known map IDs and recognizes map names from other languages");
    AppTranslations.Select("en", temp);
    Check(GameMapNames.Display("Venta agresiva") == "Hard Sell",
        "map names from added translation files resolve regardless of selected UI language");
    var installedRoot = Path.Combine(temp, "installed-app");
    var versionDirectory = Path.Combine(installedRoot, "App", "versions", "1.2.3");
    Directory.CreateDirectory(Path.Combine(versionDirectory, "Translations"));
    Directory.CreateDirectory(Path.Combine(installedRoot, "Translations"));
    File.WriteAllText(Path.Combine(versionDirectory, "Translations", "en.json"),
        "{\"language.name\":\"English\",\"ui.servers\":\"Bundled servers\"}");
    File.WriteAllText(Path.Combine(installedRoot, "Translations", "en.json"),
        "{\"language.name\":\"English\",\"ui.servers\":\"Shared servers\"}");
    File.WriteAllText(Path.Combine(installedRoot, "Translations", "es.json"),
        "{\"language.name\":\"Español\",\"ui.servers\":\"Servidores\"}");
    Check(AppTranslations.AvailableLanguages(versionDirectory).Any(language => language.Code == "es"),
        "custom language beside the launcher is available to a versioned application");
    AppTranslations.Select("en", versionDirectory);
    Check(AppTranslations.Get("ui.servers") == "Shared servers",
        "shared translations take priority over versioned bundled translations");
    AppTranslations.Select("es", versionDirectory);
    Check(AppTranslations.Get("ui.servers") == "Servidores",
        "shared custom translations survive application version changes");
    File.WriteAllText(Path.Combine(translations, "fr.json"), "{invalid");
    AppTranslations.Select("fr", temp);
    Check(BalanceGameNames.Variant("Ace", "Weapons", "Base") == "Queen of Diamond" &&
          AppTranslations.Get("ui.servers") == "Servers",
        "invalid editable file falls back to native English");
    AppTranslations.Select("en", temp);
    balance.Fields.Single(field => field.Row == "DEye_Weapon").Value = "15";
    balance.Save();
    using (var savedBalance = JsonDocument.Parse(File.ReadAllText(location.BalanceProfileFile)))
    {
        Check(savedBalance.RootElement.GetProperty("custom").GetString() == "preserve" &&
            savedBalance.RootElement.GetProperty("overrides").GetArrayLength() == 2,
            "balance save preserves unrelated profile data and keeps untouched defaults sparse");
    }
    var invalidBalance = BalanceProfileEditor.Load(location);
    invalidBalance.Fields.Single(field => field.Row == "DEye_Weapon").Value = "21";
    var beforeInvalid = File.ReadAllText(location.BalanceProfileFile);
    try { invalidBalance.Save(); throw new Exception("Out-of-range balance value accepted."); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(location.BalanceProfileFile) == beforeInvalid,
        "out-of-range values do not alter the balance profile");
    File.Delete(location.BalanceProfileFile);
    var newBalance = BalanceProfileEditor.Load(location);
    newBalance.Fields.Single(field => field.Row == "Chavez_Active_Base").BoolValue = false;
    newBalance.Save();
    using (var savedBalance = JsonDocument.Parse(File.ReadAllText(location.BalanceProfileFile)))
    {
        var overrides = savedBalance.RootElement.GetProperty("overrides");
        Check(overrides.GetArrayLength() == 1 &&
            overrides[0].GetProperty("value").ValueKind == JsonValueKind.False,
            "a new profile stores only changed defaults and preserves Boolean values");
    }

    var pending = ServerSnapshotReader.Read(location, 4242, null);
    Check(pending.State == "Starting" && pending.PlayerCount is null && !pending.HasPlayerRoster,
        "no file fallback invents a player count while the stream is unavailable");
    Check(ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.Disconnected,
              TimeSpan.FromSeconds(20)).State == "Connecting" &&
          ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.Disconnected,
              TimeSpan.FromMinutes(2)).State == "Connection unavailable",
        "an older server is connecting or unavailable, never stuck on starting");
    Check(ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.Disconnected,
              TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2)).State == "Connecting" &&
          ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.Disconnected,
              TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(46)).State == "Connection unavailable",
        "a reopened app gets a fresh connection window even when the server is old");
    Check(ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.Connected,
              TimeSpan.FromMinutes(2)).State == "Waiting for data" &&
          ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.InvalidData,
              TimeSpan.FromMinutes(2)).State == "Incompatible data" &&
          ServerSnapshotReader.Read(location, 4242, null, LiveChannelStatus.Stale,
              TimeSpan.FromMinutes(2)).State == "Data interrupted",
        "a connected channel distinguishes missing, rejected and stale telemetry");

    var live = new
    {
        schemaVersion = 2, type = "snapshot", processId = 4242, instanceId = secondInstance.Id,
        state = "In match", map = "DI_SR",
        players = new[] { new { name = "Alice", token = "p1", platform = "PC", networkId = "EOS-123" },
                          new { name = "Bob", token = "p2", platform = "Xbox", networkId = "" } }
    };
    var parsed = ServerSnapshotReader.ParseLive(JsonSerializer.Serialize(live), 4242);
    var snapshot = ServerSnapshotReader.Read(location, 4242, parsed);
    Check(snapshot.HasPlayerRoster && snapshot.PlayerCount == 2 && snapshot.Players[1].Name == "Bob" &&
        snapshot.Players[0].Identity == "PC · EOS-123" && snapshot.Players[1].Identity == "",
        "live protocol provides actual roster");
    Check(ServerSnapshotReader.ParseLive(JsonSerializer.Serialize(live), 4243) is null,
        "snapshots from another server PID rejected");
    Check(ServerSnapshotReader.ParseLive(JsonSerializer.Serialize(live), 4242, secondInstance.Id) is not null &&
          ServerSnapshotReader.ParseLive(JsonSerializer.Serialize(live), 4242, existingServer.Id) is null,
        "live snapshots cannot be attributed to another instance of the same executable");
    Check(ServerSnapshotReader.ParseLive("{invalid", 4242) is null,
        "invalid live message rejected");
    Check(ServerSnapshotReader.Read(location, null, parsed).State == "Offline",
        "cached snapshots are ignored after the server stops");

    var detailedPlayer = new
    {
        schemaVersion = 2, type = "snapshot", processId = 4242,
        state = "In match", map = "DI_SR",
        players = new[] { new { name = "Alice", token = "p1", platform = "PC",
            networkId = "EOS-123", platformId = "", character = "Ace", accountLevel = 43,
            characterLevel = 18, team = 1, dead = true, health = 25.5, maxHealth = 100.0,
            gadgets = new[] { "Drone", "BounceMat" },
            loadout = new { weapon = 2, expertise = 0, passive = 1 },
            upgrades = new[] {
                new { tier = "Civilian", kind = "Maxup_Health", owned = true },
                new { tier = "Staff", kind = "Boost_HackSpeed", owned = false },
                new { tier = "Guard", kind = "Boost_Cover", owned = false },
                new { tier = "Technician", kind = "Boost_Expertise", owned = false },
                new { tier = "VIP", kind = "UnknownFuturePowerup", owned = false }
            } } }
    };
    var detailed = ServerSnapshotReader.ParseLive(JsonSerializer.Serialize(detailedPlayer), 4242);
    Check(detailed?.Players.Single() is { Character: "Ace", AccountLevel: 43, CharacterLevel: 18,
                                           Team: 1, IsDead: true, HasHealth: true } detailedRow &&
          detailedRow.HealthPercent == 25.5 && detailedRow.HealthText.StartsWith("25", StringComparison.Ordinal) &&
          !snapshot.Players[0].HasHealth && !snapshot.Players[0].IsDead,
        "optional levels, health and death state survive the live snapshot without inventing missing health");
    static PlayerRow LevelPlayer(int? level) =>
        new("Alice", "p1", "PC", "EOS-123", "", Character: "Ace", CharacterLevel: level);
    Check(new[] { 2, 9, 10 }.All(level =>
              LevelPlayer(level) is { HasCharacterLevel: true, IsGoldCharacterLevel: false } player &&
              player.DisplayCharacterLevel == level) &&
          LevelPlayer(11) is { IsGoldCharacterLevel: true, DisplayCharacterLevel: 1 } &&
          LevelPlayer(23) is { HasCharacterLevel: true, IsGoldCharacterLevel: true,
                               DisplayCharacterLevel: 13 } gold &&
          gold.CharacterLevelBadgeText.EndsWith("13", StringComparison.Ordinal) &&
          LevelPlayer(10).CharacterLevelBadgeForeground is Avalonia.Media.SolidColorBrush basicBrush &&
          basicBrush.Color == Avalonia.Media.Color.Parse("#F5F3F8") &&
          gold.CharacterLevelBadgeForeground is Avalonia.Media.SolidColorBrush goldBrush &&
          goldBrush.Color == Avalonia.Media.Color.Parse("#FFD36A") &&
          !LevelPlayer(null).HasCharacterLevel,
        "character level badge stays basic through 10 and subtracts ten for gold levels");
    Check(detailed!.Players[0].GadgetItems.Select(item => item.CanonicalId)
              .SequenceEqual(["Drone", "BouncingMat"]) &&
          detailed.Players[0].UpgradeItems.Count == 5 &&
          detailed.Players[0].UpgradeItems[0] is { Owned: true, Opacity: 1.0 } &&
          detailed.Players[0].UpgradeItems[3].Tier == "Technician" &&
          detailed.Players[0].UpgradeItems[4] is
              { Tier: "VIP", Kind: "UnknownFuturePowerup", Owned: false, Opacity: 0.43 },
        "player equipment preserves five tier positions, including unknown future upgrades");
    Check(detailed.Players[0].LoadoutItems.Select(item => (item.Category, item.Variant))
              .SequenceEqual([("Weapons", "Mod2"), ("Expertises", "Base"), ("Passives", "Mod1")]) &&
          parsed!.Players[0].LoadoutUnavailable,
        "selected weapon, expertise and passive variants survive the live snapshot while older snapshots remain readable");
    Check(parsed!.Players[0].AccountLevel is null && parsed.Players[0].Character.Length == 0,
        "older live bridge snapshots remain readable with unavailable progression");

    const int testPid = 4242;
    await using (var pipe = new NamedPipeServerStream($"Briefcase.Server.{testPid}",
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
    {
        var stateChanges = 0;
        using var connection = new ServerLiveClient(testPid, new WindowsNamedPipeTransport(),
            () => Interlocked.Increment(ref stateChanges));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.WaitForConnectionAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(live));
        while (connection.Latest is null && !timeout.IsCancellationRequested)
            await Task.Delay(20, timeout.Token);
        Check(connection.Latest?.PlayerCount == 2,
            "transport interface receives a live snapshot over the Windows pipe");
        var kickTask = connection.KickAsync("p2", timeout.Token);
        using var commandReader = new StreamReader(pipe, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var commandLine = await commandReader.ReadLineAsync(timeout.Token);
        using var command = JsonDocument.Parse(commandLine!);
        Check(command.RootElement.GetProperty("command").GetString() == "kick" &&
            command.RootElement.GetProperty("playerToken").GetString() == "p2",
            "kick targets the selected connection token, not a display name");
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 2, type = "result",
            requestId = command.RootElement.GetProperty("requestId").GetString(),
            ok = true, message = "Kick sent to player"
        }));
        Check((await kickTask).Ok, "server acknowledgement completes the kick command");
        var moveTask = connection.MoveTeamAsync("p1", 1, 2, timeout.Token);
        using (var move = JsonDocument.Parse((await commandReader.ReadLineAsync(timeout.Token))!))
        {
            Check(move.RootElement.GetProperty("command").GetString() == "moveTeam" &&
                  move.RootElement.GetProperty("team").GetInt32() == 1 &&
                  move.RootElement.GetProperty("teamSize").GetInt32() == 2,
                "team movement carries the destination and lobby team size");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 2, type = "result",
                requestId = move.RootElement.GetProperty("requestId").GetString(),
                ok = true, message = "Player moved to team"
            }));
        }
        Check((await moveTask).Ok, "server acknowledgement completes the team move");
        var swapTask = connection.SwapTeamAsync("p1", "p2", 2, timeout.Token);
        using (var swap = JsonDocument.Parse((await commandReader.ReadLineAsync(timeout.Token))!))
        {
            Check(swap.RootElement.GetProperty("command").GetString() == "swapTeam" &&
                  swap.RootElement.GetProperty("playerToken").GetString() == "p1" &&
                  swap.RootElement.GetProperty("otherToken").GetString() == "p2",
                "team swap identifies both connections");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 2, type = "result",
                requestId = swap.RootElement.GetProperty("requestId").GetString(),
                ok = true, message = "Players swapped"
            }));
        }
        Check((await swapTask).Ok, "server acknowledgement completes the team swap");
        while (Volatile.Read(ref stateChanges) < 2 && !timeout.IsCancellationRequested)
            await Task.Delay(20, timeout.Token);
        var beforeUpdate = Volatile.Read(ref stateChanges);
        await writer.WriteLineAsync(JsonSerializer.Serialize(live));
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 2, type = "snapshot", processId = testPid,
            state = "Lobby", map = "DI_Hardsell", players = Array.Empty<object>()
        }));
        while ((connection.Latest?.State != "Lobby" ||
                Volatile.Read(ref stateChanges) < beforeUpdate + 1) &&
               !timeout.IsCancellationRequested)
            await Task.Delay(20, timeout.Token);
        Check(connection.Latest?.PlayerCount == 0 &&
              Volatile.Read(ref stateChanges) == beforeUpdate + 1,
            "live changes notify the UI once while identical snapshots do not");
    }

    const int reopenPid = 4243;
    var reopenedSnapshot = JsonSerializer.Serialize(new
    {
        schemaVersion = 2, type = "snapshot", processId = reopenPid,
        state = "Lobby", map = "DI_SR", players = Array.Empty<object>()
    });
    await using (var persistentPipe = new NamedPipeServerStream($"Briefcase.Server.{reopenPid}",
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var firstApp = new ServerLiveClient(reopenPid, new WindowsNamedPipeTransport()))
        {
            await persistentPipe.WaitForConnectionAsync(timeout.Token);
            await using var writer = new StreamWriter(persistentPipe, new System.Text.UTF8Encoding(false),
                bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(reopenedSnapshot);
            while (firstApp.Latest is null && !timeout.IsCancellationRequested)
                await Task.Delay(20, timeout.Token);
            Check(firstApp.Latest?.State == "Lobby", "first app session receives server state");
        }
        persistentPipe.Disconnect();
        using (var secondApp = new ServerLiveClient(reopenPid, new WindowsNamedPipeTransport()))
        {
            await persistentPipe.WaitForConnectionAsync(timeout.Token);
            await using var writer = new StreamWriter(persistentPipe, new System.Text.UTF8Encoding(false),
                bufferSize: 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(reopenedSnapshot);
            while (secondApp.Latest is null && !timeout.IsCancellationRequested)
                await Task.Delay(20, timeout.Token);
            Check(secondApp.Latest?.State == "Lobby",
                "reopening the app reconnects to the same running server channel");
        }
    }

    var modsRoot = Path.Combine(win64, "ue4ss", "Mods");
    Directory.CreateDirectory(Path.Combine(modsRoot, "BriefcasePlayerCap", "Data"));
    Directory.CreateDirectory(Path.Combine(modsRoot, "BriefcasePregameTimer"));
    File.WriteAllText(Path.Combine(modsRoot, "mods.txt"),
        "BriefcasePlayerCap : 1\nBriefcasePregameTimer : 0\n");
    File.WriteAllText(Path.Combine(modsRoot, "BriefcasePlayerCap", "Data", "config.json"), "{}");
    Directory.CreateDirectory(Path.Combine(modsRoot, "BriefcasePlayerCap", "dlls"));
    File.WriteAllBytes(Path.Combine(modsRoot, "BriefcasePlayerCap", "dlls", "main.dll"), [1]);
    File.WriteAllText(Path.Combine(modsRoot, "BriefcasePlayerCap", ".serverapp-install.json"), "{\"version\":\"1.2.3\"}");
    var mods = ModCatalog.Read(location);
    Check(mods.Count == 2 && mods[0].State == "Enabled" &&
        mods[0].Configuration == "Has configuration" && mods[0].Version == "1.2.3" &&
        mods[0].HasNativeBinary && mods[0].IsCatalogManaged && mods[1].State == "Disabled" && !mods[1].HasVersion && !mods[1].IsCatalogManaged,
        "local UE4SS mod selection, config, binary and known version read without admin endpoint");

    var selectionRegistry = new ServerRegistry(Path.Combine(temp, "selection-settings.json"), inMemory: true);
    var selectedFirst = new ServerRow("first", win64, true);
    var selectedSecond = new ServerRow("second", Path.Combine(temp, "AnotherServer", "DeceiveInc", "Binaries", "Win64"), true);
    selectionRegistry.Servers.Add(selectedFirst);
    selectionRegistry.Servers.Add(selectedSecond);
    using (var selection = new MainViewModel(registry: selectionRegistry))
    {
        await selection.RefreshAsync();
        Check(!selection.IsSelectionLoading && selection.Mods.Count == 2,
            "first server finishes loading with its own mod list");
        selection.Players.Add(new PlayerRow("Former player", "test", "PC", "", ""));
        selection.SelectedServer = selectedSecond;
        Check(selection.IsSelectionLoading && selection.Mods.Count == 0 && selection.Players.Count == 0 &&
              selection.ServerName == "—" && selection.PlayerCountText == "—",
            "switching servers immediately hides previous server information");
        await selection.RefreshAsync(waitForSelection: true);
        Check(selection.IsSelectionLoading, "the loading view remains until the selected page is ready");
        selection.CompleteSelectionLoading(selectedSecond);
        Check(!selection.IsSelectionLoading, "the loading view clears after the new server page is ready");
    }

    var registry = new ServerRegistry(Path.Combine(temp, "app-settings.json"));
    registry.Language = "fr";
    registry.Save();
    Check(ServerRegistry.ReadSavedLanguage(registry.FilePath) == "fr" &&
          new ServerRegistry(registry.FilePath).Language == "fr",
        "selected application language persists independently of servers");
    registry.Language = "es";
    registry.Save();
    Check(ServerRegistry.ReadSavedLanguage(registry.FilePath) == "es" &&
          new ServerRegistry(registry.FilePath).Language == "es",
        "custom translation language persists in application settings");
    var sentinel = Path.Combine(temp, "demo-settings.json");
    File.WriteAllText(sentinel, "do not replace");
    var inMemory = new ServerRegistry(sentinel, inMemory: true);
    Check(inMemory.Servers.Count == 0, "demo registry does not load real settings");
    inMemory.CatalogUrls.Add("https://example.invalid/catalog.json");
    inMemory.Save();
    Check(File.ReadAllText(sentinel) == "do not replace", "demo registry never persists data");

    using (var demo = new MainViewModel(demo: true))
    {
        Check(demo.Servers.Count == 3 && demo.Players.Count == 5 && demo.Mods.Count == 3 &&
              demo.MarketplaceMods.Count == 3 && demo.IsRunning && demo.ShowPlayerTeams &&
              demo.PlayerTeams.Count >= 2 && demo.PlayerTeams[0].Members.Count == 3 &&
              !demo.CanArrangeTeams,
            "demo fills overview, roster, mods and marketplace without a server");
        var player = demo.Players[0];
        var duo = new PlayerTeamGroup(1, 2, [player]);
        Check(duo.Slots.Count == 2 && duo.Slots[0].Player == player &&
              duo.Slots[1].IsEmpty && duo.Slots[1].Team == 1,
            "a partly filled duo team exposes a drop slot beside its player");
        await demo.KickPlayerAsync(player);
        await demo.StopAsync();
        await demo.RefreshMarketplaceAsync();
        await demo.CheckUpdatesAsync();
        await demo.InstallMarketplaceModAsync(demo.MarketplaceMods[0]);
        await demo.RefreshAsync();
        Check(demo.IsRunning && demo.Players.Contains(player) && demo.MarketplaceMods.Count == 3,
            "demo actions do not access transports, downloads or server files");
        demo.SelectedServer = demo.Servers[^1];
        Check(!demo.IsRunning && demo.Players.Count == 0 && demo.PlayerTeams.Count == 0,
            "demo server selection changes the in-memory snapshot");
        var (demoSettings, demoMaps) = DemoData.Configuration(demo.Servers[0].Id);
        Check(demoSettings["MaxPlayers"] == "12" && demoMaps.Count == 4,
            "demo configuration includes values and repeatable map rotation");
    }
    var first = registry.Add(location.Shipping);
    Check(first.Name == Path.GetFileName(temp),
        "server list uses the installation folder when no name is configured in the INI");
    Check(!first.AutoUpdateBeforeStart,
        "automatic pre-start updates are off by default for development builds");
    first.AutoUpdateBeforeStart = true;
    registry.Save();
    var sibling = registry.Add(location.Shipping);
    Check(sibling.Id != first.Id && sibling.InstanceRoot != first.InstanceRoot && registry.Servers.Count == 2,
        "adding the same installation creates a second isolated instance");
    var firstMods = Path.Combine(first.InstanceRoot, "ue4ss", "Mods");
    var siblingMods = Path.Combine(sibling.InstanceRoot, "ue4ss", "Mods");
    File.WriteAllText(Path.Combine(firstMods, "mods.txt"), "BriefcasePlayerCap : 0\n");
    File.WriteAllText(Path.Combine(firstMods, "BriefcasePlayerCap", "Data", "config.json"),
        "{\"maxPlayers\":12}");
    Check(File.ReadAllText(Path.Combine(siblingMods, "mods.txt")).Contains("BriefcasePlayerCap : 1") &&
          File.ReadAllText(Path.Combine(siblingMods, "BriefcasePlayerCap", "Data", "config.json")) == "{}",
        "UE4SS mod selection and writable mod configuration are isolated per instance");
    var versionRoot = AppDataStore.VersionInstallRoot(temp, "smoketest");
    var versionWin64 = Path.Combine(versionRoot, "DeceiveInc", "Binaries", "Win64");
    Directory.CreateDirectory(versionWin64);
    File.WriteAllBytes(Path.Combine(versionWin64, "DeceiveIncServer-Win64-Shipping.exe"), []);
    var version = registry.AddInstallation("smoketest", versionRoot);
    var originalInstanceRoot = first.InstanceRoot;
    registry.Migrate(first, version);
    Check(first.Win64 == version.Win64 && first.InstanceRoot == originalInstanceRoot &&
          sibling.Win64 == location.Win64 && File.ReadAllText(Path.Combine(firstMods, "mods.txt")) ==
          "BriefcasePlayerCap : 0\n",
        "migration changes only the shared executable and keeps the server's mod/config data");
    registry.AddCatalog("https://example.test/catalog.json");
    var loaded = new ServerRegistry(registry.FilePath);
    Check(loaded.Servers.Count == 2 && loaded.Installations.Single().Branch == "smoketest" &&
          loaded.Servers[0].Win64 == version.Win64 &&
          loaded.CatalogUrls.Single() == "https://example.test/catalog.json" &&
          loaded.Servers[0].AutoUpdateBeforeStart && loaded.Servers[1].InstanceRoot != loaded.Servers[0].InstanceRoot,
        "server list, update preference and catalog sources persist");
    foreach (var row in loaded.Servers.ToArray()) loaded.Remove(row);
    Check(new ServerRegistry(registry.FilePath).Servers.Count == 0,
        "removing a server only removes its registry entry");
    var legacyData = Path.Combine(temp, "schema2-data");
    var legacyWin64 = Path.Combine(legacyData, "game", "dedicated", "DeceiveInc", "Binaries", "Win64");
    Directory.CreateDirectory(legacyWin64);
    File.WriteAllBytes(Path.Combine(legacyWin64, "DeceiveIncServer-Win64-Shipping.exe"), []);
    var legacySettings = Path.Combine(legacyData, "settings.json");
    var legacyId = Guid.NewGuid().ToString("N");
    File.WriteAllText(legacySettings, JsonSerializer.Serialize(new
    {
        schemaVersion = 2, language = "en",
        servers = new[] { new { id = legacyId, win64 = location.Win64,
            instanceRoot = sibling.InstanceRoot, managed = true, autoUpdateBeforeStart = false } },
        catalogUrls = Array.Empty<string>()
    }));
    var upgraded = new ServerRegistry(legacySettings);
    Check(upgraded.Servers.Single().Id == legacyId && upgraded.Installations.Single().Branch == "public" &&
          upgraded.Installations.Single().InstallRoot == Path.Combine(legacyData, "game", "dedicated") &&
          File.Exists(legacySettings + ".v2-backup"),
        "schema 2 upgrades without losing servers and registers the existing shared public installation");
    File.WriteAllText(registry.FilePath, "{invalid");
    var recoveredRegistry = new ServerRegistry(registry.FilePath);
    Check(recoveredRegistry.LoadError.Length > 0 && recoveredRegistry.Servers.Count == 0,
        "malformed registry cannot crash startup");
    recoveredRegistry.Add(location.Shipping);
    Check(Directory.EnumerateFiles(temp, "app-settings.json.invalid-*").Any(),
        "malformed registry is backed up before new settings are saved");

    File.AppendAllText(location.ConfigFile, "[Other]\nKeep=unchanged\n");
    var (settings, maps) = ServerIniEditor.Read(location.ConfigFile);
    Check(ServerIniEditor.Fields.Any(field => field.Key == "HeatPercentDamagingVIP") &&
        ServerIniEditor.Fields.Any(field => field.Key == "HeatDelayAggroPostCover"),
        "visual server schema includes heat damage and delay settings");
    settings["ServerName"] = "Fixture edited";
    settings["MaxPlayers"] = "10";
    ServerIniEditor.Save(location.ConfigFile, settings, ["DI_SR", "DI_DS", "DI_SR"]);
    var (saved, savedMaps) = ServerIniEditor.Read(location.ConfigFile);
    Check(saved["ServerName"] == "Fixture edited" && saved["MaxPlayers"] == "10" &&
        savedMaps.SequenceEqual(["DI_SR", "DI_DS", "DI_SR"]), "ordered repeated map rotation round-trip");
    Check(RejectsInvalidData(() => ServerIniEditor.Save(location.ConfigFile,
              new Dictionary<string, string>(settings) { ["MaxPlayers"] = "17" }, ["DI_SR"])) &&
          RejectsInvalidData(() => ServerIniEditor.Save(location.ConfigFile,
              new Dictionary<string, string>(settings) { ["BotsAmount"] = "11" }, ["DI_SR"])) &&
          RejectsInvalidData(() => ServerIniEditor.Save(location.ConfigFile,
              new Dictionary<string, string>(settings) { ["ServerName"] = new string('N', 49) }, ["DI_SR"])),
        "server configuration rejects more than 16 players, bots above player capacity and names over 48 characters");
    ServerIniEditor.SaveServerName(Path.Combine(recoveredRegistry.Servers.Single().InstanceRoot,
        "TripwireServer.ini"), "Fixture edited");
    using (var namedServer = new MainViewModel(registry: recoveredRegistry))
    {
        await namedServer.RefreshAsync();
        Check(namedServer.SelectedServerName == "Fixture edited" &&
              recoveredRegistry.Servers.Single().Name == "Fixture edited" &&
              new ServerRegistry(recoveredRegistry.FilePath).Servers.Single().Name == "Fixture edited",
            "server list and selected server heading read the INI name on refresh and startup");
    }
    Check(File.ReadAllText(location.ConfigFile).Contains("Keep=unchanged"),
        "INI editor preserves unrelated sections");
    File.AppendAllText(location.ConfigFile, "; retained marker\r\n");
    ServerIniEditor.SaveMapRotation(location.ConfigFile, ["DI_Hardsell", "DI_SR", "DI_Hardsell"]);
    var (rotationValues, rotationMaps) = ServerIniEditor.Read(location.ConfigFile);
    Check(rotationMaps.SequenceEqual(["DI_Hardsell", "DI_SR", "DI_Hardsell"]) &&
        rotationValues["ServerName"] == "Fixture edited" && rotationValues["MaxPlayers"] == "10" &&
        File.ReadAllText(location.ConfigFile).Contains("Keep=unchanged") &&
        File.ReadAllText(location.ConfigFile).Contains("; retained marker"),
        "immediate rotation save preserves unrelated settings and duplicate map order");
    settings["HeatDecayRate"] = "100";
    try
    {
        ServerIniEditor.Save(location.ConfigFile, settings, ["DI_SR"]);
        throw new Exception("Heat limit accepted an invalid value.");
    }
    catch (InvalidDataException) { }

    var configPath = Path.Combine(modsRoot, "BriefcasePlayerCap", "Data", "config.json");
    File.WriteAllText(configPath, "{\"soloLimit\":12,\"enabled\":true,\"nested\":{\"rate\":1.5}}");
    var editor = new ModConfigEditor(configPath);
    var changes = editor.Values.ToDictionary(v => v, v => v.Value);
    changes[editor.Values.Single(v => v.Label == "nested · rate")] = "2.25";
    editor.Save(changes);
    using var edited = JsonDocument.Parse(File.ReadAllText(configPath));
    Check(edited.RootElement.GetProperty("nested").GetProperty("rate").GetDecimal() == 2.25m &&
        edited.RootElement.GetProperty("soloLimit").GetInt32() == 12,
        "mod editor preserves other JSON values while editing a nested number");
    var draftEditor = new ModConfigEditor(configPath, "{\"soloLimit\":15,\"enabled\":false}");
    Check(draftEditor.Values.Single(value => value.Label == "soloLimit").Value == "15" &&
          JsonDocument.Parse(draftEditor.Render(draftEditor.Values.ToDictionary(value => value, value => value.Value)))
              .RootElement.GetProperty("enabled").GetBoolean() == false,
        "form editor can reopen an unsaved JSON draft without writing the file");
    var modBeforeInvalid = File.ReadAllText(configPath);
    try { ModConfigEditor.SaveRaw(configPath, "{ invalid JSON }"); throw new Exception("Invalid JSON was saved."); }
    catch (JsonException) { }
    Check(File.ReadAllText(configPath) == modBeforeInvalid, "invalid JSON leaves mod configuration untouched");
    ModConfigEditor.SaveRaw(configPath, "{\"soloLimit\":16,\"custom\":{\"entries\":[1,2]}}");
    using var rawSaved = JsonDocument.Parse(File.ReadAllText(configPath));
    Check(rawSaved.RootElement.GetProperty("custom").GetProperty("entries").GetArrayLength() == 2 &&
          File.Exists(configPath + ".briefcase-backup"),
        "JSON editor saves nested values and keeps a backup");

    var package = Path.Combine(temp, "framework.zip");
    var packageFiles = new Dictionary<string, byte[]>
    {
        ["version.dll"] = [1, 2, 3], ["ue4ss/UE4SS.dll"] = [4, 5],
        ["ue4ss/Mods/mods.txt"] = [], ["Briefcase/Core/Briefcase.NativeHost.dll"] = [6, 7],
        ["ue4ss/Mods/BriefcaseServerBridge/dlls/main.dll"] = [8, 9],
        ["ue4ss/Mods/BriefcaseServerBridge/enabled.txt"] = []
    };
    var gameHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location.Shipping))).ToLowerInvariant();
    var manifest = new
    {
        updateSchema = 1, platform = "windows-x64", environment = "server", frameworkVersion = "0.0.1",
        gameSha256 = gameHash,
        files = packageFiles.Select(pair => new { path = pair.Key, bytes = pair.Value.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(),
            preserve = pair.Key == "ue4ss/Mods/mods.txt" }).ToArray()
    };
    using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("Package.json");
        using (var writer = new StreamWriter(entry.Open())) writer.Write(JsonSerializer.Serialize(manifest));
        foreach (var (name, content) in packageFiles)
        {
            entry = zip.CreateEntry(name);
            using var output = entry.Open();
            output.Write(content);
        }
    }
    BriefcasePackageInstaller.InstallOrUpdate(location, package, "0.0.1", new Progress<string>());
    Check(File.Exists(Path.Combine(win64, "version.dll")) &&
        File.Exists(Path.Combine(win64, "Briefcase", "Core", "Briefcase.NativeHost.dll")) &&
        File.Exists(Path.Combine(win64, "ue4ss", "Mods", "BriefcaseServerBridge", "dlls", "main.dll")) &&
        File.Exists(Path.Combine(win64, "ue4ss", "Mods", "BriefcaseServerBridge", "enabled.txt")),
        "validated initial framework archive installs the live server bridge into Win64");
    var legacyInjector = Path.Combine(win64, "Briefcase", "Core", "Tools", "Briefcase.ServerInjector.exe");
    var legacyBootstrap = Path.Combine(win64, "Briefcase", "Core", "Briefcase.ServerBootstrap.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyInjector)!);
    File.WriteAllBytes(legacyInjector, [10]);
    File.WriteAllBytes(legacyBootstrap, [11]);
    Check(ServerPreparation.Prepare(location) &&
          !File.Exists(Path.Combine(win64, "version.dll")) &&
          File.Exists(Path.Combine(win64, "version.dll.disabled")) &&
          !File.Exists(legacyInjector) && !File.Exists(legacyBootstrap),
        "ServerApp disables the proxy and removes legacy server-side injection files");
    using (var nativeUpdates = JsonDocument.Parse(File.ReadAllText(Path.Combine(win64, "Briefcase", "updater.json"))))
        Check(!nativeUpdates.RootElement.GetProperty("enabled").GetBoolean() &&
              !nativeUpdates.RootElement.GetProperty("updateMods").GetBoolean(),
            "ServerApp disables the legacy native updater before launch");
    Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(win64)!)!,
            "Saved", "Config", "WindowsServer", "Engine.ini")).Contains("sb.DisableEAC=1"),
        "ServerApp prepares the anti-cheat setting before injection");
    File.WriteAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt"), "CustomMod : 1\n");
    BriefcasePackageInstaller.InstallOrUpdate(location, package, "0.0.1", new Progress<string>());
    Check(File.ReadAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt")) == "CustomMod : 1\n" &&
          File.ReadAllBytes(Path.Combine(win64, "version.dll")).SequenceEqual(new byte[] { 1, 2, 3 }),
        "framework upgrade preserves mod selection and replaces packaged binaries");
    var interrupted = Path.Combine(win64, "Briefcase", "Updates", "ServerApp-test");
    Directory.CreateDirectory(Path.Combine(interrupted, "previous"));
    File.WriteAllBytes(Path.Combine(interrupted, "previous", "version.dll"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(win64, "version.dll"), [9]);
    File.WriteAllText(Path.Combine(interrupted, "transaction.json"),
        "[{\"path\":\"version.dll\",\"existed\":true}]");
    BriefcasePackageInstaller.RecoverPending(location);
    Check(!Directory.Exists(interrupted) && File.ReadAllBytes(Path.Combine(win64, "version.dll"))
          .SequenceEqual(new byte[] { 1, 2, 3 }),
        "interrupted framework installation restores its previous binary before startup");
    Check(ServerPreparation.Prepare(location) &&
          !File.Exists(Path.Combine(win64, "version.dll")) &&
          File.ReadAllBytes(Path.Combine(win64, "version.dll.disabled")).SequenceEqual(new byte[] { 1, 2, 3 }),
        "ServerApp disables the newly installed proxy after an update");

    var modZip = Path.Combine(temp, "mod.zip");
    using (var zip = ZipFile.Open(modZip, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("ue4ss/Mods/FixtureMod/dlls/main.dll");
        using var output = entry.Open();
        output.Write([42]);
    }
    var marketplaceEntry = new MarketplaceMod("FixtureMod", "Fixture", "Test", "1.0.0",
        new Uri("https://example.test/mod.zip"), "", "https://example.test/catalog.json");
    ModMarketplace.Install(location, marketplaceEntry, modZip);
    Check(File.Exists(Path.Combine(modsRoot, "FixtureMod", "dlls", "main.dll")) &&
        File.ReadAllText(Path.Combine(modsRoot, "mods.txt")).Contains("FixtureMod : 1") &&
        ModMarketplace.InstalledVersion(location, "FixtureMod") == "1.0.0",
        "marketplace archive stays inside its UE4SS mod directory and enables the mod");
    ModCatalog.SetEnabled(location, "FixtureMod", false);
    Check(ModCatalog.Read(location).Single(mod => mod.Name == "FixtureMod").State == "Disabled" &&
        File.ReadAllText(Path.Combine(modsRoot, "mods.txt")).Contains("FixtureMod : 0"),
        "disabled mod stays installed and UE4SS selection changes");
    ModCatalog.SetEnabled(location, "FixtureMod", true);
    Check(ModCatalog.Read(location).Single(mod => mod.Name == "FixtureMod").State == "Enabled",
        "installed mod can be enabled again");
    ModCatalog.Uninstall(location, "FixtureMod");
    Check(!Directory.Exists(Path.Combine(modsRoot, "FixtureMod")) &&
        !File.ReadAllText(Path.Combine(modsRoot, "mods.txt")).Contains("FixtureMod :") &&
        Directory.Exists(Path.Combine(modsRoot, "BriefcasePlayerCap")),
        "uninstall removes only the selected mod and its UE4SS selection");
    var launcherRoot = Path.Combine(temp, "launcher");
    Directory.CreateDirectory(launcherRoot);
    var payload = new Dictionary<string, byte[]>
    {
        ["Briefcase.ServerApp.exe"] = [1, 2, 3],
        ["Native/Briefcase.ServerInjector.exe"] = [4],
        ["Native/Briefcase.ServerBootstrap.dll"] = [5]
    };
    var updateManifest = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        version = "0.1.0",
        files = payload.Select(pair => new
        {
            path = pair.Key,
            bytes = pair.Value.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant()
        }).ToArray()
    });
    var updateZip = Path.Combine(temp, "app-update.zip");
    using (var zip = ZipFile.Open(updateZip, ZipArchiveMode.Create))
    {
        foreach (var (name, content) in payload)
        {
            using var appUpdateEntryStream = zip.CreateEntry("App/versions/0.1.0/" + name).Open();
            appUpdateEntryStream.Write(content);
        }
        using var appUpdateManifestStream = zip.CreateEntry("App/versions/0.1.0/manifest.json").Open();
        appUpdateManifestStream.Write(System.Text.Encoding.UTF8.GetBytes(updateManifest));
    }
    var prepared = AppUpdateStore.Prepare(launcherRoot, updateZip, "0.1.0");
    Check(AppUpdateStore.Current(launcherRoot) is null && File.Exists(prepared.Executable),
        "new app version is staged without changing the active version");
    AppUpdateStore.Activate(launcherRoot, prepared);
    Check(AppUpdateStore.Current(launcherRoot)?.Version == "0.1.0",
        "new app version activates after successful startup");
    File.WriteAllBytes(prepared.Executable, [9]);
    try
    {
        AppUpdateStore.Prepare(launcherRoot, updateZip, "0.1.0");
        throw new Exception("Modified installed app version accepted.");
    }
    catch (InvalidDataException) { }
    Check(LauncherUpdateService.ReadRepository(launcherRoot) is null &&
          LauncherUpdateService.IsNewer("0.1.1", "0.1.0") &&
          !LauncherUpdateService.IsNewer("0.1.0", "0.1.0"),
        "updates remain disabled until configured and compare semantic versions");

    await ServerRemoval.DeleteFilesAsync(location);
    Check(!Directory.Exists(temp), "deletion removes only the confirmed test server installation");
}
finally
{
    if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
}

Console.WriteLine("ServerApp contracts passed.");

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("Contract failed: " + description);
}

static bool RejectsInvalidData(Action action)
{
    try { action(); return false; }
    catch (InvalidDataException) { return true; }
}
