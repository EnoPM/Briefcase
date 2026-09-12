using System.Text.Json;

namespace Briefcase.ServerBrowser.Client;

internal sealed class ServerDirectory
{
    public int SchemaVersion { get; set; } = 2;
    public string SelectedServerId { get; set; } = "";
    public List<SavedServer> Servers { get; set; } = [];
}

internal sealed class SavedServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New server";
    public string Endpoint { get; set; } = "127.0.0.1:50000";
    public string Password { get; set; } = "";
    public string AdministrationEndpoint { get; set; } = "127.0.0.1:47000";
    public string AdministrationPassword { get; set; } = "";
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
            var sourceSchemaVersion = result.SchemaVersion;
            result.SchemaVersion = 2;
            result.Servers ??= [];
            result.Servers = result.Servers
                .Where(server => server is not null)
                .Select(server => Normalize(server, sourceSchemaVersion))
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
        var administrationEndpoint = "127.0.0.1:47000";
        var administrationPassword = "";
        try
        {
            if (File.Exists(frameworkSettingsPath))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(frameworkSettingsPath));
                if (settings.RootElement.TryGetProperty("Mods", out var mods))
                {
                    if (TryGetValues(mods, "briefcase.startup-automation", out var startup))
                    {
                        endpoint = ReadString(
                            startup, "Community server/Server endpoint", endpoint);
                        password = ReadString(
                            startup, "Community server/Server password", password);
                    }
                    if (TryGetValues(mods, "server-admin-control.client", out var administration))
                    {
                        administrationEndpoint = ReadString(
                            administration, "Briefcase server/Endpoint",
                            administrationEndpoint);
                        administrationPassword = ReadString(
                            administration, "Briefcase server/Administration password",
                            administrationPassword);
                    }
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }

        var server = new SavedServer
        {
            Name = "Community server",
            Endpoint = endpoint,
            Password = password,
            AdministrationEndpoint = administrationEndpoint,
            AdministrationPassword = administrationPassword
        };
        return new ServerDirectory
        {
            SelectedServerId = server.Id,
            Servers = [server]
        };
    }

    private static bool TryGetValues(
        JsonElement mods,
        string modId,
        out JsonElement values)
    {
        values = default;
        return mods.TryGetProperty(modId, out var mod) &&
               mod.TryGetProperty("Values", out values);
    }

    private static string ReadString(
        JsonElement values,
        string name,
        string fallback) =>
        values.TryGetProperty(name, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    private static SavedServer Normalize(SavedServer server, int sourceSchemaVersion)
    {
        server.Id = string.IsNullOrWhiteSpace(server.Id)
            ? Guid.NewGuid().ToString("N")
            : server.Id.Trim();
        server.Name = string.IsNullOrWhiteSpace(server.Name)
            ? "Unnamed server"
            : server.Name.Trim();
        server.Endpoint = server.Endpoint?.Trim() ?? "";
        server.Password ??= "";
        server.AdministrationEndpoint = sourceSchemaVersion < 2 ||
                                        string.IsNullOrWhiteSpace(server.AdministrationEndpoint)
            ? DefaultAdministrationEndpoint(server.Endpoint)
            : server.AdministrationEndpoint.Trim();
        server.AdministrationPassword ??= "";
        return server;
    }

    private static string DefaultAdministrationEndpoint(string gameEndpoint)
    {
        var endpoint = gameEndpoint?.Trim() ?? "";
        if (endpoint.Length == 0) return "127.0.0.1:47000";
        var separator = endpoint.LastIndexOf(':');
        var host = separator > 0 ? endpoint[..separator] : endpoint;
        return $"{host}:47000";
    }
}
