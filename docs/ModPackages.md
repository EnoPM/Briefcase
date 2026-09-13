# Mod packages and marketplace

Briefcase installs each external mod in its own directory:

```text
Briefcase/Mods/briefcase.example-mod/
├── briefcase.mod.json
├── Briefcase.ExampleMod.dll
└── Data/
```

`Data` is reserved for persistent files created by the mod. Briefcase preserves
it when an automatic update replaces the packaged files. Settings declared with
`Configuration.Bind` remain in `Briefcase/settings.json` and are also preserved.

Old DLLs found directly in `Briefcase/Mods` are migrated automatically into a
folder with a minimal manifest. They continue to support hot reload, but they
cannot update automatically until their manifest declares a GitHub source.

## Manifest

A release package contains `briefcase.mod.json` at the root of its ZIP:

```json
{
  "schemaVersion": 1,
  "entryAssembly": "Briefcase.ExampleMod.dll",
  "id": "briefcase.example-mod",
  "version": "1.2.0",
  "dependencies": [],
  "updates": {
    "repository": "Briefcase/ExampleMod",
    "asset": "Briefcase.ExampleMod-v{version}.zip",
    "automatic": true
  }
}
```

`id`, `version`, and `dependencies` must agree with the mod's `ModInfo`. The
`{version}` token is replaced with the latest stable GitHub release version.
For example, tag `v1.2.0` selects `Briefcase.ExampleMod-v1.2.0.zip`.

The release asset must expose GitHub's SHA-256 digest. Briefcase checks its URL,
size, digest, archive paths, manifest identity, version, and entry assembly
before installing it. Draft and prerelease releases are ignored.

Automatic mod updates run before any external mod assembly is loaded. A failed
check is logged and does not prevent the installed version from loading. Set
`automaticModUpdates` to `false` in `Briefcase/loader.json` to disable these
checks globally.

## Marketplace catalogue

The client reads the curated catalogue from
[`marketplace/catalog.json`](https://github.com/EnoPM/Briefcase/blob/main/marketplace/catalog.json).
The catalogue contains discovery metadata and the same GitHub release source:

```json
{
  "schemaVersion": 1,
  "mods": [
    {
      "id": "briefcase.example-mod",
      "name": "Example Mod",
      "author": "Author",
      "description": "A short player-facing description.",
      "repository": "Briefcase/ExampleMod",
      "asset": "Briefcase.ExampleMod-v{version}.zip",
      "target": "client",
      "homepage": "https://github.com/Briefcase/ExampleMod",
      "dependencies": []
    }
  ]
}
```

Valid targets are `client`, `server`, and `both`. The in-game Marketplace shows
client-compatible entries and installs missing catalogue dependencies before the
selected mod. The last valid catalogue is cached so it remains browsable during
a temporary GitHub outage.

Publishing a catalogue entry does not copy a mod into Briefcase. The mod stays
in its own GitHub repository, owns its releases, and can ship updates
independently from the framework.
