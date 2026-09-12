# Briefcase.Unreal.Metadata

This static library turns validated runtime reflection into a build-specific SDK
snapshot. It owns both JSON and Briefcase Snapshot encoding, output selection
from `loader.json`, and atomic replacement of the generated metadata file.

It reads Unreal state through `Briefcase.Unreal.Reflection` and does not install
hooks, invoke gameplay functions, or host managed mods.
