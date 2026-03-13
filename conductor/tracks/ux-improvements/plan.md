# Plan: UX Improvements

## Track ID: `ux-improvements`
## Status: `planning`
## Repo: `Meadow.OS.Emulator`

---

## 1. Prerequisites & Setup Script

### Goal
A single `setup.sh` script in the emulator repo root that checks for all build
prerequisites and installs anything missing. It should cover everything needed to
go from a fresh clone to a working emulator — firmware build, Renode build, and
CLI build.

### Design

```
./setup.sh              # default: legacy Meadow OS
./setup.sh --legacy     # explicit: legacy (Meadow/)
./setup.sh --rebase     # Meadow.OS/ rebase branch
```

#### What it checks/installs

**1. Meadow OS build tools** — delegate to the upstream repo's installer:
- Legacy (`../Meadow/`): runs `install-packages.sh` (Homebrew packages: arm-gcc,
  ccache, autoconf, libtool, cmake, libusb, automake, dfu-util, srecord) then
  `build-tools.sh` (kconfig, gperf, ncurses, flex, bison)
- Rebase (`../Meadow.OS/`): equivalent script (TBD — may need to create one
  from its Dockerfile deps)

**2. Renode fork** — check for sibling `../renode/` repo:
- Clone `WildernessLabs/renode` if missing
- Checkout `meadow-emulator-fixes` branch
- Init submodules (`src/Infrastructure`)
- Build with appropriate flags (`--host-arch aarch64` on Apple Silicon)
- Verify `output/bin/Release/Renode.dll` exists

**3. .NET SDK** — needed to run Renode and Meadow CLI:
- Check `dotnet` is on PATH (including `/usr/local/share/dotnet`)
- Print install instructions if missing (Homebrew or official installer)

**4. Meadow CLI** — check for sibling `../Meadow.CLI/` repo:
- Verify it builds (`dotnet build`)
- Check TCP socket branch (`feature/TCP_Socket_Connection_for_EMU`)

**5. Sibling repos** — validate expected layout:
```
wilderness_labs/
├── Meadow/                  # or Meadow.OS/
├── Meadow.OS.Emulator/     # this repo
├── Meadow.CLI/
├── Meadow.Samples/
└── renode/                  # WildernessLabs fork
```

#### Existing upstream scripts to leverage

| Script | Repo | What it does |
|--------|------|--------------|
| `install-packages.sh` | `Meadow/` | Homebrew: arm-gcc, ccache, autoconf, libtool, cmake, libusb, automake, dfu-util, srecord |
| `build-tools.sh` | `Meadow/` | Builds kconfig, installs gperf/ncurses/flex/bison (Ubuntu), ST-Link |
| `Dockerfile` | `Meadow/DockerFiles/Build/` | Full Ubuntu build env reference (arm-none-eabi-gcc, NuttX tools, python3, clang-15) |
| `Dockerfile` | `Meadow.OS/DockerFiles/Build/` | Same for rebase branch |

#### Behavior
- Idempotent — safe to re-run; skips anything already installed
- macOS-first (Homebrew), with Linux (apt) as secondary
- Non-destructive — never removes or overwrites existing tools
- Prints a summary at the end: what was installed, what was already present, what failed

### README update

Update `README.md` to reference `setup.sh` as the primary getting-started path:
- Add a **Quick Start** section near the top: clone, run `./setup.sh --legacy`,
  build firmware, run emulator
- Simplify the existing Prerequisites section — point to `setup.sh` for automated
  install, keep the manual list as a reference
- Document the `--legacy` / `--rebase` flags

---

## Future items (not yet planned)

_Add additional UX improvement tasks here as they come up._
