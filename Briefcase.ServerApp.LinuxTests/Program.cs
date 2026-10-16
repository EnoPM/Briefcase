using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Briefcase.ServerApp;
using Briefcase.ServerApp.Launcher;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
if (args.Length == 2 && args[0] == "--launcher-pending")
{
    var version = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "App/current.json")))
        .RootElement.GetProperty("version").GetString()!;
    Console.WriteLine(LauncherUpdateStore.IsPending(args[1], version) ? "PENDING" : "NOT PENDING");
    return;
}
typeof(ServerIniEditor).Assembly.GetType("Briefcase.ServerApp.AppTranslations")!
    .GetMethod("Select")!.Invoke(null, ["en", Path.Combine(Directory.GetCurrentDirectory(), "Briefcase.ServerApp")]);
if (args.Length == 3 && args[0] == "--bridge")
{
    var briefcaseRoot = args[1];
    var processId = int.Parse(args[2]);
    using var live = new ServerLiveClient(processId, new UnixServerSocketTransport(briefcaseRoot));
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (live.Latest is null && DateTime.UtcNow < deadline)
        await Task.Delay(100);
    var snapshot = live.Latest ?? throw new Exception("The Linux bridge did not provide a valid snapshot.");
    if (!snapshot.HasPlayerRoster || snapshot.State != "Lobby" || string.IsNullOrWhiteSpace(snapshot.Map))
        throw new Exception("The Linux bridge snapshot was incomplete.");
    Console.WriteLine($"Linux bridge connected: {snapshot.State}, {snapshot.Map}, {snapshot.Players.Count} players.");
    return;
}
if (args.Length == 1 && args[0] == "--test-profile")
{
    var registry = new ServerRegistry();
    var validLocation = ServerLocation.TryCreate(registry.Servers.FirstOrDefault(),
        out var location, out var error);
    if (registry.Servers.Count != 1 || registry.Installations.Count != 1 ||
        !validLocation ||
        !File.Exists(location!.Shipping) ||
        !ModCatalog.Read(location).Any(mod => mod.Name == "briefcase.pregame-timer" && mod.IsEnabled))
        throw new Exception("The isolated Linux test profile is incomplete: " + error);
    using var game = ServerProcess.Find(location!);
    Console.WriteLine($"Linux test profile ready: {registry.Servers[0].Name}, PregameTimer enabled, game PID {game?.Id.ToString() ?? "not found"}.");
    return;
}
if (args.Length == 1 && args[0] == "--repair-profile-rotation")
{
    var registry = new ServerRegistry();
    string error = "";
    ServerLocation? location = null;
    if (registry.Servers.Count != 1 ||
        !ServerLocation.TryCreate(registry.Servers.Single(), out location, out error) ||
        location is null || !location.IsInstance)
        throw new Exception("The isolated Linux test profile is unavailable: " + error);
    using var running = ServerProcess.Find(location);
    if (running is not null) throw new Exception("Stop the test server before repairing its rotation.");
    var before = File.ReadAllLines(location.ConfigFile).Count(line =>
        line.TrimStart().Contains("MapRotation=", StringComparison.OrdinalIgnoreCase));
    var changed = ServerIniEditor.RepairServerWrittenMapRotation(location.ConfigFile);
    var (_, maps) = ServerIniEditor.Read(location.ConfigFile);
    Console.WriteLine($"Test rotation: {before} INI entries, {maps.Count} selected maps; repaired={changed}.");
    return;
}
if (args.Length == 1 && args[0] == "--overview")
{
    using var view = new MainViewModel();
    await view.RefreshAsync();
    await Task.Delay(2000);
    await view.RefreshAsync();
    Console.WriteLine($"Overview: {view.ServerState}, {view.MapName}, {view.PlayerCountText}, {view.DataSource}; " +
                      $"network {view.NetworkStatus.Address}, UPnP {view.NetworkStatus.Upnp}, EOS {view.NetworkStatus.Eos}");
    if (!view.HasPlayerRoster || view.MapName == "—" || view.PlayerCountText == "—")
        throw new Exception("The Linux Overview did not receive the live server snapshot.");
    if (view.NetworkStatus.Address.Length == 0 || view.NetworkStatus.Eos != NetworkCheck.Ready)
        throw new Exception("The Linux Overview did not read the current network status.");
    return;
}
if (args.Length == 1 && args[0] == "--network-profile")
{
    var registry = new ServerRegistry();
    Check(ServerLocation.TryCreate(registry.Servers.Single(), out var location, out _),
        "Linux instance is registered");
    using var game = ServerProcess.Find(location!);
    Check(game is not null, "Linux instance is running");
    var network = ServerNetworkStatusReader.Read(location!, game!.StartTime.ToUniversalTime());
    Console.WriteLine($"Network: {network.Address}, firewall {network.Firewall}, " +
                      $"UPnP {network.Upnp}, EOS {network.Eos}");
    Check(network.Address.Length > 0 && network.Eos == NetworkCheck.Ready,
        "Linux instance network information is read from its current log");
    return;
}
var root = Path.Combine(Path.GetTempPath(), "BriefcaseLinuxContracts-" + Guid.NewGuid().ToString("N"));
try
{
    await TestSocketRetry(Path.Combine(root, "bridge-retry"));
    var binaries = Path.Combine(root, "game", "DeceiveInc", "Binaries", "Linux");
    Directory.CreateDirectory(binaries);
    File.WriteAllText(Path.Combine(binaries, "DeceiveIncServer-Linux-Shipping"), "fixture");
    var id = Guid.NewGuid().ToString("N");
    var instance = Path.Combine(root, "data", "servers", id);
    Directory.CreateDirectory(Path.Combine(instance, "Briefcase", "Core"));
    File.WriteAllText(Path.Combine(instance, "Briefcase", "Core", "libBriefcase.NativeHost.so"), "fixture");
    if (!ServerLocation.TryCreate(new ServerRow(id, binaries, true, instance), out var location, out var error))
        throw new Exception(error);
    var logDirectory = Path.Combine(instance, "GameView", "DeceiveInc", "Saved", "Logs");
    Directory.CreateDirectory(logDirectory);
    var ports = (ValueTuple<int, int>)typeof(ServerProcess)
        .GetMethod("ReadPorts", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [location!.ConfigFile])!;
    var (gamePort, queryPort) = ports;
    File.WriteAllText(Path.Combine(logDirectory, "DeceiveInc.log"),
        $"LogInit: Command Line: -TripwireServerIni={location.ConfigFile} -Port={gamePort} -QueryPort={queryPort}\n" +
        "[NetPosture] Public IP (ipify): 203.0.113.42:57779\n" +
        "[NetPosture] UPnP: Not Available (IGD status=0)\n" +
        "UEOSServerSession::OnCreateSessionComplete: SessionName:GameSession Success:1\n");
    var network = ServerNetworkStatusReader.Read(location, DateTime.UtcNow.AddSeconds(-5));
    Check(network.Address == "203.0.113.42:57779" && network.Upnp == NetworkCheck.Failed &&
          network.Eos == NetworkCheck.Ready && network.Firewall == NetworkCheck.Unknown,
        "Linux instance network status is read from private game logs");

    const string catalogUrl = "https://example.org/catalog.json";
    var packageOne = CreatePackage(root, "1.0.0", "initial");
    var mod = new MarketplaceMod("fixturemod", "Fixture mod", "Fixture", "1.0.0",
        new Uri("https://example.org/fixture.zip"), new string('a', 64), catalogUrl,
        Author: "Test", CatalogSchema: 3)
    { PlatformInstallId = "fixture.mod", PlatformAvailable = true };
    ModMarketplace.Install(location!, mod, packageOne);
    var installed = Path.Combine(location!.ModsRoot, "fixture.mod");
    Check(File.Exists(Path.Combine(installed, "Fixture.so")), "Linux binary installed");
    Check(ModCatalog.Read(location).Single(row => row.Name == "fixture.mod").IsEnabled,
        "Linux mod enabled after installation");

    var config = Path.Combine(installed, "Data", "config.json");
    Directory.CreateDirectory(Path.GetDirectoryName(config)!);
    File.WriteAllText(config, "{\"user\":true}");
    var packageTwo = CreatePackage(root, "1.1.0", "replacement");
    ModMarketplace.Install(location, mod with { Version = "1.1.0" }, packageTwo);
    Check(File.ReadAllText(config) == "{\"user\":true}", "user configuration retained on update");
    Check(File.ReadAllText(Path.Combine(installed, "Fixture.so")) == "replacement",
        "binary replaced on update");
    var invalid = CreatePackage(root, "1.2.0", "tampered", badHash: true);
    Check(RejectsInvalid(() => ModMarketplace.Install(location, mod with { Version = "1.2.0" }, invalid)),
        "modified package rejected");
    Check(File.ReadAllText(Path.Combine(installed, "Fixture.so")) == "replacement",
        "rejected package leaves previous binary intact");
    ModCatalog.SetEnabled(location, "fixture.mod", false);
    Check(!ModCatalog.Read(location).Single(row => row.Name == "fixture.mod").IsEnabled,
        "Linux mod can be disabled");
    ModCatalog.Uninstall(location, "fixture.mod");
    Check(!Directory.Exists(installed), "Linux mod can be uninstalled");

    var v2 = Encoding.UTF8.GetBytes("""
        {"schemaVersion":2,"mods":[{"id":"fixturemod","name":"Fixture mod","description":"Fixture","version":"1.0.0","author":"Test","downloadUrl":"https://example.org/windows.zip","archiveSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
        """);
    Check(!ModMarketplace.ParseCatalog(v2, catalogUrl).Single().PlatformAvailable,
        "Windows-only catalog package unavailable on Linux");
    var v3 = Encoding.UTF8.GetBytes("""
        {"schemaVersion":3,"mods":[{"id":"fixturemod","name":"Fixture mod","description":"Fixture","version":"1.0.0","author":"Test","packages":{"windows-x64":{"downloadUrl":"https://example.org/windows.zip","archiveSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"linux-x64":{"installId":"fixture.mod","downloadUrl":"https://example.org/linux.zip","archiveSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}}]}
        """);
    var parsed = ModMarketplace.ParseCatalog(v3, catalogUrl).Single();
    Check(parsed.PlatformAvailable && parsed.InstallId == "fixture.mod" &&
          parsed.DownloadUrl.AbsoluteUri == "https://example.org/linux.zip", "Linux package selected from catalog");
    var defaultCatalog = ModMarketplace.ParseCatalog(
        File.ReadAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "marketplace", "catalog-v3.json")),
        "https://raw.githubusercontent.com/EnoPM/Briefcase/main/marketplace/catalog-v3.json");
    Check(defaultCatalog.Count == 4 && defaultCatalog.All(entry => entry.PlatformAvailable &&
          entry.InstallId.StartsWith("briefcase.", StringComparison.Ordinal)),
        "default catalog exposes four native Linux mods");
    if (args.Length >= 1)
    {
        using var realArchive = ZipFile.OpenRead(args[0]);
        using var manifestStream = realArchive.GetEntry("ModPackage.json")!.Open();
        using var packageManifest = JsonDocument.Parse(manifestStream);
        var packageVersion = packageManifest.RootElement.GetProperty("version").GetString()!;
        var real = new MarketplaceMod("briefcasepregametimer", "Pregame Timer", "", packageVersion,
            new Uri("https://example.org/pregame.zip"), new string('b', 64), catalogUrl,
            Author: "Test", CatalogSchema: 3)
        { PlatformInstallId = "briefcase.pregame-timer", PlatformAvailable = true };
        ModMarketplace.Install(location, real, args[0]);
        Check(ModCatalog.Read(location).Any(row => row.Name == real.InstallId && row.IsEnabled && row.HasNativeBinary),
            "a real Linux mod package installs and is visible in the app");
        var configPath = Path.Combine(location.ModsRoot, real.InstallId, "Data", "config.json");
        var form = new ModConfigEditor(configPath);
        Check(form.Values.Count > 0, "real Linux mod configuration opens in the visual form");
        form.Save(new Dictionary<ModConfigValue, string>());
        using var saved = JsonDocument.Parse(File.ReadAllText(configPath));
        Check(saved.RootElement.ValueKind == JsonValueKind.Object,
            "real Linux mod configuration saves as valid JSON");
    }
    TestLinuxAppUpdate(root);
    if (args.Length >= 2) TestRealRelease(root, args[1]);
    if (args.Length >= 3) TestRealServerConfiguration(root, args[2]);
    if (args.Length >= 5) TestLiveServer(root, args[2], args[3], args[4],
        args.Length >= 6 ? args[5] : null);
    Console.WriteLine("Linux ServerApp contracts passed.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

static string CreatePackage(string root, string version, string content, bool badHash = false)
{
    var output = Path.Combine(root, "mod-" + version + (badHash ? "-bad" : "") + ".zip");
    var modManifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        id = "fixture.mod", version, entry = "Fixture.so", environment = "server", minimumApi = 1
    }));
    var files = new Dictionary<string, byte[]>
    {
        ["Briefcase/Mods/fixture.mod/briefcase.mod.json"] = modManifest,
        ["Briefcase/Mods/fixture.mod/Fixture.so"] = Encoding.UTF8.GetBytes(content),
    };
    var rows = files.Select(pair => new
    {
        path = pair.Key, bytes = pair.Value.Length,
        sha256 = badHash && pair.Key.EndsWith(".so", StringComparison.Ordinal)
            ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(),
        mode = 420
    }).ToArray();
    var manifest = JsonSerializer.SerializeToUtf8Bytes(new
    {
        updateSchema = 1, kind = "briefcase-mod", platform = "linux-x64",
        modId = "fixture.mod", version, repository = "Example/Fixture", files = rows
    });
    using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
    foreach (var (name, data) in files.Append(new KeyValuePair<string, byte[]>("ModPackage.json", manifest)))
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }
    return output;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("Contract failed: " + message);
}

static bool RejectsInvalid(Action action)
{
    try { action(); return false; }
    catch (InvalidDataException) { return true; }
}

static void TestLinuxAppUpdate(string root)
{
    const string version = "9.9.9";
    var installRoot = Path.Combine(root, "application");
    Directory.CreateDirectory(installRoot);
    var archive = CreateAppRelease(root, version, false);
    var candidate = AppUpdateStore.Prepare(installRoot, archive, version);
    Check(File.Exists(candidate.Executable), "Linux application executable installed");
    Check((File.GetUnixFileMode(candidate.Executable) & UnixFileMode.UserExecute) != 0,
        "installed application is executable");
    var stage = LauncherUpdateStore.Stage(installRoot, archive, version);
    Check(LauncherUpdateStore.IsPending(installRoot, version), "verified launcher update staged");
    Check((File.GetUnixFileMode(Path.Combine(stage, "Briefcase")) & UnixFileMode.UserExecute) != 0,
        "staged launcher is executable");
    AppUpdateStore.Activate(installRoot, candidate);
    Check(AppUpdateStore.Current(installRoot)?.Version == version, "new version activated");
    AppLauncherUpdate.ApplyPrepared(installRoot, version);
    Check(File.Exists(Path.Combine(installRoot, "Briefcase")) &&
          (File.GetUnixFileMode(Path.Combine(installRoot, "Briefcase")) & UnixFileMode.UserExecute) != 0,
        "launcher replaced with executable Linux file");
    Check(!LauncherUpdateStore.IsPending(installRoot, version), "completed launcher update cleared");

    var corrupt = CreateAppRelease(root, "9.9.10", true);
    Check(RejectsInvalid(() => AppUpdateStore.Prepare(installRoot, corrupt, "9.9.10")),
        "tampered application archive rejected");
    Check(AppUpdateStore.Current(installRoot)?.Version == version,
        "rejected update keeps previous active version");

    var failedDirectory = Path.Combine(root, "failed-candidate");
    Directory.CreateDirectory(failedDirectory);
    var failedExecutable = Path.Combine(failedDirectory, "Briefcase.ServerApp");
    File.WriteAllText(failedExecutable, "#!/bin/sh\nexit 1\n");
    File.SetUnixFileMode(failedExecutable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    using var process = new AppProcessController();
    Check(!process.LaunchAndWaitAsync(new InstalledApp("9.9.10", failedDirectory), CancellationToken.None)
            .GetAwaiter().GetResult(), "failed application startup is detected");
    Check(AppUpdateStore.Current(installRoot)?.Version == version,
        "failed startup leaves previous active version selected");
}

static string CreateAppRelease(string root, string version, bool tamper)
{
    var output = Path.Combine(root, "app-" + version + ".zip");
    var prefix = $"App/versions/{version}/";
    var appFiles = new Dictionary<string, byte[]>
    {
        ["Briefcase.ServerApp"] = Encoding.UTF8.GetBytes("app"),
        ["Native/Briefcase.ServerLauncher"] = Encoding.UTF8.GetBytes("server launcher"),
        ["libSkiaSharp.so"] = Encoding.UTF8.GetBytes("app skia")
    };
    var launcherFiles = new Dictionary<string, byte[]>
    {
        ["Briefcase"] = Encoding.UTF8.GetBytes("launcher"),
        ["libSkiaSharp.so"] = Encoding.UTF8.GetBytes("launcher skia"),
        ["libHarfBuzzSharp.so"] = Encoding.UTF8.GetBytes("launcher harfbuzz")
    };
    byte[] Manifest(Dictionary<string, byte[]> files) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, version,
        files = files.Select(pair => new { path = pair.Key, bytes = pair.Value.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant() }).ToArray()
    });
    using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
    void Add(string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }
    Add(prefix + "manifest.json", Manifest(appFiles));
    foreach (var (name, data) in appFiles)
        Add(prefix + name, tamper && name == "Briefcase.ServerApp" ? Encoding.UTF8.GetBytes("bad") : data);
    Add("Launcher/manifest.json", Manifest(launcherFiles));
    foreach (var (name, data) in launcherFiles) Add(name, data);
    return output;
}

static void TestRealRelease(string root, string archive)
{
    var releaseRoot = Path.Combine(root, "real-release");
    var app = AppUpdateStore.Prepare(releaseRoot, archive, "0.1.5");
    Check(File.Exists(app.Executable) &&
          (File.GetUnixFileMode(app.Executable) & UnixFileMode.UserExecute) != 0,
        "real release archive installs an executable app");
    var staged = LauncherUpdateStore.Stage(releaseRoot, archive, "0.1.5");
    Check(File.Exists(Path.Combine(staged, "Briefcase")), "real release stages launcher");
    AppUpdateStore.Activate(releaseRoot, app);
    AppLauncherUpdate.ApplyPrepared(releaseRoot, "0.1.5");
    Check(AppUpdateStore.Current(releaseRoot)?.Version == "0.1.5" &&
          (File.GetUnixFileMode(Path.Combine(releaseRoot, "Briefcase")) & UnixFileMode.UserExecute) != 0,
        "real archive installs both executable programs");
}

static void TestRealServerConfiguration(string root, string binaries)
{
    Check(ServerLocation.TryCreate(binaries, out var installation, out _),
        "real Linux game installation is recognized");
    var testData = Path.Combine(root, "real-server-configuration");
    var id = Guid.NewGuid().ToString("N");
    var instanceRoot = ServerInstanceFiles.Provision(testData, id, installation!, "Linux Test Server");
    Check(ServerLocation.TryCreate(new ServerRow(id, binaries, true, instanceRoot),
        out var instance, out _), "Linux instance is recognized");
    var schema = FormSchema.LoadForInstallation(instance!);
    var (values, _) = ServerIniEditor.Read(instance!.ConfigFile, schema);
    values["ServerName"] = "Linux Config Test";
    ServerIniEditor.Save(instance.ConfigFile, values, ["DI_Hardsell"], schema);
    var (savedValues, maps) = ServerIniEditor.Read(instance.ConfigFile, schema);
    Check(savedValues["ServerName"] == "Linux Config Test" && maps.SequenceEqual(["DI_Hardsell"]),
        "server settings and a single-map rotation persist in the Linux instance: " +
        savedValues["ServerName"] + "; maps=" + string.Join(',', maps));
    var balanceTemplate = File.ReadAllBytes(instance.BalanceTemplateFile);
    var balance = BalanceProfileEditor.Load(instance);
    Check(balance.Fields.Count > 0 && balance.HasTemplate, "balance form loads the Linux game template");
    balance.Save();
    Check(BalanceProfileEditor.Load(instance).Fields.Count == balance.Fields.Count &&
          File.ReadAllBytes(instance.BalanceTemplateFile).SequenceEqual(balanceTemplate),
        "instance balance saves without changing the shared game template");

    var viewType = typeof(ServerProcess).Assembly.GetType("Briefcase.ServerApp.LinuxGameView")!;
    var prepare = viewType.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic)!;
    var sharedHash = SHA256.HashData(File.ReadAllBytes(instance.Shipping));
    var executable = (string)prepare.Invoke(null, [instance])!;
    Check(File.Exists(executable) &&
          Path.GetFullPath(executable).StartsWith(Path.GetFullPath(instanceRoot) + Path.DirectorySeparatorChar,
              StringComparison.Ordinal), "private Linux game executable created");
    var runtimeIni = Path.Combine(instanceRoot, "GameView", "DeceiveInc", "TripwireServer.ini");
    Check(File.ReadAllText(runtimeIni) ==
          File.ReadAllText(instance.ConfigFile), "game view receives the instance configuration");
    Check(File.ReadAllText(Path.Combine(instanceRoot, "GameView", "DeceiveInc", "CommunityBalanceProfile.json")) ==
          File.ReadAllText(instance.BalanceProfileFile), "game view receives the instance balance");
    Check(File.ReadAllText(Path.Combine(instanceRoot, "CommunityBalanceProfile.default.json")) ==
          File.ReadAllText(instance.BalanceTemplateFile), "instance receives an independent balance template");
    Check(new DirectoryInfo(Path.Combine(instanceRoot, "GameView", "DeceiveInc", "Content")).LinkTarget is not null,
        "shared game assets remain linked");
    var canonicalIni = File.ReadAllText(instance.ConfigFile);
    File.AppendAllText(runtimeIni, "MapRotation=Hardsell\nMapRotation=Hardsell\n");
    Check((string)prepare.Invoke(null, [instance])! == executable &&
          File.ReadAllText(instance.ConfigFile) == canonicalIni &&
          File.ReadAllText(runtimeIni) == canonicalIni,
        "restarting refreshes the game-writable rotation without changing the chosen rotation");
    Check(SHA256.HashData(File.ReadAllBytes(instance.Shipping)).SequenceEqual(sharedHash),
        "preparing the game view does not modify the shared game executable");
}

static void TestLiveServer(string root, string binaries, string installedInstance, string launcherSource,
    string? nativeArchive)
{
    Check(ServerLocation.TryCreate(binaries, out var installation, out _),
        "live game installation is recognized");
    var id = Guid.NewGuid().ToString("N");
    var instanceRoot = ServerInstanceFiles.Provision(Path.Combine(root, "live-server"), id,
        installation!, "Linux View Smoke");
    var native = Path.Combine(AppContext.BaseDirectory, "Native");
    Directory.CreateDirectory(native);
    var launcher = Path.Combine(native, "Briefcase.ServerLauncher");
    File.Copy(launcherSource, launcher, overwrite: true);
    File.SetUnixFileMode(launcher, File.GetUnixFileMode(launcherSource));
    Check(ServerLocation.TryCreate(new ServerRow(id, binaries, true, instanceRoot),
        out var instance, out _), "live instance is recognized");
    if (nativeArchive is null)
        CopyTree(Path.Combine(installedInstance, "Briefcase", "Core"),
            Path.Combine(instanceRoot, "Briefcase", "Core"));
    else
    {
        var install = new ProcessStartInfo(launcher)
        {
            WorkingDirectory = instanceRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var prepareView = typeof(ServerProcess).Assembly.GetType("Briefcase.ServerApp.LinuxGameView")!
            .GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic)!;
        var installExecutable = (string)prepareView.Invoke(null, [instance])!;
        foreach (var part in new[] { "--server", installExecutable, "--instance-root", instanceRoot,
                     "--instance-id", id, "--install-archive", nativeArchive })
            install.ArgumentList.Add(part);
        using var installer = Process.Start(install)!;
        var output = installer.StandardOutput.ReadToEndAsync();
        var errors = installer.StandardError.ReadToEndAsync();
        Check(installer.WaitForExit(30000), "native archive installer completes");
        Check(installer.ExitCode == 0,
            "native archive installs for the instance: " + output.Result + errors.Result);
    }
    var schema = FormSchema.LoadForInstallation(instance!);
    var (values, maps) = ServerIniEditor.Read(instance!.ConfigFile, schema);
    values["ServerName"] = "Linux View Smoke";
    values["GamePort"] = "50990";
    values["QueryPort"] = "50991";
    ServerIniEditor.Save(instance.ConfigFile, values, maps, schema);
    var sharedHash = SHA256.HashData(File.ReadAllBytes(instance.Shipping));
    var log = Path.Combine(instanceRoot, "GameView", "DeceiveInc", "Saved", "Logs", "DeceiveInc.log");
    using var game = ServerProcess.Start(instance);
    try
    {
        Thread.Sleep(TimeSpan.FromSeconds(20));
        Check(!game.HasExited, "live server stays running after startup");
    }
    finally
    {
        ServerProcess.StopAsync(instance, CancellationToken.None).GetAwaiter().GetResult();
        Check(ServerProcess.Find(instance) is null, "live server stops through its supervisor");
    }
    var contents = File.ReadAllText(log);
    Check(contents.Contains("ServerName:Linux View Smoke", StringComparison.Ordinal),
        "live game reads the private instance configuration");
    Check(contents.Contains("MaxPlayers:", StringComparison.Ordinal),
        "live server logged its instance player cap");
    Check(contents.Contains(instance.BalanceProfileFile, StringComparison.Ordinal),
        "live server received its private balance profile");
    var nativeLog = File.ReadAllText(Path.Combine(instanceRoot, "Briefcase", "Logs", "BriefcaseNative.log"));
    Check(!nativeLog.Contains("Administration unavailable", StringComparison.Ordinal),
        "native administration starts for the managed instance: " +
        nativeLog.Split('\n').FirstOrDefault(line => line.Contains("Administration unavailable", StringComparison.Ordinal)));
    Check(File.Exists(Path.Combine(instanceRoot, "Briefcase", "Admin", "server.json")),
        "native administration creates its private instance identity");
    Check(SHA256.HashData(File.ReadAllBytes(instance.Shipping)).SequenceEqual(sharedHash),
        "live launch keeps the Steam game executable unchanged");
}

static void CopyTree(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var path in Directory.EnumerateFiles(source))
        File.Copy(path, Path.Combine(target, Path.GetFileName(path)));
    foreach (var path in Directory.EnumerateDirectories(source))
        CopyTree(path, Path.Combine(target, Path.GetFileName(path)));
}

static async Task TestSocketRetry(string root)
{
    var run = Path.Combine(root, "Run");
    Directory.CreateDirectory(run);
    const int pid = 34901;
    using var live = new ServerLiveClient(pid, new UnixServerSocketTransport(root));
    // The game has not created its socket yet. The client must retry rather
    // than permanently faulting after the initial ENOENT/ECONNREFUSED.
    await Task.Delay(1200);
    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(Path.Combine(run, $"server-{pid}.sock")));
    listener.Listen(1);
    using var client = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
    await using var stream = new NetworkStream(client, ownsSocket: false);
    var line = "{\"schemaVersion\":2,\"type\":\"snapshot\",\"processId\":34901," +
               "\"state\":\"Lobby\",\"map\":\"TestMap\",\"players\":[]}\n";
    await stream.WriteAsync(Encoding.UTF8.GetBytes(line));
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
    while (live.Latest is null && DateTime.UtcNow < deadline) await Task.Delay(50);
    Check(live.Latest?.Map == "TestMap", "Linux live transport reconnects after socket appears");
}
