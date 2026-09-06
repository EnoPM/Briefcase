using System.Text.Json;

namespace Briefcase.ServerBrowser.Client;

internal sealed class ServerDirectory
{
    public int SchemaVersion { get; set; } = 1;
    public string SelectedServerId { get; set; } = "";
    public List<SavedServer> Servers { get; set; } = [];
}

internal sealed class SavedServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New server";
    public string Endpoint { get; set; } = "127.0.0.1:50000";
    public string Password { get; set; } = "";
}

internal sealed class ServerDirectoryStore(string path, string frameworkSettingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public ServerDirectory Load()
    {
        if (!File.Exists(path)) return CreateInitialDirectory();
        try
        {
            var result = JsonSerializer.Deserialize<ServerDirectory>(
                             File.ReadAllText(path), JsonOptions)
                         ?? new ServerDirectory();
            result.Servers ??= [];
            result.Servers = result.Servers
                .Where(server => server is not null)
                .Select(Normalize)
                .GroupBy(server => server.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (!result.Servers.Any(server => string.Equals(
                    server.Id, result.SelectedServerId,
                    StringComparison.OrdinalIgnoreCase)))
                result.SelectedServerId = result.Servers.FirstOrDefault()?.Id ?? "";
            return result;
        }
        catch (JsonException)
        {
            return CreateInitialDirectory();
        }
        catch (IOException)
        {
            return CreateInitialDirectory();
        }
    }

    public void Save(ServerDirectory directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(directory, JsonOptions);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
    }

    private ServerDirectory CreateInitialDirectory()
    {
        var endpoint = "127.0.0.1:50000";
        var password = "";
        try
        {
            if (File.Exists(frameworkSettingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(frameworkSettingsPath));
                if (settings.RootElement.TryGetProperty("Mods", out var mods) &&
                    mods.TryGetProperty("briefcase.startup-automation", out var startup) &&
                    startup.TryGetProperty("Values", out var values))
                {
                    if (values.TryGetProperty(
                            "Community server/Server endpoint", out var endpointValue))
                        endpoint = endpointValue.GetString() ?? endpoint;
                    if (values.TryGetProperty(
                            "Community server/Server password", out var passwordValue))
                        password = passwordValue.GetString() ?? password;
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        var server = new SavedServer
        {
            Name = "Community server",
            Endpoint = endpoint,
            Password = password
        };
        return new ServerDirectory
        {
            SelectedServerId = server.Id,
            Servers = [server]
        };
    }

    private static SavedServer Normalize(SavedServer server)
    {
        server.Id = string.IsNullOrWhiteSpace(server.Id)
            ? Guid.NewGuid().ToString("N")
            : server.Id.Trim();
        server.Name = string.IsNullOrWhiteSpace(server.Name)
            ? "Unnamed server"
            : server.Name.Trim();
        server.Endpoint = server.Endpoint?.Trim() ?? "";
        server.Password ??= "";
        return server;
    }
}
