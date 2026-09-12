# Player mod handshake

Briefcase uses a public handshake channel to exchange mod manifests. The
channel is separate from Server Admin Control: a handshake frame accepts no
lifecycle, configuration, restart, or player-management command.

The initial protocol is intentionally observational. A client sends its
Briefcase version, client executable fingerprint, process-lifetime instance ID,
and installed component manifest. The server replies with its corresponding
identity and manifest. Each DLL descriptor contains a stable mod ID, version,
enabled and loaded state, dependencies, and SHA-256 content hash.

The dedicated server listens on `0.0.0.0:47000/TCP` by default. Unreal owns
`50000/UDP`; keeping the administration endpoint below Windows' dynamic TCP range avoids HNS/WinNAT port exclusions. A bounded JSON
`channel` field routes each TCP frame to either `handshake` or
`administration`. The address, port, and enabled state live in the headless
Briefcase settings under `server-admin-control.server/Unified endpoint`. The
client stores one `127.0.0.1:47000` endpoint for both channels.

Clients refresh every ten seconds. The server considers a client active for
thirty seconds after its last valid hello. The remote administration view can
request those snapshots. The public handshake remains unauthenticated and has
no administrative operations. Administration protocol version 7 uses a
short-lived PBKDF2/HMAC challenge-response proof based on the server's
`AdminPassword`; the password is never included in a protocol frame.

Protocol version 1 reserves server response fields for required, optional, and
forbidden client mods. These lists are empty and `downloadSupported` is false in
this milestone. A later version can add package URLs and signatures while
retaining the same hello shape.

Client and dedicated-server PE fingerprints are deliberately not compared to
each other because the two executables differ in the same game release. Future
compatibility policy should compare the reported client fingerprint against an
explicit list of client builds supported by that server release.
