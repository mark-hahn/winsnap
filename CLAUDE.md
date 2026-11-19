# winsnap

Taskbar-pinned screenshot tool: C# WinForms in one file, built by `build.cmd` with the `csc.exe`
that ships with Windows (no SDK install needed).

## Rules

- Deploy is claude3's Run button: `./run` → `./deploy`, which rebuilds `winsnap.exe` only when
  `winsnap.cs` or `build.cmd` changed. Don't run `./run` or `./deploy` yourself; the user deploys.

## Architecture

Architecture note: `.claude/architecture.md` (layout, flows, gotchas). Read it when a task needs
orientation, and keep it current.
