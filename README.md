# Raudel Control Panel Lite

A lightweight Windows Forms control panel for managing local services, scripts, and
Docker containers on the Raudeland infrastructure box.

## Features

- Monitor configured services (status, CPU, RAM, PID, port, owner).
- Start / Kill individual services, or Start All / Kill All.
- Run-on-startup checkboxes, synced with Windows scheduled tasks.
- Docker integration:
  - Deep compatibility: start/stop via `docker`/`docker compose`, live container
    status and per-container CPU/RAM from `docker stats`.
  - Optional live auto-listing of all Docker containers (Settings toggle).
  - One-click "Import Docker" to persist containers as services.
  - Checkbox reflects the container restart policy (`unless-stopped` / `no`).
- Add arbitrary external processes (`.exe`, `.ps1`, `.bat`, `.js`, `.py`, Docker).
- System tray support with startup/minimized mode.

## Requirements

- Windows
- .NET 10 SDK (target framework `net10.0-windows`)
- Docker CLI (optional, for Docker features)

## Build

```
dotnet publish -c Release -o publish
```

The app looks for a shared `services_config.json` in a set of known directories
(see `MainForm.ResolveConfigDirectory`). `services_config.json` and
`lite_settings.json` are intentionally git-ignored (they contain local paths and
secrets).
