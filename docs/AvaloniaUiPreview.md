# Avalonia client UI

Avalonia 12 is Briefcase's only client UI and overlay renderer. The framework
does not ship Dear ImGui, cimgui, DirectComposition, Vortice, or a second UI
backend.

The menu uses a dark, slightly transparent palette with a warm violet accent
and the Inter typeface. Its navigation has three stable entries:

- **Home** summarizes framework, server, and client-mod status.
- **Servers** saves community servers, joins them, and opens authenticated
  administration without leaving the Servers workspace.
- **Mods** manages installation state, loading, dependencies, and configuration
  for every client mod.

Mods do not add top-level tabs. Their generated settings and complex panels open
as detail views from the central Mods catalogue. The fixed-width navigation stays
predictable while pages use bordered cards with a violet strip, readable labels
and descriptions on the left, and aligned editors on the right. Advanced groups
can use expanders, and information-rich home cards automatically switch between
one and two columns.

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
is open. Opening uses a short fade, scale and vertical motion; closing reverses
the same motion before the menu can be released. When only mod overlay
primitives are visible, the Avalonia window is click-through. F1 opens and
closes the menu; Escape and **Close** hide it.

Automatic configuration pages use per-page session drafts. They remain pending
when the user visits another page or closes F1, but are discarded when the mod
unloads or the game exits. The bottom action bar applies or cancels only the
current page, and an application error is displayed directly in that bar.

Client mods describe configuration through `Briefcase.ClientModApi`. Primitive
real-time drawings use `RenderFrame.Overlay`, which batches toolkit-neutral
lines, circles, rectangles, and text for the Avalonia overlay. Mods never receive
Avalonia objects and remain independent of the concrete view implementation.

The dedicated-server package remains headless and includes no Avalonia, Skia,
client UI, or rendering assembly.
