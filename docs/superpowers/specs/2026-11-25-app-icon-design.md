# App Icon Design

## Visual direction

Use a single, quiet mark that remains recognizable in Windows' 16-pixel title
bar and taskbar slots. The selected concept is an open mint-green circular
track shaped like a C around a pale diamond core. The open track suggests Conda
and a managed environment; the diamond reads as one isolated package or
environment. A deep evergreen rounded tile provides contrast on light and dark
Windows surfaces. No letters, tiny details, or platform-specific ornament.

The canonical canvas is 256 × 256 units. The tile uses `#0B3026`, a subtle
`#26664B` outline, and 54-unit corner radius. The track uses `#8BE2AA`, a
144-unit circle diameter, 21-unit round stroke, and an opening on the right.
The diamond uses `#DFF9E7` and spans 54 × 54 units. Leave a transparent
8-unit outer margin. The same geometry drives the SVG and all ICO frames.

## Assets and integration

Keep a clean SVG in `src/CondaManager.App/Assets/AppIcon.svg` as the editable
vector asset. A deterministic Pillow-based generator creates that SVG and a
single `AppIcon.ico` containing 16, 24, 32, 48, 64, 128, and 256 pixel frames.
Pillow is a development-only dependency; the app does not load it at runtime.
The repository commits both files so ordinary .NET builds need no Python.

Set the project `ApplicationIcon` to the ICO for the published EXE. Package
the ICO as a WPF resource and set `Window.Icon` on all six application windows
so title bars, taskbar buttons, and Alt+Tab use the same mark. The Window icon
must be checked in the existing offscreen WPF smoke. A self-contained publish
must contain a Win32 icon resource with the expected green mark.

## Verification

Inspect the design at 16, 32, and 256 pixels on light and dark backgrounds;
validate ICO frame sizes and transparency. Run core checks, WPF smoke, and a
`win-x64` self-contained publish. Inspect the published EXE icon resource.
Tests and builds do not invoke a real Conda installation.
