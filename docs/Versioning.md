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

Run the **Create Version** workflow manually from GitHub Actions. Its form asks
for an open pull request number and one increment:

- `build`: `1.2.3` becomes `1.2.4`; this is the Semantic Versioning patch;
- `minor`: `1.2.3` becomes `1.3.0`;
- `major`: `1.2.3` becomes `2.0.0`.

The selected pull request must target `main`, must not be a draft, and must use a
branch in the Briefcase repository. The workflow calculates the next version
from `main`, commits `VERSION` to the pull request, waits for required checks,
and asks GitHub to merge the exact checked revision. It then tags the resulting
merge commit and calls the reusable release workflow. A rerun recognizes an
already prepared `VERSION` and does not increment it twice.

Repository Actions settings must allow workflows to write repository contents
and pull requests. Branch protection and required reviews or checks remain
enforced because the merge is performed by GitHub without an administrator
bypass.

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
