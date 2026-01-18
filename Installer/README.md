# Vortex Engine Installer

Dieses Verzeichnis enthält das Inno Setup Script für den Vortex Engine Installations-Wizard.

## Voraussetzungen für lokales Bauen

1. **Inno Setup 6.x** installieren von: https://jrsoftware.org/isdl.php
2. Die Lösung im **Release**-Modus bauen
3. Das Script `VortexEngine.iss` mit Inno Setup Compiler (ISCC) ausführen

## Lokales Bauen des Installers

### Über die Inno Setup IDE:
1. Öffne `VortexEngine.iss` in Inno Setup
2. Drücke F9 oder wähle "Build" ? "Compile"

### Über die Kommandozeile:
```powershell
# Zuerst die Lösung bauen
msbuild ..\Editor.sln /p:Configuration=Release /p:Platform="Any CPU"

# Dann den Installer erstellen
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" VortexEngine.iss
```

## Automatisches Bauen via GitHub Actions

Der Installer wird automatisch gebaut und als GitHub Release veröffentlicht wenn:

1. **Ein Tag gepusht wird** (z.B. `v1.0.0`):
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

2. **Manuell via GitHub Actions** gestartet wird:
   - Gehe zu "Actions" ? "Build and Release"
   - Klicke "Run workflow"
   - Gib die Versionsnummer ein

## Output

Der kompilierte Installer wird im Ordner `Output/` erstellt:
- `VortexEngine-Setup-X.X.X.exe`

## Installer Features

- Moderner Wizard-Style
- Deutsch und Englisch als Sprachen
- Desktop-Verknüpfung (optional)
- .NET Framework 4.8 Prüfung
- Dateiassoziation für `.vortex` Projektdateien
- Saubere Deinstallation über Windows

## Deinstallation

Die Deinstallation kann auf mehrere Arten gestartet werden:

1. **Windows Einstellungen**: Apps ? Vortex Engine ? Deinstallieren
2. **Systemsteuerung**: Programme und Features ? Vortex Engine ? Deinstallieren
3. **Startmenü**: Vortex Engine ? "Vortex Engine deinstallieren"
4. **Direkt**: `{Installationsordner}\unins000.exe` ausführen

### Was wird bei der Deinstallation entfernt:

- ? Alle Programmdateien im Installationsordner
- ? Desktop- und Startmenü-Verknüpfungen
- ? Dateiassoziationen (.vortex)
- ? Registry-Einträge
- ? Log- und Cache-Dateien
- ?? Benutzereinstellungen (optional - Checkbox während Deinstallation)

### Benutzereinstellungen

Benutzereinstellungen werden in `%LOCALAPPDATA%\VortexEngine` gespeichert.
Bei der Deinstallation wird gefragt, ob diese gelöscht werden sollen.

## Version aktualisieren

Bearbeite die `#define MyAppVersion` Zeile in `VortexEngine.iss`:
```iss
#define MyAppVersion "1.0.0"
```

Bei GitHub Actions wird die Version automatisch aus dem Tag oder der manuellen Eingabe übernommen.
