using Briefcase.ServerBrowser.Client;

namespace Briefcase.Core.Tests;

public sealed class ServerDirectoryTests
{
    [Fact]
    public void Version_one_directory_derives_administration_endpoint_from_game_host()
    {
        using var directory = new TemporaryDirectory();
        var directoryPath = Path.Combine(directory.Path, "servers.json");
        File.WriteAllText(directoryPath,
            """
            {
              "SchemaVersion": 1,
              "SelectedServerId": "saved",
              "Servers": [
                {
                  "Id": "saved",
                  "Name": "Remote server",
                  "Endpoint": "example.test:50000",
                  "Password": "game-secret"
                }
              ]
            }
            """);

        var store = new ServerDirectoryStore(
            directoryPath,
            Path.Combine(directory.Path, "settings.json"));
        var loaded = store.Load();

        Assert.Equal(2, loaded.SchemaVersion);
        var server = Assert.Single(loaded.Servers);
        Assert.Equal("example.test:47000", server.AdministrationEndpoint);
        Assert.Equal("", server.AdministrationPassword);
    }

    [Fact]
    public void Initial_directory_migrates_game_and_administration_settings()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath,
            """
            {
              "Mods": {
                "briefcase.startup-automation": {
                  "Values": {
                    "Community server/Server endpoint": "game.test:50000",
                    "Community server/Server password": "game-secret"
                  }
                },
                "server-admin-control.client": {
                  "Values": {
                    "Briefcase server/Endpoint": "admin.test:47001",
                    "Briefcase server/Administration password": "admin-secret"
                  }
                }
              }
            }
            """);

        var store = new ServerDirectoryStore(
            Path.Combine(directory.Path, "servers.json"),
            settingsPath);
        var loaded = store.Load();

        Assert.Equal(2, loaded.SchemaVersion);
        var server = Assert.Single(loaded.Servers);
        Assert.Equal("game.test:50000", server.Endpoint);
        Assert.Equal("game-secret", server.Password);
        Assert.Equal("admin.test:47001", server.AdministrationEndpoint);
        Assert.Equal("admin-secret", server.AdministrationPassword);
    }
}
