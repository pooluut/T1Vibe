# Issue tracker: Local Markdown

Issues and specs for this repo live as Markdown files in `.scratch/`.
All paths below are relative to the repository root.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`.
- The spec is `.scratch/<feature-slug>/spec.md`.
- Implementation issues are one file per ticket:
  `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01`.
- Record triage state in a `Status:` line near the top of each issue.
  Use the values in `docs/agents/triage-labels.md`.
- Append comments and conversation history under `## Comments`.

## When a skill says "publish to the issue tracker"

Write specs to `spec.md` and implementation tickets to individual files
under `issues/`, creating directories as needed.

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. If given only a number, resolve it
within the relevant feature directory. Ask for the feature if ambiguous.

## Wayfinding operations

Used by `/wayfinder`. The map is a file with one child file per ticket.

- Map: `.scratch/<effort>/map.md`, containing Notes,
  Decisions-so-far, and Fog.
- Child ticket: `.scratch/<effort>/issues/NN-<slug>.md`,
  numbered from `01`, with the question in the body.
- Type: record `research`, `prototype`, `grilling`, or `task`
  in a `Type:` line.
- Work status: use `Status: open`, `Status: claimed`, or
  `Status: resolved` for wayfinding tickets. These are workflow
  states separate from the triage vocabulary.
- Blocking: record `Blocked by: NN, NN` near the top.
  A ticket is unblocked when every listed ticket is resolved.
- Frontier: scan the effort's issues for open, unblocked, unclaimed
  tickets; first by number wins.
- Claim: set `Status: claimed` and save before starting work.
- Resolve: append the answer under `## Answer`, set
  `Status: resolved`, and append a gist and relative link to
  Decisions-so-far in the map.
