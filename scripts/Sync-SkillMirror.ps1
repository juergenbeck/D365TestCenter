#requires -Version 7.0
<#
.SYNOPSIS
    Spiegelt den Skill `d365-test-center` aus diesem Repo in die nutzenden Repos (ADR 2026-09-17-2303).

.DESCRIPTION
    Die Quelle liegt in `skills/d365-test-center/` und wird mit dem Produktcode versioniert. Jedes Ziel-Repo
    bekommt eine byte-gleiche Kopie plus eine `SPIEGEL.md` mit Quell-Commit und Baum-Hash (Pin), damit
    sichtbar wird, ob eine Kopie lokal verändert wurde oder hinter der Quelle zurückliegt.

    Unangetastet bleiben im Ziel: `PROJEKT-KONTEXT.md` und jede Datei, die die Quelle nicht kennt. Solche
    Zusatzdateien werden nur gemeldet, damit sie nicht unbemerkt neben gespiegeltem Inhalt leben.

    Ohne `-Apply` wird nur gemessen: Exit 0 heißt deckungsgleich, Exit 2 heißt Drift (mit Liste), Exit 1
    heißt, es konnte nicht gemessen werden (fehlende Quelle, unlesbares Ziel).

    Zeilenenden werden beim Schreiben auf LF normalisiert, damit ein Ziel-Repo mit `autocrlf` keinen
    Vollzeilen-Diff bekommt und der Hash über alle Rechner gleich bleibt.

.PARAMETER Apply
    Schreibt. Ohne diesen Schalter wird nichts verändert.

.PARAMETER Ziel
    Nur dieses Ziel aus der Zielliste behandeln (Name aus der Zielliste).

.PARAMETER Zielliste
    Pfad zur Zielliste (JSON mit `source` und `targets`). Die Liste nennt lokale Pfade der nutzenden
    Repos und liegt deshalb nicht in diesem Repo. Reihenfolge: dieser Parameter, dann die
    Umgebungsvariable `D365TC_SPIEGEL_ZIELE`, dann `tools/skill-spiegel/spiegel-ziele.json` im
    Nachbarordner `D365TestCenter-Workspace`.

.EXAMPLE
    pwsh ./scripts/Sync-SkillMirror.ps1            # nur prüfen
    pwsh ./scripts/Sync-SkillMirror.ps1 -Apply     # spiegeln
#>
[CmdletBinding()]
param(
    [switch] $Apply,
    [string] $Ziel,
    [string] $Zielliste
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Off
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$repo = Split-Path -Parent $PSScriptRoot
if (-not $Zielliste) { $Zielliste = $env:D365TC_SPIEGEL_ZIELE }
if (-not $Zielliste) {
    $Zielliste = Join-Path (Split-Path -Parent $repo) 'D365TestCenter-Workspace/tools/skill-spiegel/spiegel-ziele.json'
}
if (-not (Test-Path $Zielliste)) { Write-Error "Zielliste nicht gefunden: $Zielliste"; exit 1 }

$cfg = Get-Content -Raw -LiteralPath $Zielliste | ConvertFrom-Json
$quellOrdner = Join-Path $repo $cfg.source
if (-not (Test-Path $quellOrdner)) { Write-Error "Quelle nicht gefunden: $quellOrdner"; exit 1 }

# --- Quelle einlesen (LF-normalisiert, sortiert) -----------------------------------------------------
$quellDateien = [ordered] @{}
foreach ($f in Get-ChildItem -LiteralPath $quellOrdner -Recurse -File | Sort-Object FullName) {
    $rel = [IO.Path]::GetRelativePath($quellOrdner, $f.FullName).Replace('\', '/')
    if ($rel -eq 'PROJEKT-KONTEXT.md') { continue }   # gehört dem Ziel-Repo, nie aus der Quelle
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $text  = [Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n"
    $quellDateien[$rel] = [Text.Encoding]::UTF8.GetBytes($text)
}
if ($quellDateien.Count -eq 0) { Write-Error "Quelle enthält keine Dateien: $quellOrdner"; exit 1 }

function Get-BaumHash([System.Collections.IDictionary] $dateien) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $ms = [IO.MemoryStream]::new()
    foreach ($k in ($dateien.Keys | Sort-Object)) {
        $kopf = [Text.Encoding]::UTF8.GetBytes("$k`n" + $dateien[$k].Length + "`n")
        $ms.Write($kopf, 0, $kopf.Length)
        $ms.Write($dateien[$k], 0, $dateien[$k].Length)
    }
    $ms.Position = 0
    ($sha.ComputeHash($ms) | ForEach-Object { $_.ToString('x2') }) -join ''
}

$baumHash = Get-BaumHash $quellDateien
$quellCommit = (& git -C $repo rev-parse HEAD 2>$null)
if (-not $quellCommit) { $quellCommit = 'unbekannt' }
$sauber = -not (& git -C $repo status --porcelain -- $cfg.source)
$skillName = Split-Path $cfg.source -Leaf

"Quelle:      $($cfg.source) ($($quellDateien.Count) Dateien)"
"Baum-Hash:   $baumHash"
"Quell-Commit: $quellCommit$(if (-not $sauber) { '  (Arbeitsbaum der Quelle ist NICHT sauber)' })"
if ($Apply -and -not $sauber) {
    Write-Warning "Die Quelle hat uncommittete Änderungen. Der Pin zeigt dann auf einen Commit, der den gespiegelten Stand nicht enthält."
}
''

$drift = 0
$geschrieben = 0
$ziele = $cfg.targets | Where-Object { -not $Ziel -or $_.name -eq $Ziel }
if (-not $ziele) { Write-Error "Kein Ziel mit Namen '$Ziel' in der Zielliste."; exit 1 }

foreach ($t in $ziele) {
    if (-not (Test-Path $t.path)) {
        # Ein fehlender Klon ist keine Unauffälligkeit: sonst meldet der Riegel auf einem Rechner ohne
        # dieses Repo "alle Spiegel deckungsgleich", obwohl dort nichts gespiegelt ist.
        $drift++
        "[FEHLT] $($t.name): Pfad nicht vorhanden ($($t.path)), Spiegel dort nicht prüfbar"
        continue
    }
    foreach ($root in $t.skillRoots) {
        $zielOrdner = Join-Path $t.path (Join-Path $root $skillName)
        $label = "$($t.name) $root"
        $vorhanden = Test-Path $zielOrdner
        $abweichend = @()

        foreach ($rel in $quellDateien.Keys) {
            $zielDatei = Join-Path $zielOrdner $rel
            if (-not (Test-Path $zielDatei)) { $abweichend += "fehlt: $rel"; continue }
            $ist = [IO.File]::ReadAllBytes($zielDatei)
            $soll = $quellDateien[$rel]
            if ($ist.Length -ne $soll.Length -or [Convert]::ToBase64String($ist) -ne [Convert]::ToBase64String($soll)) {
                $abweichend += "abweichend: $rel"
            }
        }

        $fremd = @()
        if ($vorhanden) {
            foreach ($f in Get-ChildItem -LiteralPath $zielOrdner -Recurse -File) {
                $rel = [IO.Path]::GetRelativePath($zielOrdner, $f.FullName).Replace('\', '/')
                if ($rel -eq 'PROJEKT-KONTEXT.md' -or $rel -eq 'SPIEGEL.md') { continue }
                if (-not $quellDateien.Contains($rel)) { $fremd += $rel }
            }
        }

        # Der Pin ist der Zettel, an dem ein Rückstand sichtbar wird; ein verfälschter oder
        # stehengebliebener Pin darf deshalb nicht unauffällig bleiben.
        $pinDatei = Join-Path $zielOrdner 'SPIEGEL.md'
        if (-not $Apply) {
            if (-not (Test-Path $pinDatei)) { $abweichend += 'fehlt: SPIEGEL.md' }
            elseif (-not ((Get-Content -Raw -LiteralPath $pinDatei) -match [regex]::Escape($baumHash))) {
                $abweichend += 'abweichend: SPIEGEL.md (Baum-Hash passt nicht zur Quelle)'
            }
        }

        if ($Apply) {
            foreach ($rel in $quellDateien.Keys) {
                $zielDatei = Join-Path $zielOrdner $rel
                $ordner = Split-Path -Parent $zielDatei
                if (-not (Test-Path $ordner)) { New-Item -ItemType Directory -Path $ordner -Force | Out-Null }
                [IO.File]::WriteAllBytes($zielDatei, $quellDateien[$rel])
            }
            $pin = @(
                "# Spiegel des Skills ``$skillName`` (generiert, nicht hier editieren)",
                '',
                'Dieser Ordner ist eine **byte-gleiche Kopie** aus dem Repo D365TestCenter,',
                "Quelle ``$($cfg.source)`` (ADR 2026-09-17-2303).",
                '',
                "- **Quell-Commit:** ``$quellCommit``",
                "- **Baum-Hash:** ``$baumHash``",
                "- **Gespiegelt am:** $(Get-Date -Format 'dd.MM.yyyy HH:mm')",
                '',
                'Inhaltliche Änderungen gehören in die Quelle, danach neu spiegeln:',
                '``pwsh ./scripts/Sync-SkillMirror.ps1 -Apply`` im Repo D365TestCenter.',
                'Drift prüfen (schreibt nichts): dasselbe Skript ohne ``-Apply`` (Exit 2 = Drift).',
                '',
                'Projektspezifisches gehört in die ``PROJEKT-KONTEXT.md`` neben diesem Skill;',
                'sie wird vom Spiegeln nie angefasst.'
            ) -join "`n"
            [IO.File]::WriteAllBytes((Join-Path $zielOrdner 'SPIEGEL.md'), [Text.Encoding]::UTF8.GetBytes($pin + "`n"))
            $geschrieben++
            $zustand = if ($abweichend.Count -gt 0 -or -not $vorhanden) { 'aktualisiert' } else { 'unverändert' }
            "[OK] $label ($zustand, $($quellDateien.Count) Dateien)"
        }
        elseif ($abweichend.Count -gt 0) {
            $drift++
            "[DRIFT] $label"
            $abweichend | ForEach-Object { "         $_" }
        }
        else {
            "[OK] $label (deckungsgleich)"
        }

        if ($fremd.Count -gt 0) {
            "         Zusatzdateien im Ziel (bleiben unangetastet): $($fremd -join ', ')"
        }
    }
}

''
if ($Apply) {
    "Fertig: $geschrieben Spiegel geschrieben."
    # Ein fehlendes Ziel bleibt auch beim Schreiben ein Rückstand: sonst meldet ein aufrufendes
    # Skript Erfolg, obwohl ein Repo gar nicht bedient wurde.
    if ($drift -gt 0) { "Hinweis: $drift Ziel(e) waren nicht erreichbar."; exit 2 }
    exit 0
}
if ($drift -gt 0) { "Fertig: $drift Spiegel weichen ab."; exit 2 }
"Fertig: alle Spiegel deckungsgleich."
exit 0
