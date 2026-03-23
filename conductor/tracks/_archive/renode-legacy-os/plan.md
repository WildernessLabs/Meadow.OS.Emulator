# Plan: Emulator Support for Production Meadow OS (Legacy NuttX)

## Track ID: `renode-legacy-os`
## Status: `complete`
## Repo: `Meadow.OS.Emulator`

---

## 1 — Build Script for Legacy Repo ✅

- [x] Create `build-meadow.os_legacy-emulated.sh` based on `build-meadow.os-emulated.sh`
- [x] Point `MEADOW_DIR` to `../Meadow` instead of `../Meadow.OS`
- [x] Use legacy config path: `stm32f777zit6-meadow/mono` (slash separator)
- [x] Adapt kconfig-tweak calls for legacy NuttX config option names
- [x] Handle legacy shared header path (`nuttx/include/meadow/hcom_nuttx_shared.h`)
- [x] Handle legacy toolchain path (`Meadow/toolchain/macos/`)
- [x] Output artifacts to `build/legacy/` (separate from `build/`)
- [x] Protected-mode build: pass2 → pass1deps → pass1
- [x] Split binary output: nuttx_vectors.bin, nuttx_kernel.bin, nuttx_user.bin (avoids zero-fill between vector table and kernel text)
- [x] Version injection for hcom_nuttx_shared.h and user-space.ld
- [x] mono_main stub (HCOM calls it unconditionally)
- [x] mbedtls build and kernel Makefile patch for MBEDTLS_AOBJS
- [x] Cleanup trap restores modified files in Meadow repo on exit

## 2 — Verify Emulator Config Tweaks on Legacy ✅

- [x] Verify USB-related config options exist in legacy (CONFIG_CDCACM, CONFIG_USBDEV, etc.)
- [x] Verify UART4 config options work in legacy NuttX
- [x] Verify CONFIG_HCOM_COMMS_DEVICE_NAME exists in legacy codebase
- [x] Disable CONFIG_EXAMPLES_MONO (requires cross-compiled Mono runtime)
- [x] Set CONFIG_BOARD_RESET_ON_ASSERT=0 (prevent reset loops)
- [x] Route syslog to USART1 serial console, keep RAMLOG_CONSOLE for fault handler
- [x] Build completes successfully

## 3 — Renode Launch Scripts for Legacy ✅

- [x] Create `scripts/meadow-legacy.resc` — interactive mode (showAnalyzer for USART1)
- [x] Create `scripts/meadow-legacy-headless.resc` — headless mode (file backend for USART1)
- [x] Load split binaries from `build/legacy/` paths (user→vectors overlay→kernel)
- [x] Same `.repl` platform definition (same SoC)
- [x] HCOM socket terminal on port 4242
- [x] RCC pydev and OTG FS stub loaded

## 4 — IronPython Hook Extraction for Legacy ✅

- [x] Build legacy NuttX and extract ELF
- [x] Find equivalent hook addresses (via `arm-none-eabi-nm`/`objdump`):
  - SVC-with-PRIMASK fix (6 addresses: 0x08162f72, 0x081631be, 0x081631ce, 0x081631d8, 0x081631e4, 0x0817b148)
  - GPIO version detection fix (0x08140d64 → writes CCMv2=3 to hw_version struct at 0x20020ce4)
  - QUADSPI TCF skip (0x0816135e, 0x08161270)
  - QUADSPI DMA bypass (0x0816136a)
  - LSI calibration skip (0x0814d0d8)
- [x] Hooks inlined in .resc scripts (same pattern as new OS, different addresses)

## 5 — Build/Launch Selector ✅

- [x] Update `run.sh` to accept a `--legacy` flag
- [x] Route to correct build artifacts and Renode scripts
- [x] Usage: `./run.sh --legacy` vs `./run.sh` (default = new)
- [x] Checks for correct build artifacts before launching

## 6 — End-to-End Verification ✅

- [x] Build legacy OS with `build-meadow.os_legacy-emulated.sh`
- [x] Boot in Renode with `meadow-legacy-headless.resc`
- [x] Verify boot reaches idle — full boot chain confirmed:
  - RNG init → flash partition table → LittleFS mount → HCOM startup
  - "Mono runtime was not found flashed in external flash" (expected)
- [x] Test HCOM via `hcom_test.py` on port 4242 — GET_DEVICE_INFORMATION returns valid response:
  - Product=Meadow, Model=F7Micro, Hardware=F7CoreComputeV2, ProcessorType=STM32F777IIK6
- [x] Test Meadow.CLI `meadow config route socket://localhost:4242` then `meadow device info` — returns correct board/processor/hardware info

---

## Implementation Notes

- The `.repl` platform file is shared — both repos target identical hardware
- Hook addresses differ from new OS build — extracted from legacy ELF
- Legacy NuttX uses protected mode (kernel+user split) requiring split binary loading
- `--console --port -1` causes Renode to exit after script; use `--port <N>` for persistent runs
- The legacy `build.sh` was used as reference for version injection, toolchain setup, etc.

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03-09 | Separate build script, not parameterized | Keeps legacy/new paths clean, avoids brittle conditionals |
| 2026-03-09 | Separate Renode scripts | Hook addresses differ per binary, can't share |
| 2026-03-09 | Shared `.repl` platform | Same SoC/board — no hardware differences |
| 2026-03-09 | `build/legacy/` output dir | Clear separation of artifacts |
| 2026-03-09 | Split binary loading (vectors+kernel+user) | Single nuttx.bin zero-fills gap between vectors@0x08040000 and kernel@0x08140000, overwriting user code |

## Changes Log

### Commit 5eb67b0 — Initial legacy scaffolding
- Created `build-legacy.sh` with basic build flow (configure, kconfig-tweak, make)
- Created `meadow-legacy.resc` and `meadow-legacy-headless.resc` with placeholder hooks (`0x????????`) — addresses unknown until first successful build
- Updated `run.sh` with `--legacy` flag for OS variant selection

### Commit 5d021f1 — Script renames
- `build-emulator.sh` → `build-meadow.os-emulated.sh`, `build-legacy.sh` → `build-meadow.os_legacy-emulated.sh`
- **Why:** Naming convention clarity — the old names didn't indicate which OS they targeted

### Commit 6f302bb — kconfig-conf shim
- Added `tools/kconfig-conf` shim that translates legacy NuttX `kconfig-conf` calls to kconfiglib (Python)
- Removed `make context` step from build script
- **Why:** Legacy NuttX's build system invokes `kconfig-conf` (a C binary from the kernel kconfig tools) during `make context`. The emulator environment uses kconfiglib (Python) via `kconfig-tweak`, but doesn't have the native `kconfig-conf` binary. The shim bridges this gap. The `make context` step was also triggering this dependency before `$PATH` included our tools dir.

### Unstaged: build-meadow.os_legacy-emulated.sh — Major bringup fixes

**Toolchain setup:**
- Added `/Applications/ARM/bin` to PATH for ARM GCC 10.3
- Added toolchain version logging (`arm-none-eabi-gcc --version`, `ld.lld --version`)
- **Why:** Legacy NuttX requires GCC ~10.x (not the newer toolchain from Homebrew). The `toolchain/macos/` dir in the Meadow repo provides `ld.lld` (LLVM linker used by the kernel Makefile) but not GCC itself — that comes from `/Applications/ARM/`.

**Config tweaks discovered during bringup:**
- Kept `CONFIG_STM32F7_OTGFS` enabled (was previously disabled)
- **Why:** The `pwrmgmt` code unconditionally references OTG FS registers during init. Disabling the peripheral in Renode causes access faults. Leaving it enabled means Renode provides a stub that returns safe defaults.
- Added `CONFIG_USART1_SERIAL_CONSOLE` — explicitly routes serial console to USART1
- Added `CONFIG_RAMLOG_CONSOLE` (kept alive) while disabling `CONFIG_RAMLOG_SYSLOG`
- **Why:** The fault handler calls `ramlog_get_sysbuffer_pointer()` unconditionally. Disabling RAMLOG entirely causes a link error. `RAMLOG_CONSOLE` keeps the buffer available without routing syslog through it.
- Added `CONFIG_SYSLOG_WRITE` — enables the syslog write path
- Added `CONFIG_EXAMPLES_MONO=n` — disables the Mono application
- **Why:** Building Mono requires a separate cross-compilation step with the Mono runtime. For emulator bringup we don't need it — the `mono_main` stub satisfies the linker.

**mbedtls cross-compilation:**
- Added inline mbedtls build with ARM cross-compiler flags (`-mthumb -mcpu=cortex-m7 -mfloat-abi=hard -mfpu=fpv5-d16`)
- **Why:** The kernel link step expects `libmbedcrypto.a`, `libmbedtls.a`, `libmbedx509.a`. The production build runs `build-mbedtls.sh` separately, but for the emulator we build it inline if the libraries don't already exist.

**mono_main stub:**
- Creates a minimal `mono_main()` stub, compiles it, and archives into `libmonostub.a`
- **Why:** HCOM's app thread calls `mono_main()` unconditionally after startup. Without Mono built, this is an undefined symbol at link time. The stub returns 0 immediately, allowing HCOM to report "Mono runtime was not found" and continue operating normally.

**Kernel Makefile patch:**
- Injects `MBEDTLS_AOBJS ?=` fallback with paths to mbedtls libraries and mono stub
- **Why:** When `CONFIG_EXAMPLES_MONO=n`, the Makefile's `MBEDTLS_AOBJS` variable is never set (it's defined inside a conditional block gated on Mono being enabled). The kernel link step then fails with missing mbedtls symbols. The `?=` fallback only takes effect when the variable is unset, so it won't interfere with a full Mono build.

**Version injection for user-space.ld:**
- Added sed-based version placeholder replacement in the user-space linker script
- **Why:** `user-space.ld` contains `###VERSION_MAJOR###` etc. placeholders (same as `hcom_nuttx_shared.h`). The production `build.sh` replaces these via `scripts/version_methods.sh`. Without replacement, the linker script has syntax errors and the user-space link fails.

**Split binary output:**
- Changed from copying `nuttx.bin` + `nuttx_user.bin` to extracting three pieces via `objcopy`:
  1. `nuttx_vectors.bin` — interrupt vector table (~504 bytes from `.vectors` section)
  2. `nuttx_kernel.bin` — kernel text+data (everything except `.vectors`)
  3. `nuttx_user.bin` — user binary with Mono/SDRAM sections stripped
- **Why:** Legacy NuttX's protected-mode memory layout puts vectors at 0x08040000 and kernel text at 0x08140000 — a 1MB gap. A flat `nuttx.bin` produced by `objcopy -O binary` zero-fills this gap, creating a ~1.3MB file where the zeroes overwrite user code that lives at 0x08040200+. Loading three separate pieces at their correct addresses avoids this. The Mono sections (`.mono`, `.mono_data`, `.mono_bss` at 0xC0000000 in SDRAM) are stripped from the user binary to avoid a ~3GB zero-fill for the same reason.

**Cleanup trap expansion:**
- Now restores `user-space.ld` and kernel `Makefile` in addition to `hcom_nuttx_shared.h`
- **Why:** The build script modifies files in the Meadow repo (version placeholders, Makefile patch). The trap ensures the Meadow repo is left clean even if the build fails partway through.

### Unstaged: meadow-legacy-headless.resc / meadow-legacy.resc — Hooks populated

- Memory layout corrected from initial guess (0x08000000/0x08100000) to actual legacy layout (vectors@0x08040000, kernel@0x08140000)
- **Why:** Initial scaffolding assumed the same memory map as the new OS. The legacy NuttX linker scripts put the vector table at 0x08040000 (start of "uflash" region) and kernel text at 0x08140000 (start of "kflash" region) — discovered by examining `nuttx.elf` section headers.
- Binary loading order: user first, then vectors overlaid on top, then kernel
- **Why:** User code starts at 0x08040200 (after the 512-byte vector table gap in uflash). Loading user at 0x08040000 and then overlaying the 504-byte vector table on top correctly populates both the vectors and the user code in a single contiguous flash region.
- All 6 hook addresses extracted from `build/legacy/nuttx.elf` and populated
- QSPI DMA bypass uses corrected struct field offsets: `buflen@+4`, `addr@+8`, `buffer@+16`
- **Why:** The `qspi_meminfo_s` struct layout was previously wrong in the placeholder (offsets from the new OS build which had different packing). Correct offsets confirmed by examining the struct definition in the legacy NuttX source.

### Untracked: tools/patch-user-space-ld.py — Linker script compatibility patcher

- Strips `EXCLUDE_FILE(...)` wrappers from section patterns
- Removes `*lib*.a:*` archive:member glob patterns
- Moves `CONSTRUCTORS` keyword outside `*()` globs
- Converts `//` comments to `/* */`
- **Why:** The legacy `user-space.ld` contains linker script syntax that requires binutils 2.38+ (specifically `EXCLUDE_FILE` inside output section descriptions and archive:member matching). The ARM GCC 10.3 toolchain ships GNU ld 2.35, which doesn't support these features. This script patches the linker script for compatibility. GNU ld also only supports C-style `/* */` comments, not C++ `//` comments that appeared in some sections.
