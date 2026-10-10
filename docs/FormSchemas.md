# JSON form schemas

The server configuration form is defined by `Briefcase.ServerApp/Schemas/ServerConfiguration.json`. The build copies this file to `Schemas/ServerConfiguration.json` beside the executable and embeds a fallback. It is included in a normal application release. Changes to this file take effect when the configuration view reloads; malformed files are reported rather than silently ignored.

The format is independent of INI storage. It defines ordered `tabs`, `groups` within tabs, and `fields` within groups. Labels have English fallbacks and optional `labelKey` entries; by default the translation keys are `config.tab.<id>`, `config.category.<id>`, and `config.field.<key>`. This allows the same form schema engine to be used for mod settings and balancing in the future.

```json
{
  "schemaVersion": 1,
  "section": "/Script/DeceiveInc.TripwireServerSettings",
  "tabs": [{ "id": "GameMode", "label": "Game mode" }],
  "groups": [{ "id": "Game", "tab": "GameMode", "label": "Game" }],
  "fields": [
    { "key": "MaxPlayers", "group": "Game", "label": "Maximum players", "kind": "integer",
      "default": "8", "min": 1, "max": 16, "display": "slider", "step": 1 },
    { "key": "BotsAmount", "group": "Game", "label": "Bot count", "kind": "integer",
      "default": "0", "min": 0, "maxReference": "MaxPlayers", "display": "slider", "step": 1 }
  ]
}
```

Supported field kinds are `text`, `secret`, `integer`, `decimal`, `boolean`, and `choice` (`choices` is required). Choices can be strings or `{ "value": "A", "label": "Option A", "labelKey": "mod.option.a" }` objects. The UI translates the label but stores the stable value. A numeric field can set `min`, `max`, and `maxReference`. The effective upper bound is the lesser of `max` and the current value of `maxReference`. Other validation properties are `maxLength`, `pattern` (regular expression with a timeout), and `notEqualTo` (another field key). `display: "slider"` is available for integer and decimal fields; `step` sets its increments and is validated on save (for example, `0.25`). The optional `section` on a field places it in a different INI section. Unknown INI settings are preserved when the form saves.

An enabled UE4SS mod can add or change only the parts it needs by supplying `Data/server-configuration.override.json` in its mod folder. Arrays are merged by tab `id`, group `id`, and field `key`; existing entries accept partial changes, while new entries need their full definition. For example, PlayerCap changes just the upper bound:

```json
{ "schemaVersion": 1, "fields": [{ "key": "MaxPlayers", "max": 30 }] }
```

The mod can supply `Data/FormTranslations/en.json`, `fr.json`, and other language files as flat JSON dictionaries. The selected language wins over the mod's English translation, followed by the schema label. A catalog can distribute these files with HTTPS URLs and SHA-256 hashes, or they can be inside the mod archive. They are loaded only when that mod is enabled. Example:

```json
{ "config.field.MaxPlayers": "Nombre maximal de joueurs (PlayerCap)" }
```

The server INI editor is the first consumer of this schema. Mod configuration and balancing can adopt the same tab/group/field and validation model without adopting INI persistence.

For compatibility, already-installed mods that recorded the previous `serverConfiguration.maxPlayersMaximum` metadata keep that limit until they are reinstalled with a JSON override. New mods should use only the JSON override.
