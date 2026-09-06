# Versioning

Briefcase uses Semantic Versioning for framework releases. The repository tag
for version `0.7.0` is `v0.7.0` and the portable client and server packages are
both produced from that tag.

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
