# Briefcase.VersionProxy

`version.dll` forwards all 17 exports to the genuine Windows System32 library.
After `DllMain` returns, a worker thread loads
`Briefcase/Core/Native/Briefcase.UnrealRuntime.dll` and calls its versioned
bootstrap export. The proxy contains no Unreal, CoreCLR, mod-loading, or UI code.

The build script places the proxy in `dist\Briefcase\version.dll`, the native
runtime in `dist\Briefcase\Briefcase\Core\Native`, and the managed runtime in
the adjacent Core directory. Deploy the complete package together.
