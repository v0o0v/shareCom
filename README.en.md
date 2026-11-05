[한국어](README.md) | **English**

# ShareCom

Control 2–4 Windows PCs on the same Wi-Fi network with a single mouse and keyboard, share the clipboard between them, and move files across.

- Download: [latest release](https://github.com/v0o0v/shareCom/releases/latest) (`ShareCom.exe`, single file, no install needed)
- Design notes: [docs/design.md](docs/design.md) (Korean)
- Manual test checklist: [docs/manual-test.md](docs/manual-test.md) (Korean)

> The app's user interface is in Korean. Menu names below are given in English with the original Korean label in parentheses.

## Features

- Automatic discovery of PCs on the same network; one-time pairing with a 6-digit code shown on both screens
- TLS encryption, on by default (can be turned off in **Settings…** (`설정…`))
- Push the cursor past a screen edge to move to the neighboring PC; the keyboard follows
- Up to 4 PCs, each with multiple monitors. Drag PCs into place in **Screen layout…** (`화면 배치…`) to match your real desk (any side, DPI scaling taken into account). The layout is synced to every PC, and you can move directly from one remote PC to another.
- Automatic introduction: a new PC only needs to pair with one existing PC to become paired with all of them. Unpairing also propagates to every PC.
- Hotkeys: `Scroll Lock` toggles the edge lock, `Ctrl+Alt+1~4` jumps straight to a PC, `Ctrl+Alt+Esc` is an emergency return to your own PC. The first two can be changed in **Settings…**.
- If the connection drops, control returns to your own PC immediately; when the other PC comes back, it reconnects automatically
- Clipboard sharing: plain text, rich text (HTML, RTF) and images (PNG), with no size limit. Mouse and keyboard input takes priority even while a large clipboard item is being sent. Content that password managers mark as "do not share" is never sent.
- File transfer: copy files in File Explorer on one PC and paste them in File Explorer on another; the contents are fetched from the source PC at the moment you paste (whole folders, no size limit). You can also pick files through **Send files** (`파일 보내기`) in the tray menu; the receiving PC chooses **Accept** (`받기`) / **Decline** (`거절`), and files are saved to `Downloads\shareCom` (duplicates become `name (2)`).
- Start at sign-in: registered in Task Scheduler with highest privileges, so it starts without a UAC prompt. You are asked once on first run, and can change it in **Settings…**.
- Settings window: start at sign-in, clipboard sharing, encryption, edge lock, hotkeys, and information about this PC.

## Getting started

1. Copy `ShareCom.exe` to each PC and run it. Click **Yes** on the UAC prompt.
   - If SmartScreen blocks it as an "unknown publisher", click **More info** → **Run anyway** (the exe is not code-signed).
2. A tray icon appears near the clock: gray means no connection, green means connected, blue means you are controlling another PC.
3. Click the tray icon → **Pair with a new PC** (`새 PC와 짝 맺기`), choose the other PC, and click **Pair** (`짝 맺기`) on both PCs if the 6-digit codes match.
4. Arrange the PCs in **Screen layout…** (`화면 배치…`) if they are not side by side.

Run the same version on every PC. If something goes wrong, open **Settings…** → **Open log folder** (`기록 폴더 열기`) and share `log.txt`.

### Known limitations

- `Ctrl+Alt+Del`, UAC prompts and the lock screen must be operated on that PC directly.
- PCs do not connect while sitting at the sign-in screen; they connect once you sign in.
- When pasting files through File Explorer, the source PC must stay on until the copy finishes.

## Build

Requires the .NET 10 SDK.

```bash
dotnet test
```

```bash
dotnet publish src/ShareCom.App -c Release -o publish
```

Copy the single `publish\ShareCom.exe` to each PC and run it (runtime included, no install needed). It runs with administrator privileges and adds a firewall rule on first run.

To try it during development without a UAC prompt, build with `-p:NoAdmin=true`. In that mode it cannot control elevated windows or add the firewall rule.

## Project layout

| Path | Contents |
|---|---|
| `src/ShareCom.Core` | Protocol, connection / pairing / reconnection (`Node`), input routing (`InputRouter`), layout math and sync (`DeskLayout`, `LayoutSync`), automatic introduction (`TrustSync`), clipboard sync, file transfer (`FileTransferService`). Uses no Windows APIs. |
| `src/ShareCom.App` | Tray UI, layout editor (`LayoutEditorForm`), low-level input hooks, `SendInput` playback, clipboard watcher, virtual files for File Explorer (`VirtualFileDataObject`), settings storage, firewall |
| `tests/ShareCom.Core.Tests` | Automated tests |

Settings and logs are stored in `%APPDATA%\ShareCom\`.
