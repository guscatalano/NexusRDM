# UX Prototypes

Interactive HTML prototypes for NexusRDM. Open any file directly in a browser — no build step needed.

## Files

| File | Description |
|---|---|
| `prototype-v1.html` | Initial full-app prototype — all 8 screens, clickable navigation |

## Screens covered in v1

- Main shell (sidebar nav + tab view)
- Connections pane (tree, groups, search, toolbar)
- SSH terminal session
- RDP session (mstsc host panel)
- Audit log
- Settings
- New Connection dialog
- Credential Prompt dialog

## How to use

Open `prototype-v1.html` in any browser. Everything is interactive:
- Left sidebar icons switch between Connections / Audit Log / Settings
- Click connection tree items to open session tabs
- Click **+ New** to open the new connection dialog
- Click `homelab-rdp` to see the credential prompt
- Tabs are closeable with ✕

## Iteration history

| Version | Date | Changes |
|---|---|---|
| v1 | 2026-04-24 | Initial prototype, all 8 screens |
