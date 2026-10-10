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
      "formSchemaOverrideUrl": "https://example.com/assets/player-cap-form.json",
      "formSchemaOverrideSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
      "formTranslations": {
        "en": { "url": "https://example.com/assets/player-cap.en.json", "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" }
      },
      "documentationUrl": "https://example.com/player-cap",
      "discordUrl": "https://discord.gg/example",
      "homepageUrl": "https://example.com"
    }
  ]
}
```

The example URLs and hashes are placeholders. Use the SHA-256 digest of the exact published archive and PNG. All URLs must use HTTPS. `logoUrl` and `logoSha256` are optional, but must be provided together. The documentation, Discord, and homepage URLs are optional. IDs can contain lowercase ASCII letters, digits, hyphens, underscores, and periods, with no spaces. Versions currently use `MAJOR.MINOR.PATCH`.

`formSchemaOverrideUrl` and `formSchemaOverrideSha256` optionally provide a verified JSON override for the application's server configuration form. `formTranslations` optionally provides verified JSON language files. They are installed inside the mod's `Data` folder and are used only while the mod is enabled. A mod archive may also contain these files directly; the catalog sidecars let an existing mod release gain a form override without repackaging its native DLL. See [Form schemas](FormSchemas.md) for the layout, validation rules, references and translation fallback.

The official PlayerCap entry temporarily also retains the older `serverConfiguration.maxPlayersMaximum` property so previously built ServerApp versions keep their player-limit control. New form-schema builds use the JSON override instead.

ServerApp downloads and validates each catalog, caches its last valid copy, and revalidates with ETag or Last-Modified when available. A cached catalog can still be shown if the source is temporarily offline. Archive hashes are checked before files are installed. A downloaded logo is shown only after its hash matches the manifest. ServerApp stores the catalog URL and installed version beside each installed mod to discover future updates; updates are applied only while the server is stopped, including just before ServerApp starts it.

Legacy `schemaVersion: 1` catalogs remain readable for existing installations. New catalogs should use version 2 and the lowercase folder ID convention.
