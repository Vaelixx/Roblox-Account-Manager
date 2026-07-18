<div align="center">

<img src="icon.png" width="96" alt="Roblox Account Manager icon">

# Roblox Account Manager

**Fast, private, beautifully minimal manager for your Roblox accounts.**

Add accounts once — launch any of them into any game with one click, run several at the same time,
all from a clean, modern desktop app.

<p>
  <a href="https://github.com/Vaelixx/Roblox-Account-Manager/releases/latest"><img src="https://img.shields.io/github/v/release/Vaelixx/Roblox-Account-Manager?style=for-the-badge&label=release&color=6d5cff" alt="Latest release"></a>
  <a href="https://github.com/Vaelixx/Roblox-Account-Manager/releases"><img src="https://img.shields.io/github/downloads/Vaelixx/Roblox-Account-Manager/total?style=for-the-badge&label=downloads&color=22c55e" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8">
  <img src="https://img.shields.io/badge/platform-Windows-0078d4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows">
</p>

<p>
  <a href="#-features">Features</a> ·
  <a href="#-install">Install</a> ·
  <a href="#-quick-start">Quick start</a> ·
  <a href="#-security">Security</a> ·
  <a href="#-building-from-source">Build</a> ·
  <a href="#-roadmap">Roadmap</a> ·
  <a href="#-faq">FAQ</a>
</p>

<img src="docs/accounts.png" alt="Accounts view" width="820">

</div>

---

## ✨ Features

### Launching
| | |
|---|---|
| 🚀 **One-click launch** | Paste a Place ID, hit **Launch**. Optional Job ID joins a specific server. |
| 🧩 **Multi-instance** | Run several Roblox clients side by side — no extra tools needed. |
| 🌐 **Server browser** | Browse public servers with live player counts, join directly or copy a Job ID. |
| 🕹️ **Open anywhere** | Open an account signed-in in a private Chromium window, in the Roblox app, or view its public profile. |

### Organisation
| | |
|---|---|
| 🗂️ **Groups, aliases & notes** | Structure any number of accounts, see everything at a glance. |
| 📊 **Live data** | Avatars, presence (online / in-game / studio) and Robux for every account, refreshed automatically. |
| 🔎 **Instant overview** | Presence updates within seconds of launching or closing a game. |

### Experience
| | |
|---|---|
| 🎨 **Modern UI** | Dark, minimal WPF interface with smooth page transitions, animated buttons and subtle glow effects. |
| 📥 **System tray** | Close to tray, keep running silently — tray icon and global hotkeys run on native Win32, no WinForms. |
| 📦 **Zero setup** | One portable, self-contained `.exe` (~56 MB). No .NET install required on the target PC. |
| 🔄 **Auto-update check** | Notifies you when a new release is available — updating is always your choice. |

### Privacy & security
| | |
|---|---|
| 🔐 **Encrypted at rest** | Account store encrypted with Windows **DPAPI**, or a **master password** (AES-256-GCM + PBKDF2). |
| 🕵️ **Never plain text** | Each cookie additionally encrypted individually — even the decrypted store never contains a readable cookie. |
| 🏠 **100 % local** | Cookies never leave your machine. Only official Roblox endpoints are contacted. No telemetry, no analytics. |

---

## 🖼️ Screenshots

| Accounts | Settings |
|----------|----------|
| ![Accounts](docs/accounts.png) | ![Settings](docs/settings.png) |

---

## 📦 Install

1. Download the latest `Roblox Account Manager.exe` from the [**Releases**](../../releases) page.
2. Put it in its own folder — it creates a `data\` folder next to itself.
3. Double-click to run. Nothing else to install.

> [!NOTE]
> Windows SmartScreen may warn about an unrecognised app the first time — click **More info → Run anyway**.
> The exe is unsigned; you can always [build it from source](#-building-from-source) yourself.

---

## 🚀 Quick start

1. **Add account** → paste the account's `.ROBLOSECURITY` cookie. It is validated against Roblox before being stored.
2. Select the account — details and launch options appear on the right.
3. Enter a **Place ID** (game preview loads automatically) and press **Launch**.
4. Organise: set an **Alias**, a **Description**, pick or create a **Group** on the fly.

<div align="center"><img src="docs/add-account.png" width="440" alt="Add account dialog"></div>

### Getting a cookie

The `.ROBLOSECURITY` cookie identifies a logged-in account. Log into the account in a browser,
open the cookies for `roblox.com` and copy the `.ROBLOSECURITY` value
(starts with `_|WARNING:-DO-NOT-SHARE-THIS...`).

> [!CAUTION]
> Never share this cookie with anyone or paste it into websites. Whoever has it controls the account.

---

## 🔐 Security

Account cookies are the keys to your accounts — this app treats them accordingly:

- **Encrypted at rest.** The account file is encrypted with **Windows DPAPI** (tied to your Windows user) by default, or with a **master password** you set (AES-256-GCM + PBKDF2) from Settings.
- **Never plain text.** On top of the file encryption, each cookie is individually DPAPI-encrypted — even the decrypted store never contains a readable cookie.
- **Local only.** No server, no sync, no telemetry. The app talks exclusively to official Roblox APIs over HTTPS.
- **Validated on startup.** Cookies are re-checked against Roblox so dead sessions are flagged immediately.
- **Open source.** Every line that touches your cookies is in this repository — audit it, build it yourself.

---

## 🛠️ Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Vaelixx/Roblox-Account-Manager.git
cd Roblox-Account-Manager

# quick dev build + run
dotnet run --project src\RobloxAccountManager.csproj

# single-file, self-contained release exe  ->  .\dist\
dotnet publish src\RobloxAccountManager.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:SatelliteResourceLanguages=en `
    -o dist
```

Produces a single portable `dist\Roblox Account Manager.exe` — copy anywhere, double-click, done.

### Tech overview

| | |
|---|---|
| UI | WPF (.NET 8), MVVM, custom dark theme, hand-rolled animations |
| Tray & hotkeys | Native Win32 (`Shell_NotifyIcon`, `RegisterHotKey`) — no WinForms dependency |
| Storage | DPAPI / AES-256-GCM encrypted local store |
| Networking | `HttpClient` against official Roblox web APIs |
| Packaging | Single-file, self-contained, compressed publish |

---

## 🗺️ Roadmap

Planned / under consideration — open an [issue](../../issues) to vote or suggest:

- [ ] **Bulk launch** — select multiple accounts, launch all into the same server with stagger delay
- [ ] **Auto-rejoin watchdog** — relaunch an instance into the same server if it crashes or disconnects
- [ ] **Window arranger** — auto-tile multiple Roblox windows in a grid
- [ ] **Join friend / follow player** — join the server a given username is playing on
- [ ] **Private-server & share-link support** — paste any roblox.com share link, launch directly
- [ ] **Account health panel** — cookie age, last validation, expiry warnings
- [ ] **Encrypted backup & restore** — one-file export/import of the whole account store
- [ ] **Quick-launch hotkeys** — bind favourite account + place to a global hotkey
- [ ] **Accent & theme picker** — custom accent colour, optional light mode
- [ ] **Localisation** — English + German to start
- [ ] **CLI companion** — `ram launch --account <alias> --place <id>` for scripting

---

## ❓ FAQ

<details>
<summary><b>Is this safe? Where do my cookies go?</b></summary>

Nowhere. Everything is stored encrypted on your own disk and only sent to official `roblox.com` endpoints for validation and launching — same as your browser does. The full source is in this repo.
</details>

<details>
<summary><b>Why does SmartScreen warn me?</b></summary>

The exe is not code-signed (certificates cost money). **More info → Run anyway**, or build from source.
</details>

<details>
<summary><b>Why is the exe ~56 MB?</b></summary>

It bundles the entire .NET 8 runtime so you don't have to install anything. One file, works on any Windows 10/11 x64 machine.
</details>

<details>
<summary><b>Can I run more than one Roblox instance?</b></summary>

Yes — multi-instance is built in. Launch as many accounts as your PC can handle.
</details>

<details>
<summary><b>Does this violate Roblox ToS?</b></summary>

The app only automates what you could do manually in a browser (log in, launch games) and talks only to official APIs. Managing alt accounts is widely tolerated, but automation is a grey area — use it responsibly, at your own risk.
</details>

---

## 🤝 Contributing

Issues and pull requests welcome. For bigger changes, open an issue first to discuss the direction.

1. Fork → branch → change → PR
2. Keep the style: MVVM, no code-behind logic where avoidable, nullable enabled
3. `dotnet build -c Release` must pass with **0 warnings**

---

<div align="center">

**Not affiliated with Roblox Corporation.**
Use responsibly and in accordance with Roblox's Terms of Service. You are responsible for your own accounts.

Made with WPF + .NET 8 · Windows 10/11 x64

</div>
