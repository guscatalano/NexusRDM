# NexusRDM

A modern Remote Desktop and SSH connection manager built with **WinUI 3** and **.NET 9**.

## Features

### Connections
- **Per-connection icons** — pick from a curated set of Segoe Fluent glyphs in the editor; rendered in the tree row in connection-status colour. Optional — connections without an icon just show the name.
- **Groups** — create groups from the sidebar; nest groups under groups; drop a connection into any group via the editor's Group ComboBox. Groups render with a bold name and an amber folder glyph for instant visual distinction.
- **Live status dots / icons** — green when a session is up, red when not. Both the tree row and the tab header reflect status; the tab also surfaces protocol icon (CommandPrompt for SSH, Remote for RDP) matching the home-page legend.
- **Host ping** — periodic ICMP probe with optional latency display next to each connection. Toggleable + interval-configurable in Settings; on by default.
- **Tags** and full-text **search** in the sidebar.
- **Click behaviour** — single- or double-click to connect, configurable in Settings.

### SSH
- VT100/xterm terminal via [VtNetCore](https://github.com/darrenstarr/VtNetCore).
- Underlying transport via [SSH.NET](https://github.com/sshnet/SSH.NET) with sync write/flush wrap (avoids `Renci.SshNet.ShellStream` async deadlock).
- Authentication: password, private key (.pem / .ppk), keyboard-interactive.
- Per-tab toolbar: **Full screen**, **Pop out** (detaches into its own resizable window with a wait-for-`Unloaded` reparent dance to satisfy WinUI 3's single-XamlRoot rule), Disconnect.
- Audit-log lifecycle events on connect / disconnect.

### RDP
- In-proc [`mstscax.dll`](https://learn.microsoft.com/windows/win32/termserv/mstscax) hosting via WinForms `AxHost` on a top-level owned window pinned over the host tab — full control: live resolution, smart-sizing, full-screen, resolution presets, redirections.
- **Pop-out** detaches into a free-floating window; closing re-attaches to the tab and respects the active-tab visibility (re-hides if you've switched tabs).
- **Resolution dropdown** in the toolbar matches the global default-resolution setting (Match monitor / Match panel / 1024×768 … 3840×2160) and live-resizes the session via `IMsRdpClient9.UpdateSessionDisplaySettings` — no reconnect.
- **Reconnect** without closing the tab — Disconnect button flips to Connect when the session ends; clicking rebuilds a fresh `IRdpSession`, replays the last bounds, and swaps it into `OpenSession`.
- **mstscax.dll override** via SxS activation context — point at any custom DLL, the Validate button does `LoadLibrary` + `DllGetClassObject` + `CreateInstance` to confirm the COM class is reachable; on success the session uses that DLL instead of the system one.
- **mstsc.exe override** for the legacy launch-as-process backend.
- **RDP events window** — a floating diagnostic feed of every event the OCX raises (Connected / Disconnected / OnLogonError / OnAuthenticationWarning / etc.) via a custom `IDispatch` connection-point sink. Selectable, copyable; pinned always-on-top above the RDP form.
- **Edit panel covers ~440px**: when the slide-over editor is open, every embedded RDP form narrows by exactly the panel's width so the live session stays visible while you edit.

### Themes
- 8 built-in palettes — **Dracula** (default), Dark, Light, Solarized Dark, Solarized Light, Nord, Monokai — applied at runtime by mutating each shared `SolidColorBrush.Color`.
- **Custom theme editor** — pick "Custom" in Settings and edit each token (Bg0…Bg3, Tx1…Tx3, Brd, Accent / Accent2, Ssh, Rdp, Red, Yellow) via a colour-picker flyout. Persisted as `#AARRGGBB` per token.

### Settings
- **Auto-persist** — every change writes to disk; no Save button. The settings page also has a left-nav with section filtering and a **search box** that filters both the nav list and the visible body.
- **Hotkeys** — configurable for Next tab / Previous tab / Toggle full screen / Toggle pop out, each with an enable checkbox. Free-form combos like `Ctrl+Tab`, `Ctrl+Shift+P`, `F11`. Re-registered live on change.
- **Audit-log retention** — entries older than the configured window (default 7 days) are purged at startup AND immediately when the user shortens the window.
- **Confirm-close while connected** — once-per-action prompt before closing a tab or the window with live sessions; suppressible.
- **Debug mode** — surfaces developer affordances (Copy visual tree button in the sidebar).
- **Database management** — shows DB path + creation date; *Open data folder*, *Reset database* (with confirmation), *Export database* (full JSON dump of connections / groups / audit entries).

### Audit log
- Captures every Created / Updated / Deleted / Connected / Disconnected / Failed event with timestamp, profile name, and a Detail column.
- **Update events list field-level diffs** ("Name: old → new; Port: 22 → 2222; RDP options changed: ColorDepth, NetworkType") — names only on embedded option blobs to keep credentials out of the log.
- **Auto-refreshes** via an `IAuditNotifier` event hub plus a 3 s polling backup so the page reflects new entries instantly.
- Wide Detail column with text wrapping.

### Credentials
- All secrets live in Windows Credential Manager under the `NexusRDM/` prefix — never on disk.
- Deleting a connection removes its credential too; if wincred refuses, the user gets a warning dialog identifying the orphaned key.
- Password fields are disabled when "Don't save — prompt at connect time" is ticked, so the value isn't quietly typed and discarded.

### Windowing & airspace
- Embedded RDP forms are top-level Win32 windows owned by the WinUI HWND. WinUI 3 cannot host Win32 child HWNDs visibly (composition airspace) — see `docs/rdp-embedding.md` for the design journey.
- A central `Services/DialogHost` serialises every `ContentDialog` (WinUI 3 only allows one at a time) and parks every embedded RDP form for the dialog's lifetime so they don't paint over the buttons.
- Pop-out reparenting waits for the `Unloaded` event before assigning to the new `Window.Content` (XamlRoot release timing).
- Cross-process per-monitor V2 DPI awareness explicitly set on the WinForms STA thread so `SetWindowPos` rects from the WinUI thread aren't DWM-virtualised down.

### Connection editor
- **Validation** — required Name / Host with inline red error text under each field.
- **Setting search box** in the footer filters every visible row by header / placeholder / content.
- **REDIRECTIONS / AUDIO / GATEWAY / Advanced** sections cover every RDP knob the OCX exposes (auth strictness, network connection type, performance flags, keyboard hook mode, connection bar, auto-reconnect, load-balance info, etc.).
- **Group selector** + per-connection **icon picker**.

## Tech Stack

| Layer | Technology |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.7) |
| Language | C# / .NET 9 |
| MVVM | CommunityToolkit.Mvvm |
| SSH | SSH.NET, VtNetCore |
| RDP | mstscax.dll via `System.Windows.Forms.AxHost` |
| Database | SQLite via EF Core 9 |
| Secrets | Windows Credential Manager (advapi32 P/Invoke) |

## Solution Structure

```
NexusRDM/
├── src/
│   ├── NexusRDM/                  # WinUI 3 app (Views, ViewModels, Services)
│   ├── NexusRDM.Core/             # Domain models, protocol interfaces, ConnectionService
│   ├── NexusRDM.Data/             # EF Core DbContext, repositories, migrations
│   └── NexusRDM.RdpAx/            # In-proc mstscax host + SxS override + dispatch sink
├── tests/
│   ├── NexusRDM.Core.Tests/
│   ├── NexusRDM.Data.Tests/
│   ├── NexusRDM.Tests.ViewModels/ # xUnit unit tests for VM + service logic
│   └── NexusRDM.Tests.UiSmoke/    # FlaUI end-to-end smoke tests against the built exe
└── docs/
    └── rdp-embedding.md           # design journey for the WinUI 3 / mstscax airspace problem
```

## Getting Started

1. Install [Visual Studio 2022](https://visualstudio.microsoft.com/) with the **Windows App SDK** workload.
2. Install the [Windows App SDK 1.7](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) runtime.
3. Open `NexusRDM.slnx` (or build `src/NexusRDM/NexusRDM.csproj` directly).
4. Set `NexusRDM` as the startup project and run.

The app is **unpackaged** (no MSIX), self-contained against the Windows App SDK runtime — first launch creates `%LocalAppData%\NexusRDM\` for the SQLite database and settings.

## Tests

```bash
# Unit + VM tests (xUnit, fake services)
dotnet test tests/NexusRDM.Core.Tests
dotnet test tests/NexusRDM.Tests.ViewModels

# End-to-end smoke (FlaUI; launches the built NexusRDM.exe)
dotnet build src/NexusRDM
dotnet test tests/NexusRDM.Tests.UiSmoke
```

The Tests.ViewModels suite covers ~110 scenarios across the connection/edit/RDP-session VMs, theme catalogue, settings store, ping wiring, the group selector's stack-overflow regression, and per-row tree-node visuals.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- Visual Studio 2022 17.10+
- .NET 9 SDK

## License

MIT
