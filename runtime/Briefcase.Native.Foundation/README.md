# Briefcase.Native.Foundation

This static library contains native infrastructure that has no dependency on
Unreal Engine or the managed host. It currently owns the process-wide Briefcase
log. Runtime modules link it statically, so it adds no installed DLL.
