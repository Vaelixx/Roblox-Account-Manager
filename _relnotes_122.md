## Roblox Account Manager v1.2.2

Kleines Wartungs-Update mit einem Friends-Tab-Fix und ruhigerem Update-Verhalten.

### 🐛 Fixes
- **Friends-Tab:** Beim Auswählen eines Accounts im Picker stand dort `RobloxAccountManager.Models.Account` statt des Namens. Der Picker zeigt jetzt zuverlässig den **Account-Namen** (Alias/Username) — auch direkt beim Öffnen, bevor die Dropdown-Liste je aufgeklappt wurde.

### ⚙️ Verbesserungen
- **Update-Check jetzt alle 5 Minuten** statt jede Minute — deutlich weniger GitHub-Requests im Hintergrund, gleiche schnelle Erkennung neuer Versionen. Die Update-Pille in der Titelleiste bleibt wie gewohnt sichtbar, ohne zu nerven.
- **Robustere Anzeige:** `Account`, `Friend` und `GameServer` liefern jetzt einen lesbaren Namen, falls ein Control das Objekt je roh darstellt (keine kryptischen Typnamen mehr in der UI).

### 📦 Installation
Lade die `RobloxAccountManager.exe` unten herunter und doppelklick sie — Single-File, kein Installer nötig. Bestehende Installationen ziehen dieses Update dank des Auto-Updaters **innerhalb von 5 Minuten** automatisch.
