# Workflow

## Verification Protocol

Every change that affects emulator boot, HCOM communication, peripheral behavior,
or firmware loading MUST pass the verification tiers below before the phase/track
is considered complete. Run tiers in order — fail fast on Tier 1 before spending
time on Tier 2/3.

### Tier 1 — Smoke Test (hcom_test.py)

Quick automated check that the emulator boots and HCOM responds.

```bash
# Start emulator (legacy build)
./run.sh --legacy &

# Wait for HCOM socket
while ! nc -z localhost 4242 2>/dev/null; do sleep 1; done

# Run smoke test
python3 tools/hcom_test.py

# Expected: GET_DEVICE_INFORMATION response with OSVersion, DeviceName, Hardware
```

**Pass criteria:** hcom_test.py exits 0 and response contains non-empty `OSVersion|`.

### Tier 2 — Meadow.CLI Device Info

Full end-to-end test using the real Meadow.CLI over the emulated HCOM socket.

**Prerequisites:**
- Meadow.CLI 3.0.3 or later, the dotnet global tool (socket routes are in the
  release from 3.0.3): `dotnet tool install -g WildernessLabs.Meadow.CLI`, or
  `dotnet tool update -g WildernessLabs.Meadow.CLI`; `./setup.sh` does this

```bash
# Start emulator (if not already running)
./run.sh --legacy &

# Configure CLI to use emulator socket
meadow config route socket://localhost:4242

# Get device info
meadow device info
```

**Pass criteria:** Output shows correct OSVersion, Hardware (F7FeatherV2), DeviceName (MeadowF7).

### Tier 3 — Blinky App Deploy + Run

Deploy and run the canonical "hello world" Meadow app to verify Mono runtime,
filesystem, and app lifecycle work end-to-end.

**Prerequisites:**
- Meadow.Samples repo: `../Meadow.Samples`
- BlinkyCS project: `Source/Meadow F7/Blinky/BlinkyCS/`
- App MUST declare the correct board matching the emulator config:
  - `F7FeatherV2` for the default emulator board
  - BlinkyCS has both `V1App : MeadowApp<F7FeatherV1>` and `V2App : MeadowApp<F7FeatherV2>`
- CLI route must be configured: `meadow config route socket://localhost:4242`

```bash
# Start emulator (if not already running)
./run.sh --legacy &

# From the BlinkyCS project directory:
cd "../Meadow.Samples/Source/Meadow F7/Blinky/BlinkyCS"

# Build, trim, deploy, and run in one command:
meadow app run

# This will:
# 1. Build the project (Release config)
# 2. Trim assemblies (removes unused BCL code — critical for deploy speed)
# 3. Deploy trimmed binaries to emulator over HCOM
# 4. Enable Mono runtime
# 5. Listen for app console output
```

**Pass criteria:** Deploy completes without errors. Mono runtime boots, loads
App.dll, BlinkyCS initializes. Console output shows LED color cycling.

**IMPORTANT:** Do NOT use `app deploy` with untrimmed binaries — the full BCL
assemblies (mscorlib.dll = 2.3MB, System.dll = 1.9MB, etc.) are far too large
for the emulated UART. Always use `app run` which trims first.

**Current Blockers (as of 2026-03-18):**
- **Mono BCL path mismatch:** CLI deploys assemblies to `/meadow0/` but Mono
  expects `/meadow0/mono/4.5/mscorlib.dll`. Need CLI or SDRAM patch fix.
- **Flash persistence (W.10):** QSPI flash is volatile — deployed files are
  lost when the emulator restarts. Each boot reformats LittleFS.

**Resolved:**
- ~~HCOM download timing~~ — Fixed via UART RX bypass hook (UartRxBypass.cs).
  Bypasses per-byte interrupt-driven UART path. 30x speedup. Full BlinkyCS
  deploy (36 files) completes in ~140s.
- ~~Mono JIT speed~~ — Fixed via SVC/PRIMASK patch in Renode fork (12,000x
  speedup). Mono now boots in ~2-3 minutes at near real-time.

### When to Run Each Tier

| Change Type | Tier 1 | Tier 2 | Tier 3 |
|-------------|--------|--------|--------|
| Hook address changes | Required | Required | - |
| Peripheral migration (Python→C#) | Required | Required | - |
| Firmware rebuild | Required | Required | Blocked* |
| Mono/SDRAM changes | Required | Required | Blocked* |
| Script (.resc) changes | Required | - | - |
| Build script changes | Required | Required | - |
| Flash/LittleFS changes | Required | Required | Blocked* |

*Tier 3 deploy works (36 files in ~140s via UART RX bypass). Mono starts but
can't find BCL assemblies (path mismatch: CLI deploys to `/meadow0/`, Mono
expects `/meadow0/mono/4.5/`). Deployed files lost on restart (no flash
persistence, W.10). Tier 3 becomes fully viable when BCL path is fixed.

## Commit Strategy

- Conventional commits: `<type>(<scope>): <description>`
- No AI attribution in commit messages
- Never push without explicit user approval
- Never commit hacks/workarounds when user wants a proper fix — report blockers instead
- Commit working code only — if a fix requires multiple steps, complete all steps before committing

## External Tool Dependencies

| Tool | Location | Branch | Notes |
|------|----------|--------|-------|
| Meadow.CLI | dotnet global tool | release 3.0.3+ | `dotnet tool install -g WildernessLabs.Meadow.CLI`; socket routes are in the release from 3.0.3. A `../Meadow.CLI` checkout is only needed to work on the CLI. |
| Meadow.Samples | `../Meadow.Samples` | main | BlinkyCS at `Source/Meadow F7/Blinky/BlinkyCS/` |
| Renode (fork) | `../renode` | `meadow-emulator-fixes` | Must be built; `./run.sh` auto-detects |
| ARM GCC 10.3 | `/Applications/ARM/bin` | - | Required for legacy firmware builds only |
