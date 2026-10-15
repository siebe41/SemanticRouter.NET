# Machine-wide policy

An administrator can set a policy for the whole machine that the user's own configuration cannot override: not the configuration file, not the dashboard, not the desktop app's preferences. It is read once when the router (the `semantic-router` tool, its service or the desktop app) starts, from a place only administrators can write, so device management can push it:

| OS | Where | Pushed with |
|---|---|---|
| Windows | registry key `HKLM\Software\Policies\SemanticRouter` | Group Policy Preferences, Intune (a registry or custom OMA-URI profile), any tool that writes HKLM |
| macOS | managed preferences domain `io.github.siebe41.semantic-router` (`/Library/Managed Preferences/io.github.siebe41.semantic-router.plist`) | a configuration profile from your MDM |
| Linux, containers | `/etc/semantic-router/policy.json` (root-owned) | configuration management, or a read-only mount into the container |

With no policy, nothing changes: single-user installs keep working with no extra steps.

## What it does

- **At start-up, what breaks the policy is turned off, not refused.** A model on a connector the policy does not allow is disabled, a feature the policy switches off is turned off, and so on. Each change is listed with the router's warnings: on the dashboard, in the log and in `semantic-router doctor`. A connector the policy blocks is never started, even if a model still names it.
- **A change that would break it is refused.** `PUT /router/api/config`, **Validate** and **Apply** on the dashboard, a rollback to an older version and the router's own tuning all go through the same check, which answers `400` and names each setting, e.g. `Not allowed: Model 'claude-cli' is turned off: connector 'claude': Cli connectors are turned off by your organization's policy.`
- **A policy that cannot be read stops the router.** A file that is not valid JSON, or a setting with a misspelt name, is an error with the file named, never "no policy": the router does not run without restrictions an administrator meant to set. The desktop app turns updates off in that case.
- `semantic-router init` does not add (or ask about) a CLI the policy turns off, even with `--yes`; it says which ones it left out.
- `semantic-router doctor` prints the policy and where it came from, and `GET /router/api/policy` returns it.

## Settings

Every setting is optional.

| Setting | Example | Effect |
|---|---|---|
| `DisabledConnectorTypes` | `["Cli", "Copilot"]` | Connector types that may not be used. `Cli` and `Copilot` use whatever subscription account the vendor CLI is signed into; see [TERMS.md](TERMS.md#using-it-at-work). Also `OpenAICompatible`, `Anthropic`, `Responses`. |
| `DisabledPresets` | `["Codex"]` | CLI presets that may not be used: `ClaudeCode`, `GitHubCopilot`, `Codex`, `Custom`. |
| `AllowedEndpoints` | `["api.anthropic.com", "*.corp.example", "10.0.0.5:8443"]` | The only hosts connectors, the embeddings endpoint and webhooks may reach: a host name, a domain and its subdomains (`*.corp.example`), or an IP address, each with an optional port. This machine (loopback) is always allowed. Empty or absent: any host. |
| `LocalHosts` | `["gpu.corp.example"]` | When set, the only hosts a model or the embeddings endpoint may be marked local on (`IsLocal`); this machine always counts. A model elsewhere, or served by a CLI or Copilot, is treated as not local, so local-only requests (local-only routes and profiles, conversations that contained a credential) never go there. Use it so a corporate gateway that forwards to a cloud provider cannot be marked local. `[]`: only this machine. |
| `RequireLocalOnly` | `true` | Only local models may be used (after `LocalHosts`), and only embeddings that keep prompts local; remote embeddings fall back to the in-process hashing provider. |
| `StorePrompts` | `"Off"` | The most prompt text the activity log may keep: `Off` or `Redacted` caps `State:StorePrompts`. |
| `MaxRetentionDays` | `7` | The most days activity (with its prompt previews) and feedback are kept; caps `State:Retention:ActivityDays`. |
| `Dashboard` | `"ReadOnly"` | `ReadOnly`: everyone gets the read-only role, so the router can be watched but not changed from the dashboard or admin API (the dashboard shows it read-only). `Off`: the dashboard and its API answer `403`. |
| `Updates:Mode` / `Updates:Feed` | `"Off"`, or `"Feed"` and `"https://updates.corp.example/semantic-router"` | Desktop app updates. `Off`: never checked; the menu item is hidden. `Feed`: only from this feed (a URL or a folder of Velopack packages, e.g. your internal mirror of the GitHub releases); it wins over `SEMANTICROUTER_UPDATE_SOURCE`. The user's **Check for updates** switch still applies within it. A feed over plain HTTP (other than to this machine) is always refused. |
| `Features:Memory` | `false` | No memory sources are read. |
| `Features:Judge` | `false` | The judge, which sends a sample of answers to another model, is off. |
| `Features:ModelTrials` | `false` | No trials of cheaper models per route. |
| `Features:TuningApply` | `false` | Automatic tuning only suggests changes (`Tuning:Mode` `Suggest`); someone applies them on the dashboard. |
| `Features:Webhooks` | `false` | No webhooks are used for the digest and notices. |

The policy can only restrict: a `Features` switch set to `true` does not turn on what the configuration leaves off.

## Samples

### Linux and containers

`/etc/semantic-router/policy.json`, owned by root and not writable by others:

```json
{
  "DisabledConnectorTypes": ["Cli", "Copilot"],
  "AllowedEndpoints": ["api.anthropic.com", "llm-gateway.corp.example"],
  "LocalHosts": [],
  "StorePrompts": "Redacted",
  "MaxRetentionDays": 14,
  "Features": { "Judge": false, "Webhooks": false }
}
```

In a container, mount it read-only: `-v /etc/semantic-router/policy.json:/etc/semantic-router/policy.json:ro`.

### Windows

Each registry value is a setting. A list is a multi-string (`REG_MULTI_SZ`), a switch a DWORD (`1` or `0`), a number a DWORD, anything else a string; a string can also hold JSON. A section (`Features`, `Updates`) is a subkey.

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\SemanticRouter]
"DisabledConnectorTypes"=hex(7):43,00,6c,00,69,00,00,00,43,00,6f,00,70,00,69,00,6c,00,6f,00,74,00,00,00,00,00
"AllowedEndpoints"="[\"api.anthropic.com\", \"llm-gateway.corp.example\"]"
"MaxRetentionDays"=dword:0000000e
"Dashboard"="ReadOnly"

[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\SemanticRouter\Features]
"Judge"=dword:00000000

[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\SemanticRouter\Updates]
"Mode"="Feed"
"Feed"="https://updates.corp.example/semantic-router"
```

The `hex(7)` value is the multi-string `Cli`, `Copilot`; in Group Policy Preferences or Intune, enter it as a multi-string with one value per line.

### macOS

A configuration profile payload for the domain `io.github.siebe41.semantic-router`:

```xml
<dict>
  <key>PayloadType</key>
  <string>io.github.siebe41.semantic-router</string>
  <key>PayloadIdentifier</key>
  <string>com.corp.semantic-router.policy</string>
  <key>PayloadUUID</key>
  <string>2F9C4F6A-0000-4000-8000-000000000000</string>
  <key>PayloadVersion</key>
  <integer>1</integer>
  <key>DisabledConnectorTypes</key>
  <array><string>Cli</string><string>Copilot</string></array>
  <key>AllowedEndpoints</key>
  <array><string>api.anthropic.com</string><string>llm-gateway.corp.example</string></array>
  <key>Updates</key>
  <dict><key>Mode</key><string>Off</string></dict>
</dict>
```

The router reads the managed preferences with macOS's own `plutil` and ignores the `Payload…` keys.

## The key ring

The policy does not cover how the data-protection keys that decrypt the user's secrets file are protected. On macOS, on Linux and in containers only file permissions protect them unless `SemanticRouter:DataProtection:Certificate` names a certificate to encrypt them to. Set it as a host setting: in `appsettings.json` in the user's configuration directory (read by the tool, its background service and the desktop app alike), or as the `SemanticRouter__DataProtection__Certificate` environment variable for a container. On Windows they are encrypted with DPAPI already. See [host.md](host.md#the-key-ring); `semantic-router doctor` shows how they are protected.

## Not covered yet

- Settings are shown as locked only through the warnings; the editors do not grey them out.
- The ONNX embedding model download (from Hugging Face) is not limited by `AllowedEndpoints`; see [network.md](network.md) for how to place the model yourself.
- There are no ADMX templates for Group Policy yet.
