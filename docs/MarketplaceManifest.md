# Marketplace catalog format

ServerApp accepts an HTTPS URL to a JSON catalog. Version 2 entries use a stable lowercase `id`; that ID is also the directory name under `ue4ss/Mods`. The archive must contain `ue4ss/Mods/<id>/dlls/main.dll` and may contain that mod's configuration, assets, and `logo.png`. ServerApp preserves an existing `Data/config.json` when updating a mod.

```json
{
  "schemaVersion": 2,
  "mods": [
    {
      "id": "player-cap",
      "name": "Player Cap",
      "version": "1.2.3",
      "description": "Adjust the maximum number of players.",
      "author": "EnoPM",
      "downloadUrl": "https://example.com/releases/player-cap-1.2.3.zip",
      "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "logoUrl": "https://example.com/assets/player-cap.png",
      "logoSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      "serverConfiguration": { "maxPlayersMaximum": 30 },
      "documentationUrl": "https://example.com/player-cap",
      "discordUrl": "https://discord.gg/example",
      "homepageUrl": "https://example.com"
    }
  ]
}
```

The example URLs and hashes are placeholders. Use the SHA-256 digest of the exact published archive and PNG. All URLs must use HTTPS. `logoUrl` and `logoSha256` are optional, but must be provided together. The documentation, Discord, and homepage URLs are optional. IDs can contain lowercase ASCII letters, digits, hyphens, underscores, and periods, with no spaces. Versions currently use `MAJOR.MINOR.PATCH`.

`serverConfiguration.maxPlayersMaximum` is an optional server configuration capability. It accepts 17–32. When a catalog mod declaring it is installed and enabled, the server configuration allows that many players (and bots, up to the selected player count). Otherwise the application keeps the built-in limit of 16. The capability is saved with the installed mod, so it works while offline. The current PlayerCap release reads the selected player count from `TripwireServer.ini`.

ServerApp downloads and validates each catalog, caches its last valid copy, and revalidates with ETag or Last-Modified when available. A cached catalog can still be shown if the source is temporarily offline. Archive hashes are checked before files are installed. A downloaded logo is shown only after its hash matches the manifest. ServerApp stores the catalog URL and installed version beside each installed mod to discover future updates; updates are applied only while the server is stopped, including just before ServerApp starts it.

Legacy `schemaVersion: 1` catalogs remain readable for existing installations. New catalogs should use version 2 and the lowercase folder ID convention.
