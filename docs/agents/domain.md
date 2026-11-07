# Domain Docs

## Layout

This repo uses a single-context layout:

- `CONTEXT.md` at the repository root: domain vocabulary and concepts.
- `docs/adr/`: architecture decision records.

## Before exploring the codebase

Read the root `CONTEXT.md` and ADRs relevant to the area being explored.

If these files or directories do not exist, proceed silently.
The domain-modeling skill creates them when terms or decisions
are resolved.

## Use the glossary's vocabulary

Use domain terms as defined in `CONTEXT.md` in issue titles,
proposals, hypotheses, and test names.

If a concept is missing, reconsider whether it belongs in the
project's vocabulary. Note real gaps for domain-modeling.

## Surface ADR conflicts

If a proposal contradicts an existing ADR, identify the ADR
and explain why the decision should be reconsidered.
