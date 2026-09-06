namespace Briefcase.ModApi;

/// <summary>Controls what happens when a patch invokes its own target again.</summary>
public enum PatchReentrancy
{
    /// <summary>Skip only the currently executing patch during the nested call.</summary>
    SuppressCurrentPatch,

    /// <summary>Skip every patch attached to this target during the nested call.</summary>
    SuppressTargetPatches,

    /// <summary>Run the patch again. The mod is responsible for preventing recursion.</summary>
    Allow
}

/// <summary>Controls how an exception thrown by mod code is isolated.</summary>
public enum PatchExceptionPolicy
{
    /// <summary>Log the exception and continue the Unreal call.</summary>
    LogAndContinue,

    /// <summary>Log the exception and disable this patch until the mod is reloaded.</summary>
    DisablePatch,

    /// <summary>Log the exception and disable every patch belonging to this mod.</summary>
    DisableMod
}

/// <summary>Common ordering, reentrancy and failure policy for every patch.</summary>
public abstract class PatchAttribute(Type declaringType, string methodName) : Attribute
{
    /// <summary>The generated game type that owns the target UFunction.</summary>
    public Type DeclaringType { get; } = declaringType;

    /// <summary>The generated C# method name, normally supplied with nameof(...).</summary>
    public string MethodName { get; } = methodName;

    /// <summary>Higher values run first for prefixes and last for postfixes.</summary>
    public int Priority { get; set; }

    /// <summary>Patch container types that this patch must run before.</summary>
    public Type[] Before { get; set; } = [];

    /// <summary>Patch container types that this patch must run after.</summary>
    public Type[] After { get; set; } = [];

    /// <summary>Mod identifiers that this patch must run before.</summary>
    public string[] BeforeMods { get; set; } = [];

    /// <summary>Mod identifiers that this patch must run after.</summary>
    public string[] AfterMods { get; set; } = [];

    /// <summary>Behavior for a nested invocation of the same target.</summary>
    public PatchReentrancy Reentrancy { get; set; } = PatchReentrancy.SuppressCurrentPatch;

    /// <summary>Isolation policy when the patch method throws.</summary>
    public PatchExceptionPolicy OnException { get; set; } = PatchExceptionPolicy.LogAndContinue;
}

/// <summary>Base class for patches dispatched through UObject::ProcessEvent.</summary>
public abstract class UnrealPatchAttribute(Type declaringType, string methodName)
    : PatchAttribute(declaringType, methodName);

/// <summary>Runs before a reflected Unreal call dispatched by ProcessEvent.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class UnrealPrefixPatchAttribute(Type declaringType, string methodName)
    : UnrealPatchAttribute(declaringType, methodName);

/// <summary>Runs after a reflected Unreal call dispatched by ProcessEvent.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class UnrealPostfixPatchAttribute(Type declaringType, string methodName)
    : UnrealPatchAttribute(declaringType, methodName)
{
    /// <summary>Whether to run when a prefix deliberately skipped the original call.</summary>
    public bool RunWhenOriginalSkipped { get; set; } = true;
}

/// <summary>
/// Base class for direct machine-code patches. Native signatures are validated
/// separately because the C++ ABI is not the reflected ProcessEvent ABI.
/// </summary>
public abstract class NativePatchAttribute(Type declaringType, string methodName)
    : PatchAttribute(declaringType, methodName);

/// <summary>Runs before a supported direct C++ member-function call.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class NativePrefixPatchAttribute(Type declaringType, string methodName)
    : NativePatchAttribute(declaringType, methodName);

/// <summary>Runs after a supported direct C++ member-function call.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class NativePostfixPatchAttribute(Type declaringType, string methodName)
    : NativePatchAttribute(declaringType, methodName)
{
    /// <summary>Whether to run when a native prefix skipped the original call.</summary>
    public bool RunWhenOriginalSkipped { get; set; } = true;
}
