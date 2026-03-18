# Specification: Workaround Cleanup

## Track ID
`workaround-cleanup`

## Problem Statement
The emulator has accumulated workarounds at multiple levels: IronPython hooks on hot paths, Python peripherals handling every register access, dead code from superseded fixes, and documented workarounds that were never deployed or don't match reality. Several of these impose measurable performance penalties or create maintenance confusion.

## Goal
1. Remove dead code from fixes now handled natively in the Renode fork
2. Migrate remaining IronPython hot-path hooks to compiled C# or proper peripheral models
3. Rebuild firmware with version compiled in (eliminating need for runtime version hooks)
4. Correct documentation to match actual codebase state
5. Implement missing workarounds that are documented but not deployed

## Scope
All items from KnownIssues_and_Errata.md where the documented status doesn't match reality, where IronPython is used on hot paths, or where a proper fix should replace a hack.

## Out of Scope
- Upstream PRs to Renode (covered by `renode-upstream` track)
- New feature work
- Flash persistence (separate track candidate)
