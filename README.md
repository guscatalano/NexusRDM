# NexusRDM

A modern, fast Remote Desktop and SSH connection manager built with **WinUI 3** and **.NET 9**.

## Features (planned)

- **RDP sessions** — embedded via Windows MSTSC ActiveX (AxMSTSCLib)
- **SSH sessions** — SSH.NET for connections, VtNetCore for VT100/xterm terminal emulation
- **Connection groups** — organize hosts in a tree view with tags and search
- **Credential vault** — secrets stored in Windows Credential Manager (never plaintext on disk)
- **Tabbed sessions** — open multiple RDP/SSH sessions simultaneously
- **Import/export** — `.rdp` files, PuTTY sessions, JSON backup
- **Audit log** — per-connection history with timestamps
- **Themes** — WinUI 3 light/dark/system with Mica material

## Tech Stack

| Layer | Technology |
|---|---|
| UI | WinUI 3 (Windows App SDK) |
| Language | C# / .NET 9 |
| MVVM | CommunityToolkit.Mvvm |
| SSH | SSH.NET |
| Terminal emulation | VtNetCore |
| RDP | AxMSTSCLib (Windows MSTSC ActiveX) |
| Database | SQLite via EF Core 9 |
| Secrets | Windows Credential Manager (via CredentialManagement) |

## Solution Structure

```
NexusRDM/
├── src/
│   ├── NexusRDM/             # WinUI 3 app (Views, ViewModels, Controls)
│   ├── NexusRDM.Core/        # Business logic, models, service interfaces, protocol handlers
│   └── NexusRDM.Data/        # EF Core DbContext, repositories, migrations
├── tests/
│   ├── NexusRDM.Core.Tests/
│   └── NexusRDM.Data.Tests/
└── docs/
```

## Getting Started

1. Install [Visual Studio 2022](https://visualstudio.microsoft.com/) with **Windows App SDK** workload
2. Install [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (stable)
3. Clone this repo and open `NexusRDM.sln`
4. Set `NexusRDM` as startup project and run

## Requirements

- Windows 10 version 1809 (build 17763) or later
- Visual Studio 2022 17.x+
- .NET 9 SDK

## License

MIT
