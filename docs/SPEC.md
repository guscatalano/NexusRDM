# NexusRDM — Specification v0.1

## Overview

A modern WinUI 3 desktop app for managing RDP and SSH connections in a single tabbed interface. Targets sysadmins and developers managing many remote machines.

---

## Milestones

### M1 — Scaffold + Connection CRUD ← current

- \[x\] Solution + project files (NexusRDM, Core, Data, Tests)
- \[x\] Core models: ConnectionProfile, Group, AuditEntry, ProtocolOptions
- \[x\] Service interfaces: IConnectionService, ICredentialVault, ISshHandler, IRdpHandler
- \[x\] CredentialVault (Windows Credential Manager, never touches SQLite)
- \[x\] ConnectionService (orchestrates repo + audit)
- \[x\] SSH protocol: SshSession ([SSH.NET](http://SSH.NET) + async read loop) + SshHandler factory
- \[x\] EF Core: NexusDbContext, ConnectionRepository, AuditRepository
- \[x\] DI wiring: ServiceCollectionExtensions, App.xaml.cs bootstrapper
- \[x\] UI shell: NavigationView + TabView in MainWindow
- \[x\] ConnectionsPane: TreeView + search + toolbar (bound to ConnectionsViewModel)
- \[ \] EditConnectionDialog (Add/Edit/Delete a profile)
- \[ \] First EF Core migration + seeded test data

### M2 — SSH Sessions

- \[ \] SshHandler.ConnectAsync end-to-end
- \[ \] VtNetCore terminal renderer in SwapChainPanel
- \[ \] SshSessionView tab content (keyboard input + resize)
- \[ \] Password auth + private key auth UI

### M3 — RDP Sessions

- \[ \] AxMSTSCLib HWND host bridge for WinUI 3
- \[ \] RdpSessionView tab content
- \[ \] .rdp file import/export (parse key=value format)
- \[ \] Drive/clipboard/audio redirect settings

### M4 — Polish

- \[ \] Settings page (theme, default ports, log path)
- \[ \] Audit log viewer page
- \[ \] Tags filter in ConnectionsPane
- \[ \] PuTTY sessions.reg import
- \[ \] SFTP file browser (SSH)
- \[ \] Keyboard shortcuts (Ctrl+N, Ctrl+W, Ctrl+F, Ctrl+Tab)

---

## Architecture

```
NexusRDM (WinUI 3 app)
  └─ depends on ──▶  NexusRDM.Core  (models, interfaces, services, protocols)
  └─ depends on ──▶  NexusRDM.Data  (EF Core, repositories)
                          └─ depends on ──▶  NexusRDM.Core
```

Key rules:

- **Core has zero WinUI / EF Core dependencies** — pure .NET, fully testable
- **Credentials never enter SQLite** — stored in Windows Credential Manager under `NexusRDM/<key>`
- **ViewModels depend on IConnectionService** — never on repositories directly
- **Protocol handlers (SshHandler, RdpHandler) are registered as singletons** — sessions are transient

---

## NuGet Packages

ProjectPackagePurposeNexusRDMMicrosoft.WindowsAppSDKWinUI 3 platformNexusRDMCommunityToolkit.MvvmObservableObject, RelayCommand source generatorsNexusRDMSerilog.Sinks.FileRolling log fileNexusRDM.Core[SSH.NET](http://SSH.NET)SSH protocolNexusRDM.CoreVtNetCoreVT100/xterm terminal emulationNexusRDM.CoreCredentialManagementWindows Credential Manager wrapperNexusRDM.DataEF Core SqliteORM + SQLite driver

---

## Data

- SQLite at `%LOCALAPPDATA%\NexusRDM\connections.db`
- Logs at `%LOCALAPPDATA%\NexusRDM\logs\nexus-YYYYMMDD.log`
- Tables: `Connections`, `Groups`, `AuditLog`
- Run migrations: `dotnet ef migrations add <Name> --startup-project src\NexusRDM`
