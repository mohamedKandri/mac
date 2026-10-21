# MacShell

A personal macOS-style menu bar and dock for Windows 11 (C# / WPF, .NET 8). See [CLAUDE.md](CLAUDE.md) for the
stack, structure, conventions and the full decisions log, and `.claude/skills/` for the project skills.

## Build and run

1. Install the .NET 8 SDK (or run Microsoft's `dotnet-install.ps1 -Channel 8.0`).
2. `.\publish.ps1` builds one self-contained `dist\MacShell.exe`.
3. Start `dist\MacShell.exe`. On first run it writes `dist\config.json` (hot-reloaded on save).

Quit from the logo menu (top-left) to get the Windows taskbar back.
