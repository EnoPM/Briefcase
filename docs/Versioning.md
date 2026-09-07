# Versioning

Briefcase uses three numeric version components. The plain-text `VERSION` file
contains the current version, such as `0.8.0`, and nothing else.

The public framework version, native API versions and game build identity are
separate compatibility axes:

- `VERSION` is the single source for managed assembly versions, the version
  reported by the native API, installed package metadata, tags, and archive
  names;
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

Pull requests should be reviewed and merged into an integration branch before
publication. Run the **Create Version** workflow manually from GitHub Actions.
Its form asks for that source branch and one increment:

- `build`: `1.2.3` becomes `1.2.4`; this is the Semantic Versioning patch;
- `minor`: `1.2.3` becomes `1.3.0`;
- `major`: `1.2.3` becomes `2.0.0`.

The source branch must be a remote branch in the Briefcase repository and must
still contain the same `VERSION` as `main`. The workflow fetches an exact
snapshot of both branches, merges the source into `main` locally, and writes the
new `VERSION` into that merge commit. It then creates the tag and pushes `main`
and the integration branch atomically before calling the reusable release
workflow. The source branch is advanced to the same release commit, so it
already contains the new version when it receives the next reviewed pull
requests.

Repository Actions settings must allow workflows to write repository contents.
If `main` is protected, its ruleset must explicitly allow this workflow or the
GitHub Actions bot to update it. Otherwise the atomic push is rejected and
neither `main` nor the release tag is changed.

Build and inspect the same archives locally at any time with:

```powershell
./scripts/release/PrepareRelease.ps1 -Configuration Release
```

The reusable **Release** workflow checks out the new tag, verifies it against
`VERSION`, rebuilds both targets, and publishes these assets:

- `Briefcase-Client-v<VERSION>.zip`
- `Briefcase-Server-v<VERSION>.zip`

Each ZIP is ready to extract directly into the matching
`DeceiveInc/Binaries/Win64` directory. Release packaging strips PDB files,
rejects bundled user mods, and verifies that the server archive contains no
client rendering libraries.
