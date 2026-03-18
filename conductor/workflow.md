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
- Meadow.CLI repo: `../Meadow.CLI` (branch `feature/TCP_Socket_Connection_for_EMU`)
- Must be built from source until SocketConnection is published to NuGet
- Build: `dotnet build Source/Meadow.CLI.sln`

```bash
# Start emulator (if not already running)
./run.sh --legacy &

# Configure CLI to use emulator socket
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- config route socket://localhost:4242

# Get device info
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- device info
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
  - The entry point class is selected by Meadow from the compiled App.dll
  - BlinkyCS has both `V1App : MeadowApp<F7FeatherV1>` and `V2App : MeadowApp<F7FeatherV2>`

```bash
# Build the app
dotnet build "../Meadow.Samples/Source/Meadow F7/Blinky/BlinkyCS/BlinkyCS.csproj"

# Deploy to emulator via CLI
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- app deploy \
    "../Meadow.Samples/Source/Meadow F7/Blinky/BlinkyCS/bin/Debug/netstandard2.1/"

# Run (or reset emulator to auto-start the app)
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- app run
```

**Pass criteria:** Mono runtime boots, loads App.dll, BlinkyCS initializes without crash.
Console output shows LED initialization. No unhandled exceptions.

### When to Run Each Tier

| Change Type | Tier 1 | Tier 2 | Tier 3 |
|-------------|--------|--------|--------|
| Hook address changes | Required | Required | - |
| Peripheral migration (Python→C#) | Required | Required | - |
| Firmware rebuild | Required | Required | Recommended |
| Mono/SDRAM changes | Required | Required | Required |
| Script (.resc) changes | Required | - | - |
| Build script changes | Required | Required | - |
| Flash/LittleFS changes | Required | Required | Required |

## Commit Strategy

- Conventional commits: `<type>(<scope>): <description>`
- No AI attribution in commit messages
- Never push without explicit user approval
- Never commit hacks/workarounds when user wants a proper fix — report blockers instead
- Commit working code only — if a fix requires multiple steps, complete all steps before committing

## External Tool Dependencies

| Tool | Location | Branch | Notes |
|------|----------|--------|-------|
| Meadow.CLI | `../Meadow.CLI` | `feature/TCP_Socket_Connection_for_EMU` | Build from source; SocketConnection not yet on NuGet. Will merge to main eventually. |
| Meadow.Samples | `../Meadow.Samples` | main | BlinkyCS at `Source/Meadow F7/Blinky/BlinkyCS/` |
| Renode (fork) | `../renode` | `meadow-emulator-fixes` | Must be built; `./run.sh` auto-detects |
| ARM GCC 10.3 | `/Applications/ARM/bin` | - | Required for legacy firmware builds only |
