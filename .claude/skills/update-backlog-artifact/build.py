# Regenerate the backlog Artifact page from backlog.md and the GitHub issue tracker.
#
#   python build.py [--no-issues] <output.html>
#
# Parses every top-level `- \`BL-NNN\` ...` bullet under each `## ` theme heading
# (excluding "## Standing notes") into one row object per item:
#   {theme, id, type, status, size, next, impact, evidence, scope, title}
# then adds one row per open `backlog`-labelled GitHub issue (id "#N", theme and
# tags from its theme:/type:/size:/next:/impact:/evidence: and status labels) into
# its theme after the BL- items, and injects them all as JSON in place of the
# template's __BACKLOG_DATA__ placeholder. --no-issues skips the tracker.
#
# All file I/O is explicit UTF-8; edit this file with the Read/Edit/Write tools
# rather than a PowerShell round-trip (see CLAUDE.md).

import io
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
SOURCE = os.path.join(REPO, "backlog.md")
TEMPLATE = os.path.join(HERE, "template.html")

# An optional {SCOPE} marker may sit between the tags and the bold title.
BULLET = re.compile(r"^- `(BL-\d+)` (.*)$")
TAGS = re.compile(r"^((?:`\[[^`]*?\]` ?)+)")
TAG = re.compile(r"`\[([^`]*?)\]`")
SCOPE = re.compile(r"^\{[^}]*\}\s*")
TITLE = re.compile(r"\*\*(.+?)\*\*")

# The tag vocabularies backlog.md's header documents. CheckItemIds.ps1 enforces
# the same lists at commit time; keep the two in step.
TYPES = ["Bug", "Feature", "Research", "Tuning", "Cleanup", "Fidelity", "Perf", "Tooling", "Testing"]
STATUS = re.compile(r"^(Owed-playtest|Blocked: .+|Divergence)$")
SIZES = ["S", "M", "L"]
PROPERTY = re.compile(r"^(Next|Impact|Evidence): (\w+)$")
VOCAB = {
    "Next": ["decode", "data", "code", "look", "decide"],
    "Impact": ["high", "low", "none"],
    "Evidence": ["decoded", "data", "footage", "spec", "feel", "trace"],
}
SCOPE_TAG = re.compile(r"^[A-Z]{1,2}\d+[A-Z]?$")


def classify(item_id, taglist):
    """Sort a bullet's leading tags into the row's fields. Returns (fields, problems)."""
    fields = {"type": None, "status": None, "size": None, "next": None,
              "impact": None, "evidence": None, "scope": None}
    problems = []
    if taglist:
        fields["type"] = taglist[0]
        if taglist[0] not in TYPES:
            problems.append("unknown type [%s]" % taglist[0])
    for tag in taglist[1:]:
        pm = PROPERTY.match(tag)
        if STATUS.match(tag):
            fields["status"] = tag
        elif tag in SIZES:
            fields["size"] = tag
        elif pm:
            key, value = pm.group(1), pm.group(2)
            if value not in VOCAB[key]:
                problems.append("[%s: %s] is not one of %s" % (key, value, ", ".join(VOCAB[key])))
            fields[key.lower()] = value
        elif SCOPE_TAG.match(tag):
            fields["scope"] = tag
        else:
            problems.append("unknown tag [%s]" % tag)
    return fields, problems


def parse(src):
    """Return (rows, themes, problems). rows are dicts, see the header comment."""
    lines = src.split("\n")
    rows, themes, problems, theme = [], [], [], None
    i, n = 0, len(lines)
    while i < n:
        line = lines[i]
        if line.startswith("## "):
            heading = line[3:].strip()
            theme = None if heading == "Standing notes" else heading
            if theme and theme not in themes:
                themes.append(theme)
            i += 1
            continue
        m = BULLET.match(line)
        if not (m and theme):
            i += 1
            continue
        # The bullet runs until the next top-level bullet or theme heading.
        block = [m.group(2)]
        j = i + 1
        while j < n and not (lines[j].startswith("- `BL-") or lines[j].startswith("## ")):
            block.append(lines[j])
            j += 1
        text = re.sub(r"\s+", " ", " ".join(block)).strip()

        tagstr = TAGS.match(text)
        tagstr = tagstr.group(1) if tagstr else ""
        taglist = TAG.findall(tagstr)
        fields, tag_problems = classify(m.group(1), taglist)
        rest = SCOPE.sub("", text[len(tagstr):])
        tm = TITLE.match(rest)
        title = tm.group(1).strip() if tm else None
        if fields["type"] is None or title is None:
            sys.stderr.write("PARSE FAIL %s: %s\n" % (m.group(1), text[:120]))
        for p in tag_problems:
            problems.append("%s: %s" % (m.group(1), p))
        row = {"theme": theme, "id": m.group(1)}
        row.update(fields)
        row["title"] = title
        rows.append(row)
        i = j
    return rows, themes, problems


# Issues carry backlog.md's tags as labels (docs/agents/issue-tracker.md, "Backlog labels"):
# theme:<slug> whose description is the backlog.md heading, type:<lower>, size:, next:,
# impact:, evidence:, and the status labels below. A triage label stands in for the status
# when no status label is present.
UNTRIAGED = "Untriaged"
STATUS_LABELS = {"owed-playtest": "Owed-playtest", "blocked": "Blocked", "divergence": "Divergence"}
TRIAGE = ["needs-triage", "needs-info", "ready-for-agent", "ready-for-human", "wontfix"]


def gh_json(args):
    out = subprocess.run(["gh"] + args, capture_output=True, cwd=REPO, shell=(os.name == "nt"))
    if out.returncode != 0:
        raise RuntimeError(out.stderr.decode("utf-8", "replace").strip() or "gh failed")
    return json.loads(out.stdout.decode("utf-8"))


def fetch_issues():
    """Open `backlog` issues as rows. Returns (rows, problems); raises on a gh failure."""
    themes = {l["name"][len("theme:"):]: l["description"]
              for l in gh_json(["label", "list", "--limit", "500", "--json", "name,description"])
              if l["name"].startswith("theme:")}
    issues = gh_json(["issue", "list", "--state", "open", "--label", "backlog", "--limit", "500",
                      "--json", "number,title,labels"])
    rows, problems = [], []
    for issue in issues:
        iid = "#%d" % issue["number"]
        row = {"theme": UNTRIAGED, "id": iid, "type": None, "status": None, "size": None,
               "next": None, "impact": None, "evidence": None, "scope": None,
               "title": issue["title"].strip()}
        names = [l["name"] for l in issue.get("labels", [])]
        for name in names:
            group, _, value = name.partition(":")
            if not value:
                row["status"] = row["status"] or STATUS_LABELS.get(name)
            elif group == "theme":
                if value not in themes:
                    problems.append("%s: no theme label %s" % (iid, name))
                row["theme"] = themes.get(value, UNTRIAGED)
            elif group == "type":
                row["type"] = next((t for t in TYPES if t.lower() == value), None)
                if row["type"] is None:
                    problems.append("%s: unknown type label %s" % (iid, name))
            elif group == "size":
                row["size"] = value if value in SIZES else None
                if row["size"] is None:
                    problems.append("%s: unknown size label %s" % (iid, name))
            elif group.capitalize() in VOCAB:
                if value not in VOCAB[group.capitalize()]:
                    problems.append("%s: %s is not one of %s"
                                    % (iid, name, ", ".join(VOCAB[group.capitalize()])))
                row[group] = value
        row["status"] = row["status"] or next((l for l in TRIAGE if l in names), None)
        row["type"] = row["type"] or "Issue"
        rows.append(row)
    rows.sort(key=lambda r: int(r["id"][1:]))
    return rows, problems


def main():
    args = [a for a in sys.argv[1:] if a != "--no-issues"]
    with_issues = "--no-issues" not in sys.argv
    if len(args) != 1:
        sys.stderr.write("usage: build.py [--no-issues] <output.html>\n")
        return 2
    out_path = args[0]

    src = io.open(SOURCE, encoding="utf-8").read()
    rows, themes, problems = parse(src)

    # Sanity check: every raw bullet in the file must have produced a row.
    raw = len(re.findall(r"^- `BL-\d+`", src, re.M))
    if raw != len(rows):
        sys.stderr.write("MISMATCH: %d raw bullets, %d rows parsed\n" % (raw, len(rows)))
        return 1
    ids = [r["id"] for r in rows]
    dupes = sorted(set(x for x in ids if ids.count(x) > 1))
    if dupes:
        sys.stderr.write("DUPLICATE IDS: %s\n" % ", ".join(dupes))
        return 1
    if any(r["type"] is None or r["title"] is None for r in rows):
        return 1
    if problems:
        for p in problems:
            sys.stderr.write("TAG: %s\n" % p)
        return 1

    issues = []
    if with_issues:
        try:
            issues, issue_problems = fetch_issues()
        except Exception as e:  # noqa: BLE001 - any gh failure is fatal, named
            sys.stderr.write("ISSUES: %s (pass --no-issues to build from backlog.md alone)\n" % e)
            return 1
        if issue_problems:
            for p in issue_problems:
                sys.stderr.write("LABEL: %s\n" % p)
            return 1
        # Each issue joins its theme after that theme's BL- items; a theme only issues use
        # goes after the file's themes, and Untriaged last.
        for issue in issues:
            if issue["theme"] not in themes and issue["theme"] != UNTRIAGED:
                themes.append(issue["theme"])
        if any(r["theme"] == UNTRIAGED for r in issues):
            themes.append(UNTRIAGED)
        rows = [r for t in themes for r in rows + issues if r["theme"] == t]

    tpl = io.open(TEMPLATE, encoding="utf-8").read()
    if "__BACKLOG_DATA__" not in tpl:
        sys.stderr.write("template.html has no __BACKLOG_DATA__ placeholder\n")
        return 1
    tpl = tpl.replace("__BACKLOG_DATA__", json.dumps(rows, ensure_ascii=False))
    io.open(out_path, "w", encoding="utf-8").write(tpl)

    untagged = sum(1 for r in rows if r["size"] is None)
    sys.stderr.write("%d items across %d themes (%d without property tags, %d GitHub issues) -> %s\n"
                     % (len(rows), len(themes), untagged, len(issues), out_path))
    sys.stderr.write("themes: %s\n" % " | ".join(themes))
    return 0


if __name__ == "__main__":
    sys.exit(main())
