#if !BRIEFCASE_HEADLESS
using Briefcase.Rendering;

namespace Briefcase.ManagedHost;

/// <summary>
/// BCL-only state passed through the optional UI assembly boundary. Avalonia is
/// kept out of ManagedHost while the retained UI receives live startup progress.
/// </summary>
internal sealed record AvaloniaUiState(
    ConfigurationRegistry Configuration,
    FrameworkStartupProgress StartupProgress,
    AvaloniaMenuLifetime MenuLifetime,
    AvaloniaMenuMargins MenuMargins);
#endif
