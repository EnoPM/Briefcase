# Avalonia client UI

Avalonia 12 is Briefcase's only client UI and overlay renderer. The framework
does not ship Dear ImGui, cimgui, DirectComposition, Vortice, or a second UI
backend.

The client package separates two responsibilities:

- `Core/Ui/Avalonia/Briefcase.AvaloniaUi.dll` owns the transparent game overlay,
  startup progress, vector overlay primitives, F1 input surface, and animation;
- `Core/Ui/Avalonia/Briefcase.AvaloniaMenu.dll` owns the configuration menu and
  is loaded only when that menu is first constructed.

The loading content is centered directly over the dimmed game without a menu
panel. It has an independent visual lifetime and is released after startup.

The menu dimensions follow the game client area. Configure the percentage
reserved on each edge in `Briefcase/loader.json`:

```json
{
  "avaloniaMenuLifetime": "cached",
  "avaloniaMenuMargins": {
    "horizontalPercent": 12.5,
    "verticalPercent": 8.0
  },
  "gameWindowChrome": false
}
```

For example, horizontal margins of 12.5 percent leave the panel at 75 percent
of the game width. Vertical margins of 8 percent leave it at 84 percent of the
game height. Both values accept 0 through 45 and are recalculated while the game
window changes size.

With `avaloniaMenuLifetime` set to `cached`, the menu tree is built before its
first opening animation and retained while hidden. Set it to `per-open` to build
a fresh tree before every opening animation and dispose it after every closing
animation.

The full-client backdrop dims the game and consumes pointer input while the menu
is open. When only mod overlay primitives are visible, the Avalonia window is
click-through. F1 opens and closes the menu; Escape and **Close** hide it.

Client mods describe configuration through `Briefcase.ClientModApi`. Primitive
real-time drawings use `RenderFrame.Overlay`, which batches toolkit-neutral
lines, circles, rectangles, and text for the Avalonia overlay. Mods never receive
Avalonia objects and remain independent of the concrete view implementation.

The dedicated-server package remains headless and includes no Avalonia, Skia,
client UI, or rendering assembly.
