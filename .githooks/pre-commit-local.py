#!/usr/bin/env python3
"""Customer-neutrality guard for the product repo (called by .githooks/pre-commit).

The product must not carry customer names (ADR-2026-09-18-1258 in the collaboration
workspace). This check blocks a commit whose staged added lines or file paths contain a
blocked term. The terms are stored as SHA-256 hashes of lowercase strings, so this file
does not name the customers it protects against.

Two kinds of terms:
- LONG terms match anywhere, also inside identifiers ("XyzConfig", "isxyz", "xyz_field").
  Every substring of a matching length is hashed and compared. ANCHORED long terms only
  match where a token or camelCase part begins, because they also occur inside neutral
  words (for example across the join of "...l" + "Mapping").
- SHORT terms (few letters) match only as a whole word, where words are split at
  non-alphanumerics and at camelCase boundaries ("XyzLogging" yields "xyz", "logging").

Excluded paths are tooling that is rolled out centrally into many repos (.claude/,
.githooks/, .codex/) and must not be edited here. Binary files are not inspected.

Usage:
  python .githooks/pre-commit-local.py            # staged changes (hook mode)
  python .githooks/pre-commit-local.py --all      # every tracked file (audit mode)
  python .githooks/pre-commit-local.py --self-test
Bypass in a genuine emergency: git commit --no-verify.
"""
import hashlib
import re
import subprocess
import sys

# term length -> set of hashes; substring match
LONG_HASHES = {
    7: {"e0f5a86d11fd8a90f4fb0c5772c9d2111858218901902fa19d95135d9230d760",
        "0495b19c3b9c078adc17ae8459a2dc08a5d0f1c268d17c9153f7fcfdf2af8bc5",
        "84b9c4a25719b0f6283027369caab8bb76e797d6cb129f804ca4a79ddff85e28"},
    8: {"174d1a77b94b03b8472eb2d619cfe39341bd4c5685d1ee3eff6f8ae958f9fe73",
        "147f11cebe89f1a65d2f97564e9f67fcf7a97dd43a4334f3abd21d1219c61022"},
    5: {"42780bfa47f3261b690457d19b72bcc3e30a957f4ba096c350f257aee612b5ab"},
}
# whole-word match
SHORT_HASHES = {
    "6dea2fd9a583c0282664e60a3098927a9c03e657aca81dbce4933918ef8de56c",
    "2727b4974efc6e191938f31d75db92d22711c008f936c3b2448fbecc6a076d6e",
    "8d2879f8b70960bb38cb9eeb42d50003ddf169edbd660194ce9952b8fb33668d",
}
# long terms that may only match at a token or camelCase-part start
ANCHORED_HASHES = {"42780bfa47f3261b690457d19b72bcc3e30a957f4ba096c350f257aee612b5ab"}
EXPECTED_TERM_COUNT = 9

EXCLUDED_PREFIXES = (".claude/", ".githooks/", ".codex/")
TOKEN_RE = re.compile(r"[A-Za-z0-9]+")
CAMEL_RE = re.compile(r"[A-Z]+(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+|[0-9]+")
_cache = {}


def _h(s):
    h = _cache.get(s)
    if h is None:
        h = _cache[s] = hashlib.sha256(s.encode("utf-8")).hexdigest()
    return h


def blocked_words(text):
    hits = []
    for token in TOKEN_RE.findall(text):
        low = token.lower()
        starts = {m.start() for m in CAMEL_RE.finditer(token)} | {0}
        for length, hashes in LONG_HASHES.items():
            for i in range(0, len(low) - length + 1):
                part = low[i:i + length]
                h = _h(part)
                if h in hashes and (h not in ANCHORED_HASHES or i in starts):
                    hits.append(part)
        for word in CAMEL_RE.findall(token):
            if _h(word.lower()) in SHORT_HASHES:
                hits.append(word.lower())
    return hits


def excluded(path):
    return path.startswith(EXCLUDED_PREFIXES)


def git(*args):
    return subprocess.run(["git", "-c", "core.quotePath=false", *args],
                          capture_output=True, check=True).stdout.decode("utf-8", "replace")


def _diff_target(line):
    """Path of a '+++' header line; None for /dev/null. Handles quoted paths."""
    rest = line[4:]
    if rest.startswith('"') and rest.endswith('"'):
        rest = rest[1:-1].encode("latin-1", "backslashreplace").decode("unicode_escape").encode("latin-1").decode("utf-8", "replace")
    return rest[2:] if rest.startswith("b/") else None


def diff_findings(diff_text):
    findings = []
    current = None
    line_no = 0
    for line in diff_text.splitlines():
        if line.startswith("+++ "):
            current = _diff_target(line)
            continue
        if line.startswith("@@"):
            m = re.search(r"\+(\d+)", line)
            line_no = int(m.group(1)) if m else 0
            continue
        if current is None or excluded(current):
            continue
        if line.startswith("+"):
            for w in blocked_words(line[1:]):
                findings.append((current, line_no, w, line[1:].strip()))
            line_no += 1
    return findings


def staged_findings():
    findings = []
    for path in git("diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z").split("\0"):
        if path and not excluded(path):
            for w in blocked_words(path):
                findings.append((path, 0, w, path))
    findings += diff_findings(git("diff", "--cached", "-U0", "--no-color", "--diff-filter=ACMR"))
    return findings


def all_findings():
    findings = []
    for path in git("ls-files", "-z").split("\0"):
        if not path or excluded(path):
            continue
        for w in blocked_words(path):
            findings.append((path, 0, w, path))
        try:
            with open(path, "rb") as fh:
                data = fh.read()
        except OSError:
            continue
        if b"\0" in data[:8000]:
            continue
        for i, text in enumerate(data.decode("utf-8", "replace").splitlines(), 1):
            for w in blocked_words(text):
                findings.append((path, i, w, text.strip()))
    return findings


def self_test():
    terms = sum(len(v) for v in LONG_HASHES.values()) + len(SHORT_HASHES)
    assert terms == EXPECTED_TERM_COUNT, f"blocked list has {terms} terms, expected {EXPECTED_TERM_COUNT}"
    long_probe, short_probe = "zqprobe", "zqx"
    LONG_HASHES.setdefault(len(long_probe), set()).add(_h(long_probe))
    SHORT_HASHES.add(_h(short_probe))
    try:
        # long term: plain, inside identifiers, camelCase, hyphenated host
        assert blocked_words(f"x {long_probe}_field y") == [long_probe]
        assert blocked_words("class ZqprobeConfig {}") == [long_probe]
        assert blocked_words(f"is{long_probe}dev") == [long_probe]
        assert blocked_words(f"https://{long_probe}-dev.crm4") == [long_probe]
        # short term: whole word and camelCase part only
        assert blocked_words("ZqxLogging target") == [short_probe]
        assert blocked_words(f"{short_probe}_field") == [short_probe]
        assert blocked_words(f"{short_probe}abc longer") == [], "short term must not match inside a word"
        # neutral text
        assert blocked_words("contoso_field Update Source StandardCrmConfig") == []
        # anchored long term: token or camelCase start only
        anchored_probe = "zqanc"
        LONG_HASHES.setdefault(len(anchored_probe), set()).add(_h(anchored_probe))
        ANCHORED_HASHES.add(_h(anchored_probe))
        try:
            assert blocked_words("ZqAnc IsZqAnc zqanc_field") == [anchored_probe] * 3
            assert blocked_words("Modelzqancer xyzqanc") == [], "anchored term matched mid-word"
        finally:
            LONG_HASHES[len(anchored_probe)].discard(_h(anchored_probe))
            ANCHORED_HASHES.discard(_h(anchored_probe))
        # real neutral identifiers that once produced false alarms
        assert blocked_words("ModelMapping LabelMapper XmlMapping HtmlMapper ToolMappings") == []
        # diff parsing: quoted non-ASCII path, excluded path, removed lines
        diff = ("+++ \"b/docs/h\\303\\244ufig.md\"\n@@ -0,0 +3 @@\n+a zqprobe_x\n"
                "+++ b/.githooks/x.py\n@@ -0,0 +1 @@\n+zqprobe\n"
                "+++ b/src/a.cs\n@@ -1 +1 @@\n-zqprobe old\n+neutral\n")
        f = diff_findings(diff)
        assert [(p, n, w) for p, n, w, _ in f] == [("docs/häufig.md", 3, long_probe)], f
    finally:
        LONG_HASHES[len(long_probe)].discard(_h(long_probe))
        SHORT_HASHES.discard(_h(short_probe))
    print("pre-commit-local self-test: ok")
    return 0


def main(argv):
    try:
        sys.stderr.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    except (AttributeError, ValueError):
        pass
    if "--self-test" in argv:
        return self_test()
    findings = all_findings() if "--all" in argv else staged_findings()
    if not findings:
        return 0
    print("Kundenneutralität: blockierte Begriffe gefunden (ADR-2026-09-18-1258).", file=sys.stderr)
    for path, line, word, text in findings[:50]:
        where = f"{path}:{line}" if line else f"{path} (Dateiname)"
        print(f"  {where}: '{word}' in: {text[:120]}", file=sys.stderr)
    if len(findings) > 50:
        print(f"  ... und {len(findings) - 50} weitere", file=sys.stderr)
    print("Kundenspezifisches gehört ins Workspace-Repo. Notausweg: git commit --no-verify.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
