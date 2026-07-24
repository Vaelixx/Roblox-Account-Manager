## What's new in v1.4.0

### Multi-instance now works for launches started outside the app

This is the headline fix. Roblox enforces single-instance with **two** guards, and the app only ever defeated one of them:

| Guard | Where it lives | Before |
|---|---|---|
| `ROBLOX_singletonMutex` | global named mutex | held open ✅ |
| `ROBLOX_singletonEvent` | **inside each client process** | untouched ❌ |

Because of the second guard, pressing **Play** on roblox.com, launching from the Roblox home screen, or opening a Discord invite while a client was already running did *not* open a new window — the new client handed its launch URL to the running one and exited. Launches from the manager itself were unaffected, since those carry a fresh auth ticket the running client accepts, which is why the problem looked inconsistent.

The new guard clears that per-client lock in every running client and keeps doing so via a lightweight watcher, so a client the app never started is treated exactly like one it did.

### New
- **Multi-instance guard panel** (Settings → Launch) — live status, a *Fix multi-instance now* button, and a *Restart as administrator* option that appears only when elevation would actually help
- **External clients are managed too** — clients started from the website or home screen are adopted into the registry, so Anti-AFK, the RAM monitor and the window grid can reach them
- **Self-check** (Settings → Diagnostics) — verifies the Roblox install, the `roblox-player` protocol handler (and warns when another launcher has hijacked it), data-folder writability, free disk space, Roblox API reachability, and the multi-instance guard
- **Diagnostics log** — one rotating, cookie-scrubbed log with an in-app error counter, *Open log folder* and *Clear log*
- **Client window controls** — arrange every client in a grid, minimize all, restore all

### Fixes
- **Data loss:** an account file that failed to decrypt (typically copied from another Windows user or PC) silently loaded as *empty*, and the next save overwrote the real file **and its backup**. The app now refuses to start and tells you where the file is.
- **Wrong client attribution:** two accounts launched close together were deterministically swapped — the wrong cookie on auto-rejoin, and "close previous client" killing the other account's window
- **Untracked clients:** attribution was a single attempt 4 s after launch; a slow client start meant it was never tracked at all — no Anti-AFK, no crash watchdog, no RAM cap, and nothing said so. Now retried for 24 s.
- **PID reuse:** tracked PIDs were never re-validated, so a recycled PID could have the RAM monitor kill, Anti-AFK type into, or the scheduler close an unrelated application
- **Rate limiting:** an HTTP 429 (exactly what launching several accounts in a row provokes) was treated as "this cookie is dead" and marked good accounts invalid. Only 401/403 do that now.
- **Anti-AFK** no longer sends its keystroke when the client window fails to take focus — it used to type Space/W into whatever you were doing
- **Scheduler auto-close** now only closes the clients that task started, instead of every client of those accounts
- **Mutex thread affinity:** the multi-instance mutex was acquired on whichever thread ran a launch and released from another, which always threw and was silently swallowed. It now lives on its own thread.
- Crash reports **append** instead of truncating — a repeating fault no longer destroys the evidence of the first one

### Under the hood
- New services: `RobloxSingletonService`, `DiagnosticsService`, `HealthCheckService`, `InstanceControlService`
- Handle-level singleton clearing via the process handle table (`NtQueryInformationProcess` → `DuplicateHandle` with `DUPLICATE_CLOSE_SOURCE`), with a system-wide fallback for older Windows
- Failures the user can act on now surface as a toast or a status line instead of an empty `catch`
- Clean build: 0 warnings, 0 errors

### Note
Roblox's anti-tamper can refuse the process handle on some systems. If that happens the app says so explicitly and offers to restart with administrator rights — it no longer just quietly does nothing.

**Full Changelog**: https://github.com/Vaelixx/Roblox-Account-Manager/compare/v1.3.3...v1.4.0
