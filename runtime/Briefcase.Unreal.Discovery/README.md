# Briefcase.Unreal.Discovery

This static library owns bounded inspection of the mapped executable image:

- validated PE section views;
- byte-pattern and RIP-relative signature resolution;
- discovery of the Unreal runtime globals used by Briefcase.

It contains no hooks, managed hosting, mod loading, or Unreal object traversal.
`Briefcase.UnrealRuntime.dll` links it statically, so this partition does not add
another file to the installed Briefcase package.
