# Briefcase.VersionProxy

`version.dll` forwards all 17 exports to the genuine Windows System32 library.
The Unreal metadata runtime is statically linked into this same DLL and runs on
a worker thread after `DllMain` returns.

The source remains separated by responsibility. The build script places the
proxy in `dist\Briefcase\version.dll` and the managed runtime in the adjacent
`dist\Briefcase\Briefcase` directory; deploy both parts together.
