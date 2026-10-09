# Shared instances

ServerApp now supports several logical servers sharing one dedicated-server
installation. Each server has a separate INI, balance profile, UE4SS runtime,
mod selection, mod configuration and Briefcase data under
`<data-root>/servers/<instance-id>/`.

Application settings can install separate Steam branches under
`<data-root>/game/versions/<branch>/` or in a chosen folder. The previously used
`<data-root>/game/dedicated` is registered as the public version during the
settings migration. The branch's build ID is read from its Steam manifest.
Beta passwords are entered for an install or update and are not stored in the
ServerApp registry. Creating a server selects one installed version. A stopped
server can switch to another installed version from Overview; its instance
configuration and mods remain in place, and its original game files are not
deleted. Give simultaneous instances distinct game and query ports.

The app rediscovers running instances by a GUID/PID marker and accepts live
snapshots only when the bridge reports the expected GUID. Shared game and
Briefcase updates are disabled while any instance using that installation is
running. Deleting one server's files deletes its instance data; it leaves the
shared game installation intact.

The registry is schema 3; migration from schema 2 retains existing server
entries and backs up the former settings file. Tests cover switching an
existing server to a shared executable while preserving its instance data.
The Windows local implementation passed a two-process live launch test. Some
game-owned output under the shared `DeceiveInc/Saved` directory has not yet been
audited for collisions. See `BriefcaseNative/docs/SharedServerInstances.md` for
the runtime details and remaining limits.
