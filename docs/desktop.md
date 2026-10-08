# The desktop app

`SemanticRouter` is the router as a desktop app: an icon in the tray (the menu bar on macOS) that runs the router and shows the dashboard. Nothing stays open in a terminal, and it can start when you sign in. It is the same router and the same dashboard as the [`semantic-router` tool](host.md), with the same configuration directory, so you can switch between them freely.

![The dashboard in the app window](images/dashboard.png)

## Install

Download the archive for your machine from the [releases](https://github.com/siebe41/SemanticRouter.NET/releases). It is self-contained: no .NET installation is needed.

| Platform | Download | Then |
|---|---|---|
| Windows 10/11 | `SemanticRouter-<version>-win-x64.zip` (or `win-arm64`) | Unzip anywhere, for example `%LOCALAPPDATA%\Programs\SemanticRouter`, and run `SemanticRouter.exe`. |
| macOS 12+ | `SemanticRouter-<version>-osx-arm64.zip` (Apple silicon) or `osx-x64` (Intel) | Move `SemanticRouter.app` to Applications. The app is not signed yet: the first time, right-click it and choose **Open**, or run `xattr -dr com.apple.quarantine /Applications/SemanticRouter.app`. |
| Linux | `SemanticRouter-<version>-linux-x64.tar.gz` (or `linux-arm64`) | Unpack, for example to `~/.local/opt/SemanticRouter`, and run `SemanticRouter`. See [Linux](#linux) for the tray and the app window. |

To build it yourself: `dotnet run --project src/SemanticRouter.Desktop`, or `deploy/package-desktop.sh <rid> <version> <dir>` for the same archives the release makes.

## Using it

Start it and the dashboard opens. Close the dashboard and the router keeps running; the tray icon brings it back (click it, or choose **Open dashboard**). Starting the app again while it runs also opens the dashboard.

The window opens at a size that fits the screen it is on (title bar included, whatever the display scaling), and after that where you left it: its position, size and whether it was maximized are kept in `desktop.json`. If that screen is gone, it opens on the main screen again.

The tray menu:

| Item | What it does |
|---|---|
| **Open dashboard** | Opens the dashboard where you chose (below). |
| Status line | Where the router listens, whether it is the app's own or one already running, or why it could not start. |
| **Restart router** | Starts the app's own router again, for example after it failed to start. |
| **Open the dashboard in** → **App window** / **Web browser** | Your choice of where the dashboard opens, remembered in `desktop.json`. The app window uses the platform's own web view (WebView2, WebKit, WebKitGTK or WPE WebKit); links that leave the router open in your browser. |
| **Start at sign-in** | Starts the app, in the tray, whenever you sign in. |
| **Show the log**, **Show the configuration folder** | The router's log (`logs/semantic-router.log`) and its files. |
| **About SemanticRouter** | The version, and links to the GitHub repository, the documentation, issues, releases and the licence. |
| **Quit SemanticRouter** | Stops the app's router and quits. |

`SemanticRouter --quit` quits a running app from a script.

## The app and the background service

The app and [`semantic-router service`](host.md#run-it-in-the-background) both run the router without a terminal; use whichever you like. They cooperate rather than compete for the port:

- **If a router already runs for the configuration directory** (the background service, or `semantic-router` in a terminal), the app uses it: the status line says so, the dashboard opens on it, and quitting the app leaves it running.
- **Otherwise the app runs the router itself**, and quitting the app stops it.
- If both start at sign-in, whichever binds the port first serves, and the app attaches to the service.

The background service has no icon but runs even when no one is signed in to a desktop (with lingering on Linux). The app has the icon, the window and no console flash at sign-in on Windows.

## Where things live

Everything is in the router's [configuration directory](host.md#where-things-live), shared with the tool:

| | |
|---|---|
| `desktop.json` | The app's own settings: where the dashboard opens, and where its window was. |
| `logs/semantic-router.log` | What the router logged (the app has no console). Rolled over to `.1` past 10 MB. |
| `webview/` | The app window's web view profile. |

**Start at sign-in** writes `~/.config/autostart/semantic-router.desktop` (Linux), `~/Library/LaunchAgents/io.github.siebe41.semantic-router.desktop.plist` (macOS) or the value `SemanticRouter Desktop` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (Windows; listed in Task Manager → Startup apps). Moving the app afterwards needs the item turned off and on again.

Options are the tool's: `--config-dir`, `--urls` and `--SemanticRouter:<key>=<value>` work the same way.

## Finding your CLIs

An app started from the Dock, Finder or a launcher does not get the PATH your shell sets up, so `claude`, `codex` and `copilot` installed in `~/.local/bin`, Homebrew or an npm prefix would not be found. On macOS and Linux the app asks your login shell for its PATH once when it starts, and adds `~/.local/bin`, `~/.npm-global/bin`, `/opt/homebrew/bin` and `/usr/local/bin` when they exist. Windows programs get your user PATH anyway.

## Linux

- **The tray icon** needs a desktop that shows StatusNotifier icons: KDE Plasma, Cinnamon, XFCE and most others do; on GNOME install the *AppIndicator and KStatusNotifierItem Support* extension. Without one the app still runs; start it again to open the dashboard, and `SemanticRouter --quit` to quit.
- **The app window** needs WebKitGTK 4.1 or WPE WebKit 2.0 (`libwebkit2gtk-4.1-0` on Debian and Ubuntu, `webkit2gtk4.1` on Fedora). Without either, **App window** is greyed out and the dashboard opens in your browser.

## Status

Exercised on Linux (X11) only so far: hosting the router, attaching to a running one, the single-instance hand-over, `--quit`, and the dashboard in the app window on WebKitGTK. The Windows and macOS builds compile and their logic is tested in CI, but the tray, the window and start at sign-in have not yet been tried on those systems.
