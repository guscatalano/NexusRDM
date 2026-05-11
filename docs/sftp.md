# SFTP File Transfer

MVP design notes for the SFTP tab — two-pane file manager that opens against a connection profile, runs in its own SSH session independent of any terminal tab, and bidirectionally cross-launches with the SSH terminal view.

## Why SFTP (not SCP)

The original SCP protocol is deprecated — OpenSSH 9.0 (2022) switched its `scp` command-line tool to using SFTP under the hood by default. SFTP is the proper file-transfer subsystem: directory listings, partial transfers, metadata, atomic rename, chmod, symlinks. We use it via SSH.NET's `SftpClient`.

## Data flow

```
┌──────────────────────────────────────────────────────┐
│ User profile tree → right-click → "Open SFTP"        │
│   OR existing SSH tab toolbar → "Files" button       │
└────────────────────────┬─────────────────────────────┘
                         │
                         ▼
              MainWindow.OpenSftpTabAsync(profile)
              ├─ ResolveSshCredentialsAsync (same path as SSH)
              ├─ ISftpHandler.CreateSessionForProfile
              │     → SftpSession wrapping a fresh SftpClient
              │       (separate TCP connection from any SSH tab)
              ├─ SessionManager.AddSftp(profile, session)
              ├─ new SftpView(new SftpSessionViewModel(...))
              ├─ wire OpenSshRequested → OpenSshTabAsync
              ├─ wire TransferCompleted → audit log
              └─ AddSessionTab(...)
                         │
                         ▼
              SftpView.OnLoaded → ViewModel.ConnectAsync
                         │
                         ▼
              SftpSession.Connect → SftpClient.Connect on threadpool
                         │
                         ▼
              UI: parallel local/remote panes, transfer queue
```

## Separate session, by design

The SFTP session uses its own `SftpClient` and a **separate TCP connection** even when an SSH terminal tab is already open for the same profile. Reasons:

- A big upload can't stall the interactive terminal session.
- The terminal can disconnect / reconnect without affecting in-flight transfers.
- The two can be opened/closed independently from the user's perspective.
- SSH.NET's `SftpClient` is built to own its own session — there's no public API to attach to an existing `SshClient`.

The cost is one extra TCP handshake + auth per SFTP tab. Negligible on LAN; acceptable on WAN. The profile's vault credentials are reused, so the user never re-enters anything.

## Cross-launch

Both directions are wired so users can pivot between terminal and files for the same host without going back to the connection tree:

- **SFTP → SSH** — `SftpView.OpenSshRequested` event raised by the toolbar "Terminal" button. `MainWindow` handles it by calling `OpenSshTabAsync` against the same `ConnectionProfile`.
- **SSH → SFTP** — `SshSessionView.OpenSftpRequested` event raised by the toolbar "Files" button. `MainWindow` handles it by calling `OpenSftpTabAsync`.

Each cross-launch creates a *new* tab (and a new session) — the two share only the profile they were built from. The standard tab-reuse path in `OnConnectRequested` also distinguishes by protocol so opening an SSH tab won't surface an existing SFTP tab and vice versa.

## Components

### Core (`src/NexusRDM.Core`)

| File | Purpose |
|---|---|
| `Models/SftpEntry.cs` | Record type for directory entries (used by both panes — local + remote share the renderer). |
| `Interfaces/ISftpHandler.cs` | `ISftpSession` interface + `ISftpHandler` factory + `SftpTransferEventArgs` for audit. |
| `Protocols/SftpSession.cs` | Concrete wrapper around SSH.NET `SftpClient`. Implements connect, list, upload, download, mkdir, delete, rename. Synchronous SSH.NET calls bounced to the thread pool. Fires `TransferCompleted` after every upload/download with bytes + elapsed + success/error. |
| `Protocols/SftpHandler.cs` | Factory mirroring `SshHandler` for credential resolution. Builds `PasswordAuthenticationMethod` / `PrivateKeyAuthenticationMethod` / `KeyboardInteractiveAuthenticationMethod` based on `profile.SshAuthMode`. |
| `Models/AuditEntry.cs` | New enum value `AuditAction.FileTransfer` (appended — existing rows store action as int, so reordering would rewrite history). |
| `Interfaces/IConnectionService.cs` | New `RecordAuditAsync(id, action, detail)` for free-form audit entries — used to log file transfers. |

### UI (`src/NexusRDM`)

| File | Purpose |
|---|---|
| `ViewModels/SftpSessionViewModel.cs` | Two `ObservableCollection<SftpEntry>` for the panes. Local pane uses `Directory.EnumerateDirectories`/`Files`; remote pane goes through `ISftpSession.ListDirectoryAsync`. Transfer queue is a single `Queue<TransferRequest>` pumped one at a time — uploads and downloads compete for the single SFTP channel. |
| `Views/SftpView.xaml`/`.xaml.cs` | Toolbar + two-pane Grid + bottom status bar with `ProgressBar` for the active transfer. Right-click on either pane → Upload (local) / Download + Delete (remote). Double-click navigates into folders. Path TextBox accepts manual entry. |
| `Services/SessionManager.cs` | `AddSftp(profile, ISftpSession)` + `OpenSession.SftpSession` property. `DisposeAsync` cleans up the SFTP client alongside SSH/RDP. |
| `App.xaml.cs` | DI registers `ISftpHandler` as the Core `SftpHandler` (no PuTTYNG branch — SFTP always uses SSH.NET). |
| `MainWindow.xaml.cs` | `OpenSftpTabAsync(profile)` — credential resolution → factory → session → VM → view → tab. Wires `view.OpenSshRequested` to `OpenSshTabAsync` and `session.TransferCompleted` to the audit log. |
| `Views/ConnectionsPane.xaml.cs` | Right-click context menu on the connection tree gains "Open SFTP" for SSH-protocol profiles. Fires `OpenSftpRequested`. |
| `Views/SshSessionView.xaml`/`.xaml.cs` | Toolbar gains a "Files" button between the existing controls; click raises `OpenSftpRequested` event. |

## Transfer queue

Single-channel serial. One transfer at a time across uploads + downloads. The user can queue many — `Enqueue{Upload,Download}` appends to a `Queue<TransferRequest>`, and `PumpQueueAsync` (a single in-flight task gated by `_pumpRunning`) drains it. After every transfer completes the active pane is refreshed so size / new entries reflect.

Why serial: SSH.NET's `SftpClient` is single-channel from our perspective. Concurrent calls aren't safe. Parallel transfers would need either multiple `SftpClient` instances (= more TCP + auth) or careful queuing of low-level SFTP packets. MVP punts that — most users don't need parallelism for one-off uploads.

## Audit logging

Every transfer logs to the audit table as `AuditAction.FileTransfer`. The `Detail` string carries the human-readable summary:

```
SFTP upload 4.2 MB in 1.3s: C:\Users\me\report.pdf ↔ /home/me/report.pdf
SFTP download FAILED: (stream) ↔ /etc/shadow — Permission denied (EACCES).
```

Both paths are recorded so the user can later answer "where did this file come from / go to."

## Known limitations (MVP)

- **No directory upload/download yet** — single files only. Recursive copy is a follow-up.
- **No drag-and-drop from Windows Explorer** — user has to right-click → Upload from the local pane. The local pane navigates Windows directories directly; D&D would just be syntactic sugar.
- **No resume on disconnect** — a transfer that fails mid-stream is lost. SSH.NET supports range reads/writes; wiring resumption is a follow-up.
- **No symlink-following toggle** — symlinks render with a `IsSymlink=true` flag but the UI doesn't distinguish them visually yet.
- **No edit-in-place** — open remote file, edit locally, push changes back. Could layer on top.
- **ServerPrompt auth + missing password may deadlock** — the SFTP tab has no terminal to render the broker's prompt into. Stored / key auth is fine. A modal credential dialog for SFTP-specific prompts is a follow-up.
- **Permission display only, no chmod UI yet** — `SftpEntry.Permissions` is captured but not surfaced; chmod menu is a follow-up.

## References

- IETF draft-ietf-secsh-filexfer-13 — SFTP protocol (never finalized but widely implemented).
- OpenSSH 9.0 release notes — SCP-using-SFTP switch.
- SSH.NET docs: <https://github.com/sshnet/SSH.NET>
- See also `docs/ssh-terminal.md` for the underlying SSH transport, credential resolution, and the cross-launch pattern.
