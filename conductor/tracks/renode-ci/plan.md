# Plan: CI Integration for Emulated Testing

## Track ID: `renode-ci`
## Status: `planned`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## 1 — Create Test Runner Script
- [ ] Create `scripts/run-tests.sh`
- [ ] Renode backend: run `.resc` script in headless mode, capture UART output, detect test pass/fail
- [ ] Implement timeout and exit-code reporting

## 2 — Renode Robot Framework Tests (optional)
- [ ] Explore Renode's Robot Framework integration for structured test cases
- [ ] Create `.robot` test files for key boot/peripheral scenarios
- [ ] Integrate with CI reporting

## 3 — Pipeline Integration
- [ ] Add emulation test step to CI (Azure Pipelines per existing `azure-pipelines.yml` in Meadow.OS)
- [ ] Install Renode in CI environment (Linux container)
- [ ] Build Meadow.OS → run in Renode → validate boot + tests → report
- [ ] Gate merges on emulated test pass (once stable)

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
