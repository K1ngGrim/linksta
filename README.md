# linksta

Konsolen-Tool, das deine ILIAS-Kurse per **WebDAV** auf den Rechner synchronisiert, damit du alle Dateien offline hast.
Läuft auf **macOS, Linux und Windows** (.NET 10, [Spectre.Console](https://spectreconsole.net)).

```
linksta pull
── Analysis I ─────────────────────────────────────────
  ＋ Übungen/Blatt 05.pdf 212 KB
  ↻ Skript.pdf 4.1 MB
✓ 1 neu, 1 aktualisiert (84 Dateien, 4.3 MB geladen)
```

📖 **Anleitung:** https://k1nggrim.github.io/linksta/

## Installation

Fertige Programme für macOS, Linux und Windows gibt es auf der
[Release-Seite](https://github.com/K1ngGrim/linksta/releases/latest). Die Schritte pro System
stehen in der [Anleitung](https://k1nggrim.github.io/linksta/#installieren).

**Selbst bauen (kein .NET auf dem Zielrechner nötig):**

```bash
./publish.sh
```

Danach liegt in `dist/<plattform>/` die Datei `linksta` bzw. `linksta.exe`. Auf macOS/Linux installiert `./install.sh` sie nach `~/.local/bin`. Unter Windows kopierst du `linksta.exe` in einen Ordner im `PATH` (z. B. `%USERPROFILE%\bin`).

**Variante B – als dotnet-Tool:**

```bash
dotnet pack src/Linksta -c Release
dotnet tool install -g linksta --add-source ./nupkg
```

## Schnellstart

```bash
linksta init                    # ILIAS-URL, Client-ID, Benutzer, Passwort, Zielordner
linksta add https://ilias.uni-xy.de/goto.php?target=crs_123456
linksta courses                 # oder: deine Kurse aus „Meine Kurse und Gruppen“ ankreuzen
linksta browse                  # oder: Magazin interaktiv durchsuchen und Kurse abonnieren
linksta pull                    # alles herunterladen / aktualisieren
```

### Was muss ich bei `init` angeben?

| Frage      | Woher?                                                                                                        |
|------------|---------------------------------------------------------------------------------------------------------------|
| URL        | Einfach irgendeinen ILIAS-Link einfügen, z. B. einen Kurs-Link. Basis-URL und Client werden daraus erraten.    |
| Client-ID  | Steht in Links wie `goto_`**`CLIENT`**`_crs_123.html` oder `?client_id=`**`CLIENT`**.                           |
| Benutzer   | Dein ILIAS-Login.                                                                                             |
| Passwort   | Wird im System-Schlüsselbund gespeichert (siehe unten).                                                       |

> **Login über Shibboleth/SSO?** WebDAV funktioniert nur mit Benutzername und Passwort (HTTP Basic Auth).
> Bei vielen Unis musst du dafür in ILIAS unter *Profil → Einstellungen* ein **lokales Passwort** bzw.
> **WebDAV-Passwort** setzen. Ob WebDAV aktiv ist, siehst du z. B. am Aktionsmenü eines Kurses
> („Als WebDAV-Ordner öffnen“). Diesen Link kannst du auch direkt bei `linksta add` einfügen.

### Kurse hinzufügen

`linksta add` versteht alle üblichen Formate, auch mehrere auf einmal:

```bash
linksta add 123456                                            # Ref-ID
linksta add "https://ilias.uni.de/goto.php?target=crs_123456" # Permalink
linksta add https://ilias.uni.de/goto_client_grp_777.html     # Gruppen-Permalink
linksta add "https://ilias.uni.de/ilias.php?ref_id=555&..."   # URL aus der Adresszeile
linksta add https://ilias.uni.de/webdav.php/client/ref_42/    # WebDAV-Link
linksta add 123456 --name "Ana I"                             # eigener Kurzname
```

Am bequemsten ist `linksta courses`: linksta meldet sich dafür wie ein Browser an und liest
„Meine Kurse und Gruppen“ aus, denn WebDAV selbst kennt keine Mitgliedschaften.

### Kurse mit „/“ im Titel (🌐 Web-Modus)

ILIAS blendet über WebDAV alles aus, dessen Titel ein `/` enthält (z. B. „Winter 2026 / 2027“).
Der Server meldet dann *„This object title is invalid or hidden!“*, auch wenn ILIAS im Pfeilmenü
einen WebDAV-Link anzeigt. linksta erkennt das bei `add` und `courses` automatisch und
synchronisiert solche Kurse stattdessen über die **ILIAS-Weboberfläche** (in `list` mit 🌐 markiert):

- Es meldet sich mit denselben Zugangsdaten am normalen Login-Formular an.
- Es liest Dateien, Ordner und Gruppen von den Kursseiten. Tests, Foren und Weblinks werden übersprungen.
- Änderungen erkennt es an Typ, Größe, Version und Datum aus der Kursliste.
- Konflikte, Papierkorb, Verlauf und Ausschlüsse funktionieren genauso wie bei WebDAV.

Der Web-Modus hängt vom HTML-Aufbau der Seiten ab. Nach einem größeren ILIAS-Update kann er
deshalb Anpassungen brauchen.

## Befehle

| Befehl                                  | Beschreibung |
|-----------------------------------------|---|
| `linksta init`                          | Einrichten (geht auch ohne Fragen: `--url`, `--client`, `--user`, `--dir`) |
| `linksta login` / `logout`              | Passwort neu speichern / löschen |
| `linksta add <Link…>`                   | Kurs(e) abonnieren |
| `linksta courses` (`mine`)              | Deine Kurse aus dem Dashboard anzeigen und per Häkchen abonnieren (`--list`, `--all`) |
| `linksta browse [Ref-ID]`               | Magazin interaktiv durchsuchen (mit Suchfeld), Kurse abonnieren |
| `linksta list` (`ls`)                   | Abonnierte Kurse mit Dateianzahl, Größe und letztem Sync |
| `linksta remove <Kurs> [--delete-files]`| Abo entfernen (Dateien bleiben, außer mit `--delete-files`) |
| `linksta pause` / `resume <Kurs>`       | Kurs vorübergehend nicht synchronisieren |
| `linksta pull [Kurs…]` (`sync`)         | Neue/geänderte Dateien laden |
| `linksta status [Kurs…]` (`st`)         | Zeigen, was sich geändert hat, ohne zu laden |
| `linksta recent [-d 14]` (`new`)        | Was wurde in den letzten Tagen neu geladen? |
| `linksta search <Text \| Muster>`       | Offline in Dateinamen suchen, z. B. `search "*blatt*.pdf" --open` |
| `linksta open [Kurs]`                   | Ordner im Finder/Explorer/Dateimanager öffnen |
| `linksta exclude add <Muster> [-c Kurs]`| Dateien ausschließen, z. B. `*.mp4`, `Aufzeichnungen`, `Übungen/**/Lösung*` |
| `linksta config show` / `config set`    | Einstellungen (`url`, `client`, `user`, `dir`, `parallel`, `max-size`) |
| `linksta ls-remote [Pfad]`              | Diagnose: rohes WebDAV-Listing |

Kursnamen dürfen abgekürzt werden: `linksta pull ana` reicht für „Analysis I“.

### Optionen für `pull`

| Option            | Wirkung |
|-------------------|---|
| `-n, --dry-run`   | Nur anzeigen |
| `--prune`         | Auf ILIAS gelöschte Dateien lokal in den Papierkorb `.linksta/trash/` verschieben |
| `--overwrite`     | Bei Konflikten lokale Änderungen überschreiben |
| `-j, --parallel N`| Gleichzeitige Downloads (Standard 4) |
| `-v, --verbose`   | Alle geladenen Dateien einzeln auflisten |
| `-q, --quiet`     | Nur Fehler und neue Dateien ausgeben (für Cronjobs) |

## Wie der Sync funktioniert

- **Nur Download.** linksta lädt nie etwas zu ILIAS hoch.
- Geänderte Dateien erkennt linksta an ETag, Größe und Änderungsdatum auf ILIAS (im Web-Modus: Größe, Version und Datum).
- **Lokale Änderungen gehen nie verloren.** Hast du eine Datei bearbeitet (z. B. Notizen im PDF):
  - Ist sie auf ILIAS unverändert, bleibt deine Version.
  - Wurde sie auch auf ILIAS geändert, wird die neue Version geladen und deine als `Datei (lokal 2026-11-06 14-30).pdf` behalten.
- Lokal gelöschte Dateien werden beim nächsten `pull` wiederhergestellt. Was du dauerhaft nicht willst, schließt du mit `exclude` aus.
- Auf ILIAS gelöschte Dateien bleiben lokal liegen, bis du `--prune` verwendest. Auch dann landen sie im Papierkorb und werden nicht endgültig gelöscht.
- Dateinamen werden für alle Systeme gültig gemacht (`:` `?` `*` …, reservierte Windows-Namen). Gleichnamige Dateien in einem Ordner bekommen ` (2)` angehängt.
- Abbruch mit Strg+C: Halb geladene Dateien werden verworfen, alles bisher Geladene bleibt gespeichert.
- Verbindungsfehler werden mehrmals wiederholt. Fällt ein Download aus, laufen die anderen weiter.

## Wo liegt was?

| Was                     | Pfad |
|-------------------------|---|
| Konfiguration           | macOS: `~/Library/Application Support/linksta/` · Linux: `~/.config/linksta/` · Windows: `%APPDATA%\linksta\` (mit `LINKSTA_HOME` änderbar) |
| Passwort                | macOS: Schlüsselbund · Windows: DPAPI-verschlüsselt · Linux: Secret Service (`secret-tool`), sonst eine Datei mit Rechten `600` |
| Dateien                 | `~/ILIAS/<Kurs>/…` (bei `init` wählbar) |
| Sync-Status, Verlauf, Papierkorb | `~/ILIAS/.linksta/` |

Alternativ kann das Passwort über die Umgebungsvariable `LINKSTA_PASSWORD` kommen (praktisch für Server/CI).

## Automatisch synchronisieren

**macOS/Linux (cron), stündlich:**

```bash
crontab -e
# 0 * * * * /usr/local/bin/linksta pull -q >> ~/ILIAS/.linksta/cron.log 2>&1
```

**Windows (Aufgabenplanung):**

```powershell
schtasks /Create /SC HOURLY /TN linksta /TR "%USERPROFILE%\bin\linksta.exe pull -q"
```

## Release & Doku veröffentlichen

- **Doku:** `docs/index.html` wird bei jedem Push auf `main` per GitHub Actions auf GitHub Pages veröffentlicht.
  Einmalig im Repo unter *Settings → Pages → Source* „GitHub Actions“ auswählen.
- **Release:** Ein Tag wie `v1.0.0` pushen. Die Actions bauen dann alle Plattformen (macOS auf macOS-Runnern,
  damit die Binaries signiert sind) und hängen sie mit Prüfsummen an ein GitHub-Release:

  ```bash
  git tag v1.0.0 && git push origin v1.0.0
  ```

## Entwicklung

```bash
dotnet build
dotnet test       # Unit-Tests + End-to-End-Tests gegen einen simulierten ILIAS-WebDAV-Server
dotnet run --project src/Linksta -- pull --dry-run
```

Aufbau:

```
src/Linksta/
  Program.cs              Befehle registrieren (Spectre.Console.Cli)
  Commands/               Ein Befehl pro Klasse
  WebDav/WebDavClient.cs  PROPFIND/GET mit Retry
  Ilias/IliasLink.cs      ILIAS-Links → WebDAV-Pfade
  Sync/                   Scanner → Planner → Engine, Statusdatei, Verlauf
  Security/               Passwort im OS-Schlüsselbund
tests/Linksta.Tests/      xUnit, FakeIlias (In-Memory + HTTP-Server)
```
