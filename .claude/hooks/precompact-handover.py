#!/usr/bin/env python3
"""PreCompact-Hook: schreibt vor dem (Auto-)Compact einen Notbehelf-Handover-Entwurf.

HANDGESCHRIEBEN (nicht auto-generiert). Wird NICHT von Sync-UmlautTriggers.ps1 verwaltet.
D365TestCenter-Variante der Familien-Vorlage (Palas/DeutscheBahn-Linie), Ziel
.claude/handover-drafts/ (gitignored): dieses Produkt-Repo hat kein eigenes Session-System,
die Übergabe-Konvention liegt im D365TestCenter-Workspace-Repo. Eingeführt per
claudecode-ADR-2026-07-04-0922 (Familien-Entscheid), Ergänzung zum Kontextlast-Hook: der
warnt VOR den Schwellen, dieser Hook greift als letzte Auffanglinie, wenn Auto-Compact
doch zuschlägt und die scharfen Details (exakte Namen, Zeilennummern, Belege) zu ungenauen
Zusammenfassungen verdichtet. Der Entwurf hält den greifbaren Git-Stand fest (HEAD, Branch,
git status) plus einen Transcript-Auszug, damit die Folge-Session einen Anker hat.

Der Entwurf ERSETZT keine ordentliche Übergabe (Session-State und Konventionen im
D365TestCenter-Workspace). Er ist ein lokales Wegwerf-Rohgerüst, das die nächste Session
lesen, prüfen und in eine saubere Übergabe überführen soll; danach löschen.

fail-open: jeder Fehler -> Exit 0, der Flow wird nie blockiert.
"""
import datetime
import json
import os
import subprocess
import sys


def run_git(project_dir, args):
    """Ruft git im Projektverzeichnis auf und gibt stdout zurück (leer bei Fehler)."""
    try:
        res = subprocess.run(
            ['git'] + args,
            cwd=project_dir,
            capture_output=True,
            text=True,
            encoding='utf-8',
            errors='replace',
            timeout=10,
        )
        return (res.stdout or '').strip()
    except Exception:
        return ''


def transcript_tail(path, max_lines=60):
    """Liest die letzten Zeilen des Transcripts (JSONL) als Roh-Auszug."""
    try:
        if not path or not os.path.isfile(path):
            return ''
        with open(path, encoding='utf-8', errors='replace') as fh:
            lines = fh.readlines()
        return ''.join(lines[-max_lines:])
    except Exception:
        return ''


def main():
    raw = ''
    try:
        raw = sys.stdin.read()
    except Exception:
        pass
    try:
        data = json.loads(raw) if raw else {}
    except Exception:
        data = {}

    trigger = data.get('trigger', 'unbekannt')
    transcript_path = data.get('transcript_path', '')

    project_dir = os.environ.get('CLAUDE_PROJECT_DIR') or os.getcwd()
    target_dir = os.path.join(project_dir, '.claude', 'handover-drafts')
    try:
        os.makedirs(target_dir, exist_ok=True)
    except Exception:
        target_dir = project_dir

    stamp = datetime.datetime.now().strftime('%Y-%m-%d-%H%M')
    target = os.path.join(target_dir, stamp + '-handover-autocompact-entwurf.md')

    head = run_git(project_dir, ['log', '-1', '--oneline']) or '(unbekannt)'
    branch = run_git(project_dir, ['rev-parse', '--abbrev-ref', 'HEAD']) or '(unbekannt)'
    status = run_git(project_dir, ['status', '--short']) or '(sauberer Working Tree)'
    tail = transcript_tail(transcript_path)

    body = (
        "# Handover-Entwurf (automatisch vor Compact)\n\n"
        "> **Notbehelf, kein fertiger Handover.** Vom PreCompact-Hook erzeugt, weil die Session "
        "verdichtet wurde (Trigger: %s). Die Folge-Session liest diesen Entwurf, verifiziert ihn "
        "gegen den Live-Stand und schreibt daraus eine ordentliche Übergabe nach der Konvention "
        "des D365TestCenter-Workspace-Repos (dort liegen Session-State und Arbeitsregeln). "
        "Danach diesen Entwurf löschen; der Ordner .claude/handover-drafts/ ist gitignored.\n\n"
        "## Git-Stand zum Compact-Zeitpunkt\n\n"
        "- **Branch:** %s\n"
        "- **HEAD:** %s\n\n"
        "### Uncommittete Änderungen (`git status --short`)\n\n"
        "```\n%s\n```\n\n"
        "## Transcript-Auszug (letzte Zeilen, roh)\n\n"
        "> Nur als Gedächtnisstütze. Die scharfen Details stehen in den Dateien, nicht hier.\n\n"
        "```\n%s\n```\n"
        % (trigger, branch, head, status, tail if tail else '(kein Transcript-Auszug verfügbar)')
    )

    try:
        with open(target, 'w', encoding='utf-8') as fh:
            fh.write(body)
    except Exception:
        return 0
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)
