---
name: new-plan
description: Start a new plan from the repo's plan template — writes docs/PLAN-<name>.md with the right sections, and offers to fill it out from the analysis already done in this session (a grilling, a backlog sweep, a capture analysis). Use when starting a new milestone/feature/polish plan. Answer "scaffold only" and you get structure alone, for you, the Plan agent, or milestone-polish to fill.
---

You start a new plan. Always the structure; and — when this session has already done the thinking —
the content too, written down from what the session established. You never *invent* content: you
record what is already known, and mark every gap.

`docs/agents/plan-template.md` is the canonical structure — read it first; it is the source of truth for
every section, the `[core]`/`[situational]` split, and the wave-letter + number item IDs (`A1`,
`B11`, …). Do not restate its content here; follow it.

## 1. Settle name, weight, and whether to fill

**Name / path.** A milestone plan → `docs/PLAN-M<n>-<name>.md`; a feature or refactor plan →
`docs/PLAN-<name>.md`. Always `docs/` root (a plan there is *live*). Kebab-case the `<name>`. Take it from the argument if it's obvious.

**Can you fill it?** Decide *before* asking. You may offer to fill only when **both** hold:

- **Provenance.** The plan's substance already exists in this session's transcript — a `/grilling`,
  a backlog sweep, an `/analyse-capture`, a `Plan` agent's output, or the author's own stated
  analysis. Not your own inference, and not something you would go and read the repo to find out.
- **Readiness.** You can name the checklist items *and* a one-line Approach for each right now,
  without further exploration.

If you would have to go read code, decodes or backlog entries to fill it, you cannot fill it. Don't
offer — scaffold, and say in one line why.

**Then ask once.** A single `AskUserQuestion` carrying both open questions, before you write
anything:

- **Weight** — routine (`[core]` sections only) or research-heavy (add the `[situational]` sections:
  Decisions, the ⚠ disproven-claims/confidence tables, the data survey). Put the weight you actually
  think is right first, marked `(Recommended)`; you have the session context, so guess informedly
  rather than presenting a blank choice.
- **Fill** — "fill it out from this session" vs "scaffold only". Omit this question entirely when the
  gate above failed.

Do **not** ask for the date — take today's date from your context for the ACTIVE PLAN banner.

## 2. Write the file — once, complete

Create `docs/PLAN-<name>.md` from the template:

- Include every `[core]` section always.
- Include a `[situational]` section when the chosen weight calls for it, **or** when this session
  actually produced its content regardless of weight — a grilling that settled scope gets its
  **Decisions** table (it is the authority when the prose contradicts itself, and it is the part
  nobody can reconstruct from code later); a disproof made this session gets its ⚠ wrong-claims row.
  Never include a `[situational]` section empty.
- Strip the top meta banner and every `<!-- guidance -->` comment — the written plan is clean.
- Keep the **Ground rules** section verbatim from the template — plans are self-contained, so it is
  inlined, not referenced.

**If scaffold only:** fill in the title, the ACTIVE PLAN date, and the scope paragraph if the author
gave you scope. Leave all item content, decisions and evidence as the template's `<…>` placeholders.
Seed the Checklist with one empty wave (`### Wave A — <name>`) and one placeholder item
(`A1 ☐ <title>`) plus a matching per-item detail stub, so the shape is obvious. Then hand it over and
point at how to fill it (their own drafting, the `Plan` agent, or `milestone-polish`).

**If filling:** write the real waves, items and per-item sections from what the session established.

- **Fill inline, in this session — never delegate the fill to a subagent.** The transcript is the
  whole asset here, and a subagent does not have it.
- **Every field the session doesn't cover becomes an explicit `<TODO: what's missing>`**, not a
  silent omission and not a guess. A visibly unfilled field is the property that makes filling safe.
- **The Evidence confidence grade is capped by provenance**, never by how convincing the discussion
  felt:
  - `traced` — only with a path + line, or a decode produced in this session.
  - `direction-sound` — only with a measurement behind it. The magnitude stays TUNE.
  - `lead-only` — everything resting on discussion. Most grilling-derived items land here. Say so.
- **Backlog-derived items: record provenance, don't re-check.** The template requires each item drawn
  from `backlog.md` or from a `backlog` GitHub issue (`docs/agents/issue-tracker.md`) to have been
  re-verified still-open against both the record and the code. A checklist item cites its source
  id, `BL-NNN` or `#N`. State in the scope paragraph which items *were* re-verified in this
  session and which were not; give each unverified one a
  `<TODO: re-verify still-open against git log --grep (or gh issue view N --json state) + the code>`.
  Do not run the check yourself — that is the exploration this skill doesn't do.
- **Verify lines and Model recommendations** are usually not in a conversation. If the session didn't
  settle them, they are TODOs like anything else.

Then report back: the path, what you filled, and **the full TODO list** — grouped by item — so the
author can see at a glance which items still need Evidence, a Verify, or a re-verification, and
decide whether to write them or hand them to the `Plan` agent.

## 3. Remind them of the lifecycle (don't do it — just point)

The process of *running* a plan lives in other tools; name them so the author knows where to go:

- **Landing each item** — flip its checklist status ☐/◐ → ☑ (or ❌ if disproven), rewrite its
  per-item section to lead with **Landed.** / **Verified.** and keep the pre-landing text under
  **Original approach (kept for reference).**, then commit. The commit ritual (a message body
  carrying the what-landed/verification record, PROJECT_CONTEXT.md "Current status" refresh,
  `Co-Authored-By` trailer naming the acting agent, main only) is the **`commit-next`** skill — use
  it per item.
- **Completing the plan** — the closing commit deletes the plan file (git keeps it; there is no
  archive directory), records the completion in its message, clears PROJECT_CONTEXT.md's "Current
  status" pointer (or names the next plan), and unlinks any live prose that linked the file by path
  (grep the filename). Completed plans are cited by name and item, never by path.
