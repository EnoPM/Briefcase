# Briefcase server browser

This client built-in adds the **Servers** entry to Briefcase's F1 navigation.
It stores named community servers in `Briefcase/servers.json` and supports IPv4,
host names, and bracketed IPv6 endpoints in `ADDRESS:PORT` form.

The ImGui render callback only edits managed state. Connecting creates a pending
request that is consumed by `DeceiveIncPlayerController.ReceiveTick`; the shared
`CommunityServerConnection` helper then invokes
`EOSServerBrowserSubsystem.DirectConnect` on Unreal's game thread. Startup
Automation compiles the same helper source, keeping endpoint validation and the
actual travel operation consistent.
