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
  - BlinkyCS has both `V1App : MeadowApp<F7FeatherV1>` and `V2App : MeadowApp<F7FeatherV2>`
- CLI route must be configured: `meadow config route socket://localhost:4242`

```bash
# Start emulator (if not already running)
./run.sh --legacy &

# From the BlinkyCS project directory:
cd "../Meadow.Samples/Source/Meadow F7/Blinky/BlinkyCS"

# Build, trim, deploy, and run in one command:
dotnet run --project /path/to/Meadow.CLI/Source/Meadow.CLI -- app run

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
Deployed files lost on restart (no flash persistence, W.10).

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

## Mono Runtime + BCL Build Chain

The emulator's legacy Mono stack has three independently built components:

### 1. Mono Runtime (nuttx_mono.bin)
- **Source:** `../Meadow/mono/` (Mono fork, submodule in Meadow repo)
- **Config:** `../Meadow/mono/config.h` — generated by `./configure`, NOT tracked in git
  - **MUST have `ENABLE_NETCORE` undef'd** for legacy builds (mscorlib)
  - If defined, runtime expects `System.Private.CoreLib.dll` instead
- **Built by:** `./build-meadow.os-emulated.sh` — compiles Mono into NuttX user ELF, extracts `.mono` sections to `build/legacy/nuttx_mono.bin`
- **Verify after build:** `strings build/legacy/nuttx_mono.bin | grep -c "System.Private.CoreLib"` — must be 0

### 2. BCL Assemblies (mscorlib.dll, System.dll, etc.)
- **Source:** Same `../Meadow/mono/` tree
- **Built by:** `cd ../Meadow && bash build-bcl.sh` (do NOT use `--netcore`)
  - Copies mono/ to monobcl/, runs autogen + configure (`--enable-nuttx --disable-boehm`), then `make -j8`
  - Output: `../Meadow/monobcl/libs/bcl/` (filtered by `bcl-blacklist.txt`)
- **NOT built by** `build-meadow.os-emulated.sh` — must be built separately

### 3. LittleFS Image (littlefs.bin)
- **Source:** BCL assemblies from step 2
- **Built by:** `tools/build_lfs_v1_image ../Meadow/monobcl/libs/bcl build/legacy/littlefs.bin`
  - Places assemblies at filesystem root (`/meadow0/mscorlib.dll`)
  - Creates `meadow.config.yaml` with `MonoControl: --interp`
- **Extract existing:** `tools/extract_lfs_v1_image build/legacy/littlefs.bin /tmp/output_dir`
- **NOT built by** `build-meadow.os-emulated.sh` — must be built separately

### Critical: Meadow repo branch
- **Legacy builds:** Meadow repo must be on `main` branch
- **The `Feature_NuttX_dotnet10` branch** has the same Mono source but different NuttX kernel code
- `config.h` is a local generated file — verify it matches your build intent after branch switches
