# Versioning

Briefcase uses Semantic Versioning for framework releases. The current development version is `0.8.0`. A release produced from this
revision uses tag `v0.8.0` for both the portable client and server packages.

The public framework version, native API versions and game build identity are
separate compatibility axes:

- `VERSION` is the public Briefcase release version.
- the integer API constants in `Briefcase.ModApi` change only when their binary
  contracts change;
- a generated SDK directory is keyed by the executable PE timestamp and image
  size, such as `6A96564B-06283000`;
- every mod owns its own Semantic Versioning lifecycle.

A mod release should record its required Briefcase version range, target
(`Client`, `Server`, or `Hybrid`) and supported game build. A framework patch
release keeps existing APIs compatible. A framework minor release may add APIs.
A framework major release may remove or change public contracts.

Generated SDK assemblies are build artifacts. Their game-build identity and
metadata schema version are authoritative; they do not receive an independent
manually maintained Semantic Versioning number.

## Publishing a release

1. Update `VERSION` and commit the complete release revision.
2. Build and inspect both archives locally with
   `./scripts/release/PrepareRelease.ps1 -Configuration Release`.
3. Create and push the exact matching tag, for example `v0.8.0` when `VERSION`
   contains `0.8.0`.

The `Release` GitHub Actions workflow checks out that tag, verifies the version,
rebuilds both targets, and publishes these assets:

- `Briefcase-Client-v<VERSION>.zip`
- `Briefcase-Server-v<VERSION>.zip`

Each ZIP is ready to extract directly into the matching
`DeceiveInc/Binaries/Win64` directory. Release packaging strips PDB files,
rejects bundled user mods, and verifies that the server archive contains no
client rendering libraries.
