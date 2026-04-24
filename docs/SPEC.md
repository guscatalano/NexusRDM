# NexusRDM — Specification v0.1

## Overview

A modern WinUI 3 desktop app for managing RDP and SSH connections in a single tabbed interface. Targets sysadmins and developers managing many remote machines.

---

## Status

### ✅ M1 — Connection CRUD

- \[x\] Solution + project files (NexusRDM, Core, Data, Tests)
- \[x\] Core models: ConnectionProfile, Group, AuditEntry, ProtocolOptions
- \[x\] Service interfaces: IConnectionService, ICredentialVault, ISshHandler, IRdpHandler
- \[x\] CredentialVault — Windows Credential Manager, `NexusRDM/` prefix, never touches SQLite
- \[x\] ConnectionService — orchestrates repo + audit log
- \[x\] EF Core: NexusDbContext, ConnectionRepository, AuditRepository
- \[x\] InitialSchema migration + design-time factory
- \[x\] DI wiring in App.xaml.cs
- \[x\] MainWindow shell — NavigationView (compact) + TabView
- \[x\] ConnectionsPane — TreeView + search + toolbar (Add / New Group / Refresh)
- \[x\] Right-click context menu — Connect / Edit… / Delete with confirmation dialog
- \[x\] EditConnectionDialog — General, Credentials, SSH options, RDP options tabs
- \[x\] ValueConverters for show/hide SSH vs RDP panels

### ✅ M2 — SSH Sessions

- \[x\] SshSession ([SSH.NET](http://SSH.NET)) — async read loop firing DataReceived events
- \[x\] SshHandler factory — password auth + private key auth
- \[x\] SessionManager singleton — tracks open SSH + RDP sessions
- \[x\] SshSessionViewModel — connect/disconnect/resize/input
- \[x\] SshSessionView — toolbar + terminal surface + status bar
- \[x\] TerminalControl — VtNetCore 1.0.9 VT parser, Canvas renderer, keyboard translation
- \[x\] CredentialPromptDialog — shown when no saved credential

### ✅ M3 — RDP Sessions

- \[x\] RdpHandler + RdpSession — launches mstsc.exe, reparents window into HWND panel
- \[x\] Writes temporary .rdp file with all settings (resolution, audio, clipboard, drives)
- \[x\] RdpSessionViewModel — connect/disconnect/resize/Ctrl+Alt+Del
- \[x\] RdpSessionView — toolbar + HWND host panel + status bar

### ✅ M4 — Settings + Audit

- \[x\] AuditLogPage + AuditLogViewModel — filterable list of all events
- \[x\] SettingsPage + SettingsViewModel — theme, default ports, persisted to LocalSettings
- \[x\] Navigation wired: Audit Log and Settings open as pinned tabs

### ✅ Tests

- \[x\] 9 passing integration tests (ConnectionServiceTests)
  - CreateAsync persists + writes audit entry
  - UpdateAsync changes display name
  - DeleteAsync removes profile + writes audit entry
  - SearchAsync finds by display name and by host
  - CreateGroupAsync persists group
  - DeleteGroupAsync nullifies FK on child connections

---

## Architecture

```
NexusRDM (WinUI 3, net9.0-windows10.0.19041)
  ├── depends on NexusRDM.Core  (net9.0 — models, interfaces, services, protocols)
  └── depends on NexusRDM.Data  (net9.0 — EF Core, repositories, migrations)
                    └── depends on NexusRDM.Core

NexusRDM.Core.Tests (net9.0 — xunit, in-memory SQLite)
  ├── depends on NexusRDM.Core
  └── depends on NexusRDM.Data
```

Key rules:

- Core has zero WinUI / EF Core dependencies — fully testable
- Credentials never enter SQLite — Windows Credential Manager only
- ViewModels depend on IConnectionService, never on repositories directly
- Protocol handlers are singletons; sessions are transient

---

## Known TODOs / Future Work

- TerminalControl: replace Canvas/TextBlock renderer with Win2D SwapChainPanel for proper colour, bold, underline, and GPU-accelerated rendering
- SSH resize: [SSH.NET](http://SSH.NET) 2024.2.x ShellStream doesn't expose resize directly; current stub stores new cols/rows for reconnect. Full fix: recreate ShellStream
- RDP phase 2: replace mstsc.exe reparent with AxMSTSCLib for in-process embedding, programmatic credential passing, and event handling
- Add NewGroupDialog (currently a stub in NewGroupCommand)
- SFTP file browser tab (SSH)
- PuTTY sessions.reg import
- Tag filter in ConnectionsPane
- Keyboard shortcuts: Ctrl+N, Ctrl+W, Ctrl+F, Ctrl+Tab
- CI pipeline (GitHub Actions: dotnet build + dotnet test)

---

## Running Migrations

```powershell
cd src\NexusRDM.Data
dotnet ef migrations add <MigrationName>
# App runs EnsureCreated / Migrate automatically on startup
```

## Running Tests

```powershell
dotnet test tests\NexusRDM.Core.Tests\NexusRDM.Core.Tests.csproj
```
