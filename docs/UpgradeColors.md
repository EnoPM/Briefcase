# Deceive Inc. upgrade colors

The five color values below come from the `Civ_Icon`, `StaffIcon`, `GuardIcon`, `TechIcon` and `VIPIcon` tint properties in the game's cooked `UI/Menus/PowerupDecksMENUs/ELEMENT_PowerupBuild_ListItem` widget. They were read from the local October 2026 Windows client build. The source stores colors as Unreal `FLinearColor` values; the hex column converts linear RGB to sRGB and rounds each channel to the nearest 8-bit value for Avalonia.

| Upgrade | Game tier | Linear RGB | Avalonia sRGB |
| --- | --- | --- | --- |
| White | Civilian | `0.8148469924926758, 0.7304610013961792, 0.527114987373352` | `#E9DEC0` |
| Green | Staff | `0.10224200040102005, 0.6514059901237488, 0.05951099842786789` | `#5AD345` |
| Blue | Guard | `0.0466650016605854, 0.2195259928703308, 0.5711249709129333` | `#3D81C7` |
| Purple | Technician | `0.7830610275268555, 0.08789099752902985, 0.9375` | `#E554F8` |
| Yellow | VIP | `1.0, 0.45583099126815796, 0.0` | `#FFB400` |

All five source tints use alpha `1.0`. The tier commonly called white is an ivory tint in this widget, rather than pure `#FFFFFF`. These are UI icon tints, not sampled screenshot pixels or the lit colors of world objects.
