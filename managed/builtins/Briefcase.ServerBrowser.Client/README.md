# Briefcase server browser

This client built-in supplies the directory shown in Briefcase's framework-owned
**Servers** page. It stores named community servers in `Briefcase/servers.json`
with separate gameplay and administration endpoints and credentials. IPv4, host
names, and bracketed IPv6 endpoints use `ADDRESS:PORT` form.

Each saved server offers **Edit**, **Join**, and **Configure**. Configure selects
the server's administration endpoint and opens remote administration inside the
same Servers workspace, where the user can return to the directory.

The Avalonia component callback only edits managed state. Connecting creates a pending
request that is consumed by `DeceiveIncPlayerController.ReceiveTick`; the shared
`CommunityServerConnection` helper then invokes
`EOSServerBrowserSubsystem.DirectConnect` on Unreal's game thread. Startup
Automation compiles the same helper source, keeping endpoint validation and the
actual travel operation consistent.
