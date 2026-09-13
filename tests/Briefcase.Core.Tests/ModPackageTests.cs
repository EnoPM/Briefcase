using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Briefcase.ModPackages;

namespace Briefcase.Core.Tests;

public sealed class ModPackageTests
{
    [Fact]
    public void Repository_marketplace_catalogue_is_valid()
    {
        var path = Path.Combine(RepositoryPaths.Root, "marketplace", "catalog.json");
        var catalogue = MarketplaceCatalogClient.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, catalogue.SchemaVersion);
    }

    [Fact]
    public async Task Marketplace_refresh_keeps_the_last_valid_catalogue_when_GitHub_is_unavailable()
    {
        using var root = new TemporaryDirectory();
        var cache = Path.Combine(root.Path, "catalog.json");
        await File.WriteAllTextAsync(cache, """
        {
          "schemaVersion": 1,
          "mods": []
        }
        """);
        using var http = new HttpClient(new AlwaysFailHandler());
        using var client = new MarketplaceCatalogClient(
            http, new Uri("https://example.invalid/catalog.json"));

        var catalogue = await client.GetAsync(cache, forceRefresh: true);

        Assert.Empty(catalogue.Mods);
    }
    [Fact]
    public void Legacy_flat_mod_is_migrated_to_a_manifest_directory()
    {
        using var root = new TemporaryDirectory();
        var assembly = Path.Combine(root.Path, "Example.Mod.dll");
        File.WriteAllText(assembly, "assembly");
        File.WriteAllText(Path.Combine(root.Path, "Example.Mod.pdb"), "symbols");

        var count = ModPackageDiscovery.MigrateLegacyMods(root.Path);
        var packages = ModPackageDiscovery.Discover(root.Path);

        Assert.Equal(1, count);
        var package = Assert.Single(packages);
        Assert.Equal("Example.Mod.dll", package.Manifest.EntryAssembly);
        Assert.True(File.Exists(package.EntryAssemblyPath));
        Assert.True(File.Exists(Path.Combine(package.DirectoryPath, "Example.Mod.pdb")));
        Assert.False(File.Exists(assembly));
    }

    [Fact]
    public void Legacy_migration_does_not_overwrite_an_unrelated_package_directory()
    {
        using var root = new TemporaryDirectory();
        var occupied = Path.Combine(root.Path, "Example.Mod");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "Existing.dll"), "existing");
        ModPackageManifestFile.WriteMinimal(
            Path.Combine(occupied, ModPackageManifest.FileName), "Existing.dll");
        File.WriteAllText(Path.Combine(root.Path, "Example.Mod.dll"), "legacy");

        ModPackageDiscovery.MigrateLegacyMods(root.Path);
        var packages = ModPackageDiscovery.Discover(root.Path);

        Assert.Equal(2, packages.Count);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(occupied, "Existing.dll")));
        Assert.Contains(packages, package =>
            package.DirectoryPath.EndsWith("Example.Mod-legacy-1", StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_rejects_an_update_source_without_identity()
    {
        var manifest = new ModPackageManifest
        {
            EntryAssembly = "Example.dll",
            Updates = new GitHubModSource
            {
                Repository = "Briefcase/Example",
                Asset = "Example-v{version}.zip"
            }
        };

        Assert.Throws<InvalidDataException>(() =>
            ModPackageManifestFile.Validate(manifest));
    }

    [Fact]
    public void Marketplace_catalogue_rejects_duplicate_ids()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "mods": [
            {
              "id": "briefcase.example",
              "name": "Example",
              "author": "Author",
              "description": "First",
              "repository": "Briefcase/Example",
              "asset": "Example-v{version}.zip",
              "target": "client",
              "dependencies": []
            },
            {
              "id": "briefcase.example",
              "name": "Duplicate",
              "author": "Author",
              "description": "Second",
              "repository": "Briefcase/Example2",
              "asset": "Example2-v{version}.zip",
              "target": "client",
              "dependencies": []
            }
          ]
        }
        """;

        Assert.Throws<InvalidDataException>(() =>
            MarketplaceCatalogClient.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-2", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    public void Package_versions_follow_semantic_prerelease_order(
        string olderText,
        string newerText)
    {
        Assert.True(Parse(olderText) < Parse(newerText));
    }

    [Fact]
    public void Installer_rejects_dependencies_that_differ_from_the_catalogue()
    {
        using var root = new TemporaryDirectory();
        var prepared = CreatePreparedPackage(root.Path, "1.1.0") with
        {
            ExpectedDependencies = ["briefcase.required"]
        };

        Assert.Throws<InvalidDataException>(() =>
            ModPackageInstaller.Install(prepared, Path.Combine(root.Path, "Mods")));
    }

    [Fact]
    public void Installer_preserves_the_reserved_data_directory()
    {
        using var root = new TemporaryDirectory();
        var mods = Path.Combine(root.Path, "Mods");
        var existing = Path.Combine(mods, "briefcase.example");
        Directory.CreateDirectory(Path.Combine(existing, "Data"));
        File.WriteAllText(Path.Combine(existing, "Data", "settings.db"), "persistent");
        File.WriteAllText(Path.Combine(existing, "old.dll"), "old");
        var prepared = CreatePreparedPackage(root.Path, "1.1.0");

        var installed = ModPackageInstaller.Install(prepared, mods, existing);

        Assert.Equal("new assembly", File.ReadAllText(installed.EntryAssemblyPath));
        Assert.Equal("persistent", File.ReadAllText(
            Path.Combine(installed.DirectoryPath, "Data", "settings.db")));
        Assert.False(File.Exists(Path.Combine(installed.DirectoryPath, "old.dll")));
    }

    [Fact]
    public void Installer_rejects_archive_path_traversal()
    {
        using var root = new TemporaryDirectory();
        var staging = Path.Combine(root.Path, "staging");
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, "Example-v1.1.0.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../outside.dll", "unsafe");
            WriteEntry(archive, ModPackageManifest.FileName,
                ManifestJson("1.1.0"));
        }
        var source = Source();
        var prepared = new PreparedModPackage(
            "briefcase.example", source, Parse("1.1.0"), archivePath, staging);

        Assert.Throws<InvalidDataException>(() =>
            ModPackageInstaller.Install(prepared, Path.Combine(root.Path, "Mods")));
        Assert.False(File.Exists(Path.Combine(root.Path, "outside.dll")));
    }

    [Fact]
    public async Task Automatic_update_uses_release_digest_and_replaces_package()
    {
        using var root = new TemporaryDirectory();
        var mods = Path.Combine(root.Path, "Mods");
        var packageDirectory = Path.Combine(mods, "briefcase.example");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "Example.dll"), "old assembly");
        File.WriteAllText(Path.Combine(packageDirectory, ModPackageManifest.FileName),
            ManifestJson("1.0.0"));
        var package = Assert.Single(ModPackageDiscovery.Discover(mods));
        var archiveBytes = CreateArchiveBytes("1.1.0");
        var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var handler = new ReleaseHandler(archiveBytes, hash);
        using var http = new HttpClient(handler);
        using var releases = new GitHubModReleaseClient(http);
        using var updates = new ModUpdateService(releases);

        var results = await updates.UpdateInstalledAsync(
            [package], mods, Path.Combine(root.Path, "staging"));

        var result = Assert.Single(results);
        Assert.True(result.Updated);
        var updated = Assert.Single(ModPackageDiscovery.Discover(mods));
        Assert.Equal("1.1.0", updated.Manifest.Version);
        Assert.Equal("new assembly", File.ReadAllText(updated.EntryAssemblyPath));
    }

    private static PreparedModPackage CreatePreparedPackage(string root, string version)
    {
        var staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, $"Example-v{version}.zip");
        File.WriteAllBytes(archivePath, CreateArchiveBytes(version));
        return new PreparedModPackage(
            "briefcase.example", Source(), Parse(version), archivePath, staging);
    }

    private static byte[] CreateArchiveBytes(string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ModPackageManifest.FileName, ManifestJson(version));
            WriteEntry(archive, "Example.dll", "new assembly");
        }
        return stream.ToArray();
    }

    private static string ManifestJson(string version) => $$"""
    {
      "schemaVersion": 1,
      "entryAssembly": "Example.dll",
      "id": "briefcase.example",
      "version": "{{version}}",
      "dependencies": [],
      "updates": {
        "repository": "Briefcase/Example",
        "asset": "Example-v{version}.zip",
        "automatic": true
      }
    }
    """;

    private static GitHubModSource Source() => new()
    {
        Repository = "Briefcase/Example",
        Asset = "Example-v{version}.zip"
    };

    private static PackageVersion Parse(string value)
    {
        Assert.True(PackageVersion.TryParse(value, out var version));
        return version;
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private sealed class ReleaseHandler(byte[] archive, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                var json = $$"""
                {
                  "draft": false,
                  "prerelease": false,
                  "tag_name": "v1.1.0",
                  "assets": [
                    {
                      "name": "Example-v1.1.0.zip",
                      "size": {{archive.Length}},
                      "digest": "sha256:{{hash}}",
                      "browser_download_url": "https://github.com/Briefcase/Example/releases/download/v1.1.0/Example-v1.1.0.zip"
                    }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            });
        }
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
