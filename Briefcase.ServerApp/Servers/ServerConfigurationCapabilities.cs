namespace Briefcase.ServerApp;

public static class ServerConfigurationCapabilities
{
    public const int VanillaMaxPlayers = 16;

    public static int MaxPlayersMaximum(ServerLocation location) =>
        ModCatalog.Read(location)
            .Where(mod => mod.IsEnabled && mod.HasNativeBinary)
            .Select(mod => ModMarketplace.InstalledMetadata(location, mod.Name)?.MaxPlayersMaximum ?? VanillaMaxPlayers)
            .DefaultIfEmpty(VanillaMaxPlayers)
            .Max();
}
