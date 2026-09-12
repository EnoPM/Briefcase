#if !BRIEFCASE_HEADLESS
using Briefcase.Rendering;

namespace Briefcase.ManagedHost;

internal sealed partial class ConfigurationRegistry
{
    public AvaloniaWindowPlacement GetAvaloniaPlacement()
    {
        lock (_gate)
        {
            var window = _document.Window;
            return new AvaloniaWindowPlacement(
                window.X, window.Y, window.Width, window.Height);
        }
    }

    public void RememberAvaloniaPlacement(AvaloniaWindowPlacement placement)
    {
        if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y) ||
            !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
            placement.Width < 1 || placement.Height < 1)
            return;

        lock (_gate)
        {
            var window = _document.Window;
            var x = (float)placement.X;
            var y = (float)placement.Y;
            var width = (float)placement.Width;
            var height = (float)placement.Height;
            if (NearlyEqual(window.X, x) && NearlyEqual(window.Y, y) &&
                NearlyEqual(window.Width, width) && NearlyEqual(window.Height, height))
                return;
            window.X = x;
            window.Y = y;
            window.Width = width;
            window.Height = height;
            ScheduleSave();
        }
    }
}
#endif
