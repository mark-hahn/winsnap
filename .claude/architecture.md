# winsnap: architecture

Status: built and tested 2026-11-12 (single monitor at 150%).

## What it is

A Windows app pinned to the taskbar. One click grabs the whole desktop (all monitors) and shows
it in a maximized window. Drag to crop, then save it (Save As) or copy it, and the app exits.
The crop behaviour copies the Cap pop-up in claude2/claude3 (an HTML canvas dialog there),
rewritten as native code; everything needed from it is described below.

A pure Windows project: C# WinForms on .NET Framework 4.x, compiled by the `csc.exe` that ships
with Windows. No SDK, no NuGet, no installer.

## Layout

- `winsnap.cs`: the whole app, one file.
- `build.cmd`: one line,
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:winsnap.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll winsnap.cs`
- `winsnap.exe`: the build output, built in place. The taskbar pin points at it
  (right-click, Show more options, Pin to taskbar).
- `run`, `deploy`: claude3's Run button runs `./run` (Git Bash, the tab's host is win), which
  `exec`s `./deploy`. The deploy hashes `winsnap.cs` + `build.cmd`, compares with `.build-hash`,
  and when they differ (or the exe is missing) runs `build.cmd` through
  `MSYS_NO_PATHCONV=1 cmd.exe /d /c '.\build.cmd'`, checks the exe was rewritten, then records the
  hash. It refuses (`!!`, exit 1) while `winsnap.exe` is running. On success it writes
  `winsnap.cs` and `build.cmd` into `.deployed`, the stamp claude3's deploy warning reads (that
  warning needs a git repo, which this folder doesn't have yet). `.gitattributes` keeps both
  scripts LF.

## Flow

1. `Main` (`[STAThread]`): `SetProcessDPIAware()` (user32, P/Invoke) first, then capture, then
   show the form.
2. Capture: `SystemInformation.VirtualScreen` gives the desktop's bounds. Make a `Bitmap` that size,
   then `Graphics.CopyFromScreen(vs.Location, Point.Empty, vs.Size)`. This runs before any winsnap
   window exists, so winsnap is never in the shot (claude2/3 had to minimize their own window
   first; here there is nothing to hide).
3. Window (`SnapForm`): maximized, the shot in a `PictureBox` with `SizeMode = Zoom`, a
   `FlowLayoutPanel` across the top holding the Save and Copy buttons at its right end
   (`FlowDirection.RightToLeft`, so Copy is added first; `TabIndex` keeps Save first in tab
   order, so it has the initial focus). The title reads
   `winsnap - <hint> - W x H`, the hint being "drag to crop", or "drag to crop, click to undo a
   crop" once there is a crop to undo. Keys go through `ProcessCmdKey`, so they work whichever
   control has focus.
4. Crop (as claude3's `cropDown`/`cropMove`/`cropUp`):
   - Left button down records the start point. WinForms captures the mouse to the control on
     button down, so a drag can end outside the window.
   - Move: nothing until the pointer has gone 4px from the start on either axis, then the
     selection rectangle is drawn over the picture (in `Paint`), black and white dashes so it
     shows on any picture. Only the strip around the old and new outline is invalidated.
   - Up after a drag: map both ends to image pixels (below). A rectangle under 2px either way is
     ignored. Otherwise `Bitmap.Clone(rect, format)` goes on top of a stack and is shown.
   - Up without a drag: undo, i.e. pop the stack, if it holds more than the original.
   - The stack is a `List<Bitmap>`, original at [0], the picture shown at the end. Dispose what
     gets popped (after taking it off the `PictureBox`).
   - Losing mouse capture mid-drag (`MouseCaptureChanged`, e.g. Alt+Tab) drops the drag.
5. Save (Ctrl+S): `SaveFileDialog` with PNG (default) and JPG filters and a default name of
   `winsnap-yy-MM-dd-HH-mm-ss.png` (24-hour), opening in Windows' Screenshots known folder (`ScreenshotsFolder`:
   `SHGetKnownFolderPath(FOLDERID_Screenshots, KF_FLAG_CREATE)`, where Win+PrtScn saves; here
   it is `OneDrive\Pictures\Screenshots 1`, not `Screenshots`), falling back to
   `Pictures\Screenshots`. Save the top of the stack in the format the chosen filter says,
   unless the typed name's extension says otherwise (`foo.jpg` with the PNG filter is a JPEG),
   then exit. JPEG at quality 95. Cancel goes back to the window. A failed save or copy shows a
   message box and stays open.
6. Copy (Ctrl+C): `Clipboard.SetImage(top)`, then exit.
7. Esc: exit without saving.

## Mouse to image pixels (claude3's `cropRect`)

`Zoom` letterboxes the picture inside the `PictureBox`, so the box alone doesn't say where the
picture is:

    scale = min(boxW / imgW, boxH / imgH)
    left  = (boxW - imgW * scale) / 2
    top   = (boxH - imgH * scale) / 2
    imgX(x) = (clamp(x, left, left + imgW * scale) - left) / scale     (same for y)
    rect  = x: min(imgX(a), imgX(b)), w: |imgX(b) - imgX(a)|, same for y/h, all rounded

Rounding can push the rectangle one pixel past the picture, and `Bitmap.Clone` throws on that,
so `CropRect` intersects it with the picture's bounds.

Both ends are clamped onto the picture, so a drag that starts or ends off the picture crops to
its edge. On screen the selection is drawn at `left + rect.x * scale`, `top + rect.y * scale`,
`rect.w * scale` by `rect.h * scale`.

## Gotchas

- DPI: unless the process is DPI-aware, at 150% scaling Windows reports scaled sizes and the shot
  covers only the top-left two thirds of the desktop (found in claude2). `SetProcessDPIAware()`
  must run before any screen size is read: make it the first line of `Main`. It is
  system-aware only; if a mixed-DPI multi-monitor setup captures wrong, go per-monitor with
  `SetProcessDpiAwarenessContext(-4)`.
- `VirtualScreen` can start at negative coordinates (a monitor left of or above the main one).
  Copy from `vs.Location`, never from (0, 0).
- That `csc.exe` compiles C# 5 only: no `$"..."` interpolation, `?.`, `=>` members, `nameof`
  or `out var`.
- Without `/target:winexe` a console window opens alongside.
- The clipboard needs `[STAThread]`. `Clipboard.SetImage` copies the data onto the clipboard, so
  the picture is still there after winsnap exits (tested).
- `MouseEventArgs.Button` on a move comes from the live key state, not the message, so the drag
  tracks its own `pressed` flag instead.
- The shot is `Format24bppRgb`: no alpha channel, so PNGs come out smaller.
- The rebuild fails while `winsnap.exe` is running (the file is locked). The pin points at the
  path, so a rebuild in place keeps it.
- Building from Claude's shells: run `.\build.cmd` from PowerShell (that shell sets
  `NoDefaultCurrentDirectoryInExePath`, so a bare `build.cmd` isn't found). Don't call `csc.exe`
  from Git Bash: it rewrites `/nologo` and the other `/` switches as paths.
- Clicked from the taskbar, the shot is taken almost at once. If a taskbar tooltip, hover
  highlight or thumbnail shows up in shots, add a short sleep before the capture and tune it.

## Testing

There is no test suite in the repo. To drive the app without touching whatever window has the
keyboard: post `WM_LBUTTONDOWN`/`WM_MOUSEMOVE`/`WM_LBUTTONUP` to the `PictureBox` (the largest
child window), `BM_CLICK` to the buttons, `WM_KEYDOWN` Esc to the form, and work the Save dialog
through UI Automation (file name edit has AutomationId `1001`, Save is `1`, Cancel `2`). UIA
lists the buttons as Panes named `Save (Ctrl+S)` and `Copy (Ctrl+C)`; the dialog is a `#32770`
in winsnap's process, and its folder is the element named `Address: <path>`. Test a change with
a build in a temp folder (call `csc.exe` with its own `/out:`) so the pinned exe stays the
deployed one. Read the
crop size off the title, and the result off the clipboard or the saved file. Ctrl+S and Ctrl+C
can't be posted: `ProcessCmdKey` reads Ctrl from the live key state. Never use `SendKeys`, which
types into whatever window has focus.
