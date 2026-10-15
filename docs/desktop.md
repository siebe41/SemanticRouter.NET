# The desktop app

`SemanticRouter` is the router as a desktop app: an icon in the tray (the menu bar on macOS) that runs the router and shows the dashboard. Nothing stays open in a terminal, and it can start when you sign in. It is the same router and the same dashboard as the [`semantic-router` tool](host.md), with the same configuration directory, so you can switch between them freely.

![The dashboard in the app window](images/dashboard.png)

## Install

Download the installer for your machine from the [releases](https://github.com/siebe41/SemanticRouter.NET/releases). It is self-contained: no .NET installation is needed, and no administrator rights.

| Platform | Installer | What it does |
|---|---|---|
| Windows 10/11 | `SemanticRouter-win-x64-Setup.exe` | Installs for you only, into `%LOCALAPPDATA%\SemanticRouter`, adds a Start menu entry, and starts the app. Remove it in **Settings → Apps**. |
| macOS 12+ | `SemanticRouter-osx-arm64-Setup.pkg` (Apple silicon) or `osx-x64` (Intel) | Installs `SemanticRouter.app` into Applications (or your own Applications folder) and starts it. |
| Linux | `SemanticRouter-linux-x64.AppImage` | One file: make it executable (`chmod +x`) and run it, from wherever you keep it. See [Linux](#linux) for the tray and the app window. |

The first start is the same as from the archives: the [welcome window](#using-it) offers the background service and start at sign-in.

**Until the release is [signed](#signing)**, the operating system warns on first start. On Windows, SmartScreen says "Windows protected your PC": choose **More info → Run anyway**. On macOS, if the installer is blocked, right-click it and choose **Open**, or allow it under **System Settings → Privacy & Security**.

### Signing

The release workflow signs the installers when the repository has the credentials, and builds them unsigned otherwise:

| Platform | What it takes | Repository secrets | Repository variables |
|---|---|---|---|
| Windows | An [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/) account and certificate profile, and an app registration allowed to sign with it | `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | `AZURE_SIGNING_ENDPOINT` (e.g. `https://weu.codesigning.azure.net`), `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE` |
| macOS | An Apple Developer account: the *Developer ID Application* and *Developer ID Installer* certificates (exported as `.p12`, base64) and an app-specific password for notarization | `APPLE_BUILD_CERTIFICATE_BASE64`, `APPLE_INSTALLER_CERTIFICATE_BASE64`, `APPLE_P12_PASSWORD`, `APPLE_ID`, `APPLE_APP_PASSWORD`, `APPLE_TEAM_ID` | `APPLE_SIGN_APP_IDENTITY`, `APPLE_SIGN_INSTALL_IDENTITY` (the certificates' names, e.g. `Developer ID Application: Ada Lovelace`) |

The app is built in jobs that hold no secret and can only read the repository (`installer-build`, `desktop-build`): `deploy/publish-desktop.sh` publishes it on each installer's own OS. The signing jobs download that build and run no `dotnet build` or `publish`, so no package's build code runs next to a signing credential; they only install vpk, which packs, signs and uploads. The macOS job imports the certificates into a temporary keychain and stores a `notarytool` profile, and vpk signs, notarizes and staples the app and the `.pkg`.

Locally, `deploy/package-installer.sh` builds and signs in one go with the same settings as environment variables (see the script's header); with `PUBLISH_DIR` it packages an app already published there instead.

Independently of code signing, every installer, archive and update package has a build provenance attestation and a line in the release's `SHA256SUMS`, made after signing, so you can check a download came from this repository's release workflow: see [releases.md](releases.md). The release's SBOM and `THIRD-PARTY-NOTICES.txt` cover the app too; the notices also ship next to the executable.

### Updates

An installed app updates itself from GitHub Releases. It looks a minute after it starts and then twice a day, and downloads a new version in the background (only the changes when it can). The tray menu then offers **Restart to update to …**; otherwise the update is applied the next time you quit the app. When the background service runs, it is stopped for the update and started again with the new version, with the same configuration directory and options. **Check for updates** in the tray menu turns the checks off (kept in `desktop.json`).

Untick **Install updates when quitting** to decide yourself: the app then only says a new version is available (in the tray menu, and once as a system notification) and downloads nothing until you choose **Restart to update to …**, which downloads it, applies it and starts the app again. Quitting never applies it.

From a script: `SemanticRouter --update` downloads and applies an available update and exits (0 when up to date or updating, 1 when the update could not be applied).

An administrator can turn updates off, take them from an internal feed, make them notify-only, or require them to be signed by a given publisher, for every user of the machine: see [Managed deployment](#managed-deployment).

### Uninstall

- **Windows:** **Settings → Apps → SemanticRouter → Uninstall** (or `%LOCALAPPDATA%\SemanticRouter\Update.exe uninstall`). It removes the background service and the app's sign-in entry first, then the program and the Start menu entry.
- **macOS and Linux** have no uninstaller: turn off **Keep the router running in the background** and **Start at sign-in** in the tray menu (or run `SemanticRouter --uninstall-service`), quit, then delete `SemanticRouter.app` or the AppImage.

The configuration directory (your configuration, the state database, the logs) stays; delete it yourself if you no longer want it (see [the tool's docs](host.md) for where it is).

### The archives

The releases also have the plain archives, for people who prefer them. They do not update themselves.

| Platform | Download | Then |
|---|---|---|
| Windows 10/11 | `SemanticRouter-<version>-win-x64.zip` (or `win-arm64`) | Unzip anywhere, for example `%LOCALAPPDATA%\Programs\SemanticRouter`, and run `SemanticRouter.exe`. |
| macOS 12+ | `SemanticRouter-<version>-osx-arm64.zip` (Apple silicon) or `osx-x64` (Intel) | Move `SemanticRouter.app` to Applications. The app is not signed yet: the first time, right-click it and choose **Open**, or run `xattr -dr com.apple.quarantine /Applications/SemanticRouter.app`. |
| Linux | `SemanticRouter-<version>-linux-x64.tar.gz` (or `linux-arm64`) | Unpack, for example to `~/.local/opt/SemanticRouter`, and run `SemanticRouter`. |

To build it yourself: `dotnet run --project src/SemanticRouter.Desktop`, `deploy/package-desktop.sh <rid> <version> <dir>` for the same archives the release makes, or `deploy/package-installer.sh <rid> <version> <dir>` for an installer (needs `dotnet tool install -g vpk`; the macOS installer must be built on macOS). On Windows, `deploy\update-windows.ps1` pulls, rebuilds and reinstalls the tool, the background service and the app in one go (`-InstallService` the first time; `Get-Help .\deploy\update-windows.ps1` for the options).

## Using it

Start it and the dashboard opens. The first time, a welcome window offers to keep the router running in the background and to show the app in the tray when you sign in; both are ticked, and both can be changed later in the tray menu. Close the dashboard and the router keeps running; the tray icon brings it back (click it, or choose **Open dashboard**). Starting the app again while it runs also opens the dashboard.

The window opens at a size that fits the screen it is on (title bar included, whatever the display scaling), and after that where you left it: its position, size and whether it was maximized are kept in `desktop.json`. If that screen is gone, it opens on the main screen again.

The tray menu:

| Item | What it does |
|---|---|
| **Open dashboard** | Opens the dashboard where you chose (below). |
| **Needs a decision: …** | Shown when something needs you (a connector down, a subscription that keeps hitting its limit, …); opens the page to decide on. See [notices](dashboard.md#digest-and-notifications). |
| **This week: 1,234 prompts · 62% local · saved $4.10** | The [digest](dashboard.md#digest-and-notifications) of the last 7 days; opens the Overview. |
| **Restart to update to …** | Shown when an update has been downloaded; see [Updates](#updates). |
| **Router learned N things this week** | Shown when [automatic tuning](routing-quality.md#automatic-tuning) applied changes in the last 7 days; opens the Router learned page. |
| Status line | Where the router listens, whether it is the app's own or one already running, or why it could not start. |
| **Restart router** | Restarts the router: the background service's, or the app's own (for example after it failed to start). |
| **Open the dashboard in** → **App window** / **Web browser** | Your choice of where the dashboard opens, remembered in `desktop.json`. The app window uses the platform's own web view (WebView2, WebKit, WebKitGTK or WPE WebKit); links that leave the router open in your browser. |
| **Keep the router running in the background** | The background service on or off; see [below](#the-app-and-the-background-service). |
| **Start at sign-in** | Shows the app in the tray whenever you sign in. The router itself starts at sign-in through the background service. |
| **Check for updates** | Installed app only: whether it looks for updates. |
| **Install updates when quitting** | Installed app only: whether a downloaded update is applied when you quit. Unticked, an update is only announced and applied when you choose **Restart to update**. Greyed out when your organization's policy makes updates notify-only. |
| **Show the log**, **Show the configuration folder** | The router's log (`logs/semantic-router.log`) and its files. |
| **About SemanticRouter** | The version, and links to the GitHub repository, the documentation, issues, releases and the licence. |
| **Quit SemanticRouter** | Stops the app's router and quits. |

When something [needs a decision](dashboard.md#digest-and-notifications), the app also shows a system notification, once per notice (Notification Center on macOS, `notify-send` on Linux, a toast on Windows). `Notifications:Enabled: false` in the router's configuration turns them off.

`SemanticRouter --quit` quits a running app from a script.

## The app and the background service

The router can run in two places, and the app shows it either way:

- **In the background (recommended).** Tick **Keep the router running in the background** in the tray menu (the welcome window offers it on first start). The operating system's own service manager then runs `SemanticRouter --headless` (this same program, with no window or tray) whenever you sign in, restarts it if it fails, and keeps it answering when the app is closed. The app shows that router; the status line says "Using the router already running on …", **Restart router** restarts the service, and quitting the app leaves it running. It is the same per-user service as [`semantic-router service`](host.md#run-it-in-the-background) (a systemd user unit, a launchd agent, or a sign-in entry on Windows), run by the app instead of the tool. Being a GUI program, it starts on Windows without a console window.
- **Inside the app.** With the box unticked the app runs the router itself, and quitting the app stops it.

Switching hands the port over: the app stops its own router before the service starts, and starts it again when the service is removed. If the service cannot be set up (no systemd user session, for example), the app keeps running the router itself and the tray menu says why.

If a router already runs for the configuration directory when the app starts (the service, or `semantic-router` in a terminal), the app uses it rather than competing for the port.

For scripts and installers, without the tray:

| Command | |
|---|---|
| `SemanticRouter --install-service` | Install (or update) and start the background service. Exit code 0 when it worked; the outcome is also written to the log. |
| `SemanticRouter --uninstall-service` | Stop and remove it. |
| `SemanticRouter --headless` | Run only the router, as the service does. Logs go to `logs/semantic-router.log`. |

## Where things live

Everything is in the router's [configuration directory](host.md#where-things-live), shared with the tool:

| | |
|---|---|
| `desktop.json` | The app's own settings: where the dashboard opens, where its window was, whether the welcome window was shown, and the two update switches. |
| `logs/semantic-router.log` | What the router logged (the app has no console). Rolled over to `.1` past 10 MB. |
| `webview/` | The app window's web view profile. |

**Start at sign-in** writes `~/.config/autostart/semantic-router.desktop` (Linux), `~/Library/LaunchAgents/io.github.siebe41.semantic-router.desktop.plist` (macOS) or the value `SemanticRouter Desktop` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (Windows; listed in Task Manager → Startup apps). Moving the app afterwards needs the item turned off and on again.

Options are the tool's: `--config-dir`, `--urls` and `--SemanticRouter:<key>=<value>` work the same way.

## Managed deployment

For administrators deploying the app to many machines with Intune, Configuration Manager (SCCM), Jamf or similar.

### What gets installed, and where

The installers install for one user, with no administrator rights, and that is the only kind there is today: **there is no per-machine package yet** (no MSI, and no `.pkg` that installs for every user and keeps them from changing it). Run the installer in each user's context, not as SYSTEM or root, or it installs into that account's profile instead.

| | Program | What it leaves running |
|---|---|---|
| Windows | `%LOCALAPPDATA%\SemanticRouter` (the app in `current\`, Velopack's `Update.exe` next to it), a Start menu entry, and an entry under **Settings → Apps** | Nothing until the person chooses in the welcome window (both boxes are ticked): the background service is the value `SemanticRouter` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, which starts the router (`SemanticRouter.exe --headless`, no window) at sign-in; what it runs is kept in `semantic-router.service.json` in the configuration directory; start at sign-in is the value `SemanticRouter Desktop` in the same key. |
| macOS | `SemanticRouter.app` in `~/Applications` (installed for the current user) or `/Applications` | The background service is the launchd agent `~/Library/LaunchAgents/io.github.siebe41.semantic-router.plist`; start at sign-in is `~/Library/LaunchAgents/io.github.siebe41.semantic-router.desktop.plist`. |
| Linux | The AppImage, wherever it was put | The background service is the systemd user unit `~/.config/systemd/user/semantic-router.service`; start at sign-in is `~/.config/autostart/semantic-router.desktop`. |

All of these are per user. The configuration directory (configuration, state database, logs) is the router's usual one ([host.md](host.md#where-things-live)). `SemanticRouter --install-service` and `--uninstall-service` set the background service up or remove it from a script, without the welcome window; see [the app and the background service](#the-app-and-the-background-service).

### Deploying the installer

- **Windows (Intune as a Win32 app, Configuration Manager, a sign-in script):** `SemanticRouter-win-x64-Setup.exe --silent` installs with no window. Velopack's Setup also takes `--log <file>` (write a log), `--verbose`, and `--installto <dir>` (install somewhere else than `%LOCALAPPDATA%\SemanticRouter`). Install in the user's context (in Intune, *Install behavior: User*). Setup starts the app when it has installed it, so the person sees the welcome window at once. To uninstall: `%LOCALAPPDATA%\SemanticRouter\Update.exe uninstall --silent`, which also removes the background service and the sign-in entry. For a detection rule, `%LOCALAPPDATA%\SemanticRouter\current\SemanticRouter.exe` exists.
- **macOS (Jamf, an MDM):** `SemanticRouter-osx-arm64-Setup.pkg` (or `osx-x64`). `installer -pkg SemanticRouter-osx-arm64-Setup.pkg -target CurrentUserHomeDirectory` installs into the user's `~/Applications`, as the release tests do. Installed as root (`-target /`, how most MDMs run packages), it goes into `/Applications` and belongs to root; the app's own updates run as the signed-in user and have not been tried against such an install, so turn them off in the policy (`Updates:Mode` `Off`) and deploy each new version with your MDM.
- **Linux:** copy the AppImage into place and make it executable; it does not install anything else.

Check what you deploy before you deploy it: [releases.md](releases.md) shows how to verify an installer with `gh attestation verify` and the release's `SHA256SUMS`, and the installers are code-signed when the release has signing certificates ([Signing](#signing)).

### Controlling updates

The [machine-wide policy](enterprise.md) (the registry on Windows, a configuration profile on macOS, `/etc/semantic-router/policy.json` on Linux) decides updates for every user of the machine, and the user's tray menu cannot override it:

| Policy | Effect |
|---|---|
| `Updates:Mode` `Off` | No update checks at all; the update items are gone from the tray menu. Deploy new versions yourself. |
| `Updates:Mode` `Feed` and `Updates:Feed` | Updates only from your own feed (below), never from GitHub. |
| `Updates:Apply` `Notify` | Updates are announced, and downloaded and applied only when the person chooses **Restart to update**; never when the app quits. **Install updates when quitting** is greyed out. Works with either `Mode`. |
| `Updates:Publisher` | An update is applied only when the app in it is signed by this publisher (Windows: the Authenticode certificate's subject or thumbprint; macOS: the Developer ID team ID); otherwise it is refused and deleted. On Linux it refuses every update, since AppImages are not signed. See [enterprise.md](enterprise.md#update-publisher). |

`semantic-router doctor` shows the policy and, on its `desktop app updates` line, where updates come from and what the policy adds.

### Mirror the update feed

The update feed is static files, so any HTTPS server (or a file share the machines can read, as a folder path) can serve it. Each platform is a channel: `win-x64`, `osx-arm64`, `osx-x64` and `linux-x64`. For each release, copy from its [GitHub release](https://github.com/siebe41/SemanticRouter.NET/releases) into one folder:

- `releases.<channel>.json` for each channel you serve (it lists the packages and their SHA-256 checksums),
- `SemanticRouter-<version>-<channel>-full.nupkg`, and
- `SemanticRouter-<version>-<channel>-delta.nupkg` when there is one (it updates from the previous version with a much smaller download; without it the app downloads the full package).

Replace `releases.<channel>.json` with each new release's; the app reads the newest version from it and the package names it lists, so keep at least the packages it names. The installers (`*-Setup.exe`, `*-Setup.pkg`, the AppImage) and the zip archives are not part of the feed. Before you publish a release internally, check its files against the release's `SHA256SUMS` and attestation ([releases.md](releases.md)); the app also checks each package against the checksum in `releases.<channel>.json`. Then point the policy at the folder's URL: `Updates:Mode` `Feed` and `Updates:Feed` `https://updates.corp.example/semantic-router`.

## Finding your CLIs

An app started from the Dock, Finder or a launcher does not get the PATH your shell sets up, so `claude`, `codex` and `copilot` installed in `~/.local/bin`, Homebrew or an npm prefix would not be found. On macOS and Linux the app asks your login shell for its PATH once when it starts, and adds `~/.local/bin`, `~/.npm-global/bin`, `/opt/homebrew/bin` and `/usr/local/bin` when they exist. Windows programs get your user PATH anyway.

## Linux

- **The tray icon** needs a desktop that shows StatusNotifier icons: KDE Plasma, Cinnamon, XFCE and most others do; on GNOME install the *AppIndicator and KStatusNotifierItem Support* extension. Without one the app still runs; start it again to open the dashboard, and `SemanticRouter --quit` to quit.
- **The app window** needs WebKitGTK 4.1 or WPE WebKit 2.0 (`libwebkit2gtk-4.1-0` on Debian and Ubuntu, `webkit2gtk4.1` on Fedora). Without either, **App window** is greyed out and the dashboard opens in your browser.

## Status

Exercised on Linux (X11) only so far: hosting the router, attaching to a running one, the single-instance hand-over, `--quit`, and the dashboard in the app window on WebKitGTK. The Windows and macOS builds compile and their logic is tested in CI, but the tray, the window and start at sign-in have not yet been tried on those systems.
