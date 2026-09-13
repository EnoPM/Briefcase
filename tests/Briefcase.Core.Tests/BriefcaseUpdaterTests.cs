using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Briefcase.ManagedHost;
using Briefcase.Updater;

namespace Briefcase.Core.Tests;

public sealed class BriefcaseUpdaterTests
{
    [Fact]
    public async Task Latest_release_download_is_selected_verified_and_reported()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateReleaseArchive("0.19.0", BriefcasePackageKind.Client);
        var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            return request.RequestUri == GitHubReleaseClient.LatestReleaseUri
                ? JsonResponse(CreateReleaseJson("0.19.0", archive.Length, digest))
                : BytesResponse(archive);
        }));
        using var client = new GitHubReleaseClient(http);
        var progress = new List<BriefcaseUpdateProgress>();

        var update = await client.PrepareLatestAsync(
            new BriefcaseUpdateOptions(
                "0.18.0",
                BriefcasePackageKind.Client,
                directory.Path),
            progress.Add);

        Assert.NotNull(update);
        Assert.Equal(new Version(0, 19, 0), update.Version);
        Assert.True(File.Exists(update.ArchivePath));
        Assert.Equal(2, requests);
        Assert.Contains(progress, item =>
            item.Stage == "Downloading Briefcase 0.19.0" &&
            item.Detail.StartsWith("100%", StringComparison.Ordinal));
        Assert.Equal(0.94, progress[^1].Progress);
    }

    [Fact]
    public async Task Installed_or_older_release_does_not_download()
    {
        using var directory = new TemporaryDirectory();
        var requests = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            return JsonResponse(CreateReleaseJson(
                "0.18.0",
                10,
                new string('a', 64)));
        }));
        using var client = new GitHubReleaseClient(http);

        var update = await client.PrepareLatestAsync(
            new BriefcaseUpdateOptions(
                "0.18.0",
                BriefcasePackageKind.Client,
                directory.Path));

        Assert.Null(update);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Download_with_wrong_github_digest_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var archive = CreateReleaseArchive("0.19.0", BriefcasePackageKind.Client);
        using var http = new HttpClient(new DelegateHandler(request =>
            request.RequestUri == GitHubReleaseClient.LatestReleaseUri
                ? JsonResponse(CreateReleaseJson(
                    "0.19.0",
                    archive.Length,
                    new string('0', 64)))
                : BytesResponse(archive)));
        using var client = new GitHubReleaseClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.PrepareLatestAsync(
                new BriefcaseUpdateOptions(
                    "0.18.0",
                    BriefcasePackageKind.Client,
                    directory.Path)));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_replaces_only_framework_owned_files()
    {
        using var install = new TemporaryDirectory();
        using var source = new TemporaryDirectory();
        var root = install.Path;
        Directory.CreateDirectory(Path.Combine(root, "Briefcase", "Core"));
        Directory.CreateDirectory(Path.Combine(root, "Briefcase", "Mods"));
        File.WriteAllText(Path.Combine(root, "version.dll"), "old proxy");
        File.WriteAllText(Path.Combine(root, "Briefcase", "VERSION"), "0.18.0");
        File.WriteAllText(Path.Combine(root, "Briefcase", "Core", "obsolete.dll"), "old core");
        File.WriteAllText(Path.Combine(root, "Briefcase", "loader.json"), "custom loader");
        File.WriteAllText(Path.Combine(root, "Briefcase", "settings.json"), "custom settings");
        File.WriteAllText(Path.Combine(root, "Briefcase", "Briefcase.log"), "existing log");
        File.WriteAllText(Path.Combine(root, "Briefcase", "Mods", "MyMod.dll"), "private mod");
        File.WriteAllText(Path.Combine(root, "LICENSE"), "old license");

        var archivePath = Path.Combine(source.Path, "update.zip");
        File.WriteAllBytes(
            archivePath,
            CreateReleaseArchive(
                "0.19.0",
                BriefcasePackageKind.Client,
                includeConfigurationDefaults: true));
        var request = new UpdateInstallRequest
        {
            ParentProcessId = 0,
            InstallRoot = root,
            ArchivePath = archivePath,
            Version = "0.19.0",
            PackageKind = nameof(BriefcasePackageKind.Client),
            RestartMode = "executable",
            RestartExecutable = Path.Combine(root, "DeceiveInc-Win64-Shipping.exe"),
            Restart = false
        };

        UpdateInstallerEngine.Apply(request, TextWriter.Null);

        Assert.Equal("new proxy", File.ReadAllText(Path.Combine(root, "version.dll")));
        Assert.Equal("0.19.0", File.ReadAllText(Path.Combine(root, "Briefcase", "VERSION")));
        Assert.Equal(
            "new managed host",
            File.ReadAllText(Path.Combine(
                root, "Briefcase", "Core", "Briefcase.ManagedHost.dll")));
        Assert.False(File.Exists(Path.Combine(root, "Briefcase", "Core", "obsolete.dll")));
        Assert.Equal(
            "custom loader",
            File.ReadAllText(Path.Combine(root, "Briefcase", "loader.json")));
        Assert.Equal(
            "custom settings",
            File.ReadAllText(Path.Combine(root, "Briefcase", "settings.json")));
        Assert.Equal(
            "existing log",
            File.ReadAllText(Path.Combine(root, "Briefcase", "Briefcase.log")));
        Assert.Equal(
            "private mod",
            File.ReadAllText(Path.Combine(root, "Briefcase", "Mods", "MyMod.dll")));
        Assert.Equal("new license", File.ReadAllText(Path.Combine(root, "LICENSE")));
        Assert.Empty(Directory.GetDirectories(root, ".briefcase-update-*"));
    }

    [Fact]
    public void Archive_path_traversal_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../outside.dll", "unsafe");
        }

        Assert.Throws<InvalidDataException>(() =>
            ReleaseArchiveValidator.Validate(
                archivePath, "0.19.0", BriefcasePackageKind.Client));
    }

    [Theory]
    [InlineData("{}", true, "auto", true)]
    [InlineData("{\"automaticUpdates\":false,\"updateRestartMode\":\"executable\",\"automaticModUpdates\":false}", false, "executable", false)]
    [InlineData("{\"automaticUpdates\":true,\"updateRestartMode\":\"steam\",\"automaticModUpdates\":true}", true, "steam", true)]
    public void Update_settings_are_read_from_loader_json(
        string json,
        bool enabled,
        string restartMode,
        bool automaticModUpdates)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, json);
        var warnings = new List<string>();

        var settings = FrameworkUpdateSettingsReader.Read(path, warnings.Add);

        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal(restartMode, settings.RestartMode);
        Assert.Equal(automaticModUpdates, settings.AutomaticModUpdates);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("0.18.0", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-beta", false)]
    [InlineData("one.two.three", false)]
    public void Release_versions_have_exactly_three_numeric_components(
        string value,
        bool valid)
    {
        Assert.Equal(valid, ReleaseVersion.TryParse(value, out _, out _));
    }

    [Fact]
    public void Automatic_client_restart_recognizes_a_Steam_installation()
    {
        var restart = UpdateInstallerLauncher.ResolveRestart(
            BriefcasePackageKind.Client,
            "auto",
            @"C:\Games\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64");

        Assert.Equal("steam", restart.Mode);
    }

    [Fact]
    public void Dedicated_server_restart_always_uses_its_executable()
    {
        var restart = UpdateInstallerLauncher.ResolveRestart(
            BriefcasePackageKind.Server,
            "steam");

        Assert.Equal("executable", restart.Mode);
        Assert.Null(restart.SteamAppId);
    }
    private static byte[] CreateReleaseArchive(
        string version,
        BriefcasePackageKind packageKind,
        bool includeConfigurationDefaults = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "version.dll", "new proxy");
            WriteEntry(archive, "Briefcase/VERSION", version);
            WriteEntry(
                archive,
                "Briefcase/Core/Briefcase.ManagedHost.dll",
                "new managed host");
            WriteEntry(
                archive,
                "Briefcase/Core/Briefcase.Updater.dll",
                "new update library");
            WriteEntry(
                archive,
                "Briefcase/Core/Native/Briefcase.UnrealRuntime.dll",
                "new native runtime");
            WriteEntry(
                archive,
                "Briefcase/Core/Updater/Briefcase.UpdateInstaller.exe",
                "new installer");
            if (packageKind == BriefcasePackageKind.Client)
                WriteEntry(
                    archive,
                    "Briefcase/Core/Ui/Avalonia/Briefcase.AvaloniaUi.dll",
                    "new ui");
            else
                WriteEntry(archive, "StartBriefcaseServer.bat", "new server script");
            WriteEntry(archive, "LICENSE", "new license");
            WriteEntry(archive, "README-Briefcase.txt", "new readme");
            if (includeConfigurationDefaults)
                WriteEntry(archive, "Briefcase/loader.json", "release default loader");
        }
        return stream.ToArray();
    }

    private static string CreateReleaseJson(
        string version,
        long size,
        string digest) =>
        JsonSerializer.Serialize(new
        {
            tag_name = $"v{version}",
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new
                {
                    name = $"Briefcase-Client-v{version}.zip",
                    size,
                    digest = $"sha256:{digest}",
                    browser_download_url =
                        $"https://github.com/EnoPM/Briefcase/releases/download/v{version}/Briefcase-Client-v{version}.zip"
                }
            }
        });

    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BytesResponse(byte[] value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(value)
    };

    private static void WriteEntry(ZipArchive archive, string path, string contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(
            entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
