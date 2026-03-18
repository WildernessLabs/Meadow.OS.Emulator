# Known Issues and Errata

Consolidated list of all known Renode bugs, emulation limitations, and firmware
workarounds for the Meadow.OS Emulator. Each item includes severity, current
status, and workaround details.

Last updated: 2026-03-17

---

## Table of Contents

1. [Renode Core Bugs](#1-renode-core-bugs)
2. [Renode Peripheral Gaps](#2-renode-peripheral-gaps)
3. [ESPCP / ESP32 Coprocessor](#3-espcp--esp32-coprocessor)
4. [Flash and Storage](#4-flash-and-storage)
5. [Firmware Architectural Constraints](#5-firmware-architectural-constraints)
6. [Build and Toolchain](#6-build-and-toolchain)
7. [Operational / Environment](#7-operational--environment)
8. [Performance](#8-performance)

---

## 1. Renode Core Bugs

### 1.1 SVC-with-PRIMASK Silently Skipped
- **Severity:** Critical
- **Status:** FIXED (via C# hook `SvcPrimaskFix.cs`) | Upstream: `renode-upstream` U.2
- **Component:** Cortex-M7 CPU model
- **Description:** Renode v1.16.1 silently skips `SVC` instruction execution when
  `PRIMASK=1`. Per ARMv7-M spec, this should escalate to HardFault (synchronous,
  precise). This breaks any RTOS that uses PRIMASK for critical sections around
  SVC calls (NuttX, FreeRTOS).
- **Impact:** NuttX `arm_fullcontextrestore()` crashes — `enter_critical_section()`
  sets PRIMASK=1, SVC is skipped, execution falls through to `PANIC()`.
- **Workaround:** Compiled C# hook (`peripherals/SvcPrimaskFix.cs`) clears PRIMASK
  before all SVC instruction addresses. Originally IronPython but migrated to C#
  for performance — SVC fires thousands of times per second during Mono JIT.
  Legacy addresses: 0x08162f72, 0x081631be, 0x081631ce, 0x081631d8, 0x081631e4, 0x0817b148

### 1.2 FPU Exception Frame Mismatch
- **Severity:** Low (legacy build unaffected; new OS build has runtime workaround)
- **Status:** IDENTIFIED | Upstream: `renode-upstream` U.4
- **Component:** Cortex-M7 exception entry
- **Description:** Renode exception entry always uses standard frames (32 bytes,
  `EXC_RETURN` bit4=1) even when FPU is active (`CONTROL.FPCA=1`). Should use
  extended frames (104 bytes, bit4=0) per ARMv7-M spec.
- **Impact (new OS only):** NuttX's `arm_exception.S` adds 104 for extended frame
  (`HW_XCPT_SIZE`), causing saved `REG_R13` to be off by +72 bytes. Doesn't cause
  crashes because MSP restore uses the hardware frame.
- **Legacy build:** Unaffected — legacy `exception_common` uses `add.w r2, r2, #32`
  (standard frame), matching Renode's behavior.
- **Workaround:** `scripts/pydev/fix_fpu_frame.py` corrects REG_R13 at runtime for
  the new OS build. Hook address: 0x080009B6 (right before `bl arm_doirq`).
- **Proper fix:** Requires modifying Renode's native tlib library (C code) to push
  extended frames when CONTROL.FPCA=1. Estimated 2-4 days effort. Changes the
  exception entry hot path — needs thorough testing across Cortex-M variants.

### 1.3 SYSRESETREQ Not Handled
- **Severity:** Medium
- **Status:** IDENTIFIED | No upstream item yet
- **Component:** Cortex-M7 reset handling
- **Description:** `NVIC_SystemReset()` (writing SYSRESETREQ to AIRCFG) causes
  the CPU to halt with PC=0, SP=0 instead of actually resetting the system.
- **Impact:** HCOM commands that trigger a system reset (e.g., `device config wifi`)
  cause the emulator to hang.
- **Workaround:** Avoid issuing HCOM commands that trigger SYSRESETREQ during emulation.

### 1.4 Xtensa WAITI Instruction Was a No-Op
- **Severity:** High
- **Status:** FIXED (in WildernessLabs Renode fork + C# hook fallback)
- **Component:** Xtensa CPU model (tlib `exc_helper.c`)
- **Description:** Renode's Xtensa tlib implemented `WAITI` as a no-op with the
  comment "Don't halt — without timer interrupts, EXCP_HLT pauses the machine
  forever." This was incorrect for configurations where the CCOMPARE0 timer works
  (which it does for ESP32 after Phase 2 fix). Without halt, the ESP32 idle task
  burned 100% host CPU spinning through `esp_pm_impl_waiti` → WAITI → no-op loop.
- **Impact:** ESP32 consumed ~90% of host CPU in steady state despite having no
  real work. This was the primary bottleneck limiting dual-machine performance.
- **Fix (fork):** Modified `HELPER(waiti)` in `exc_helper.c` to set
  `env->wfi = 1` and `env->exception_index = EXCP_HLT`, same pattern as ARM WFI.
  The CCOMPARE0 timer interrupt (100Hz FreeRTOS tick) wakes the CPU every 10ms.
- **Fix (installed Renode):** C# hook `InstallIdleSkip` in
  `peripherals/XtensaHotPathHooks.cs` sets `cpu.IsHalted = true` when the ESP32
  PC reaches the WAITI instruction address (0x401aef8b). Works without rebuilding
  Renode.
- **Result:** ESP32 instructions dropped from billions to 143K in steady state
  (99.99% reduction).

---

## 2. Renode Peripheral Gaps

### 2.1 RCC Clock Ready Bits Not Auto-Set
- **Severity:** High
- **Status:** FIXED (via Python peripheral) | Upstream: `renode-upstream` U.3
- **Component:** STM32F4_RCC
- **Description:** Built-in `STM32F4_RCC` does not auto-set clock ready bits
  (HSIRDY, HSERDY, PLLRDY, PLLI2SRDY, PLLSAIRDY, LSIRDY, LSERDY) when the
  corresponding enable bits are written. Also missing: SW→SWS mirroring in
  RCC_CFGR.
- **Impact:** Any firmware that enables a clock and polls the ready bit hangs
  forever. NuttX `pwrmgmt_lsi_init_timer_5_for_measuring()` polls LSIRDY
  indefinitely.
- **Workaround:** Custom Python peripheral `scripts/pydev/rcc.py` that auto-sets
  ready bits on enable writes and mirrors SW→SWS.

### 2.2 QUADSPI Transfer Complete Flag (TCF) Never Set
- **Severity:** High
- **Status:** FIXED (via hook) | Upstream: `renode-upstream` U.6
- **Component:** STM32H7_QuadSPI
- **Description:** Renode's `STM32H7_QuadSPI` never sets TCF (bit 1) in the
  Status Register after indirect-mode transactions. The peripheral is a minimal
  register stub with no actual transaction logic.
- **Impact:** Firmware polling `qspi_waitstatusflags()` hangs (800ms timeout then
  ETIMEDOUT).
- **Workaround:** Skip `qspi_waitstatusflags()` entirely:
  ```
  set qspi_wait_skip "cpu.PC = cpu.LR"
  ```
  Legacy addresses: 0x0816135e, 0x08161270

### 2.3 QUADSPI DMA Requests Not Generated
- **Severity:** High
- **Status:** FIXED (via C# hook `QspiDmaBypass.cs`)
- **Component:** STM32H7_QuadSPI + DMA
- **Description:** QUADSPI controller doesn't drive DMA request lines. DMA
  transfers to/from flash never start.
- **Impact:** LittleFS mount fails with ETIMEDOUT during flash I/O.
- **Workaround:** Compiled C# hook (`peripherals/QspiDmaBypass.cs`) at
  `qspi_memory` entry reads `struct qspi_meminfo_s` from R1 and copies directly
  between flash backing memory (0x90000000) and the provided buffer via
  `SystemBus.ReadBytes`/`WriteBytes`. Originally IronPython, migrated to C# for
  performance (22K calls during assembly load).
  ```
  struct qspi_meminfo_s layout:
    +0  flags    (u8)   — QSPIMEM_WRITE = 0x04
    +4  buflen   (u16)
    +8  addr     (u32)  — flash offset
    +16 buffer   (ptr)  — RAM buffer
  ```
  Legacy address: 0x0816136a

### 2.4 STM32SPI DMA Not Supported
- **Severity:** High (for ESPCP)
- **Status:** FIXED (via C# hook `EspcpSpiBypass.cs`)
- **Component:** STM32SPI
- **Description:** Renode's `STM32SPI` controller doesn't support DMA
  transactions (TXDMAEN unhandled). SPI DMA transfers never complete.
- **Impact:** ESPCP driver's `espcp_send_data_over_spi()` hangs waiting for
  DMA completion.
- **Workaround:** Compiled C# hook (`peripherals/EspcpSpiBypass.cs`) at
  `espcp_send_data_over_spi` entry bypasses SPI controller entirely — reads TX
  buffer from memory, feeds bytes through `SpiBridge.Transmit()`, writes RX
  bytes back to memory. Originally IronPython, migrated to C# for performance
  (15K+ calls/30s, each generating 3x GetRegisterUnsafe deprecation warnings).
  Legacy address: 0x08147c94. Dual-machine address: 0x081488A0.

### 2.5 QUADSPI Register Bits Unhandled
- **Severity:** Low (cosmetic warnings)
- **Status:** KNOWN | No fix needed
- **Component:** STM32H7_QuadSPI
- **Description:** Firmware writes to QUADSPI configuration registers (prescaler,
  clock mode) with bits that Renode's model doesn't implement. Generates warnings:
  ```
  quadspi: Unhandled write to offset 0x0. Unhandled bits: [24]
  quadspi: Unhandled write to offset 0x4. Unhandled bits: [0]
  ```
- **Impact:** None — our DMA bypass hook handles all real QSPI operations.

### 2.6 Timer 5 Input Capture Not Emulated
- **Severity:** Medium
- **Status:** FIXED (via hook)
- **Component:** Timer 5 (TIM5)
- **Description:** NuttX's `pwrmgmt_lsi_calc_prep_thread_func()` configures
  Timer 5 input capture to measure LSI frequency. Renode doesn't emulate TIM5
  input capture, so the thread spins indefinitely.
- **Impact:** CPU burn during boot (not a hang, but wastes emulation cycles).
- **Workaround:** Hook at thread entry that returns immediately:
  ```
  set lsi_skip "cpu.PC = cpu.LR"
  ```
  Legacy address: 0x0814d0d8

### 2.7 USB OTG FS Stub Only
- **Severity:** Low
- **Status:** ACCEPTABLE
- **Component:** USB OTG FS
- **Description:** `scripts/pydev/otg_fs.py` provides a minimal stub that returns
  safe defaults (AHBIDL, GRSTCTL, GINTSTS) to prevent access faults. No real
  USB functionality.
- **Impact:** USB device functionality unavailable. HCOM redirected to UART4/TCP
  instead of USB CDC-ACM (intentional design choice for emulation).

---

## 3. ESPCP / ESP32 Coprocessor

### 3.1 ESPCP Dual-Machine SPI Communication
- **Severity:** Medium
- **Status:** WORKING (dual-machine mode via SpiBridge)
- **Component:** ESPCP driver, SPI2, SpiBridge
- **Description:** In single-machine mode, ESPCP uses the `EspcpStub` C# peripheral
  to simulate ESP32 SPI responses. In dual-machine mode (STM32 + ESP32), the real
  ESP32 firmware runs on an emulated Xtensa CPU. SPI communication is bridged via
  `SpiBridge.cs` which connects the STM32's SPI2 to the ESP32's SPI slave.
- **Control signals:** On real hardware, ESP32 sends UART signals (`+++MW`, `+++SIR`,
  `+++RDY`). In dual-machine mode, GPIO pins (PI0=MessageWaiting, PD2=SPI Ready,
  PI10=Ready) are connected directly between machines.
- **MqReceiveIntercept:** C# hook (`peripherals/MqReceiveIntercept.cs`) at
  `mq_receive` (0x08157130) continuously injects `send_response` messages into the
  ESPCP thread's message queue, driving SPI polling. See §8.1 for performance impact.
- **Note:** `mint_config_interrupt` is for managed code GPIO interrupts
  (STM32→Mono/.NET), NOT for ESPCP (confirmed by Wilderness Labs engineer).

### 3.2 ESPCP SPI Lock Deadlock
- **Severity:** High
- **Status:** FIXED (via hook)
- **Component:** ESPCP SPI semaphore
- **Description:** `espcp_spi_interface_lock` semaphore initializes to 0. First
  `sem_wait()` deadlocks because we bypass real SPI (semaphore sequence never
  established).
- **Workaround:** Hooks at lock (0x08147d0c) and unlock (0x08147d18) that skip
  the semaphore entirely.

### 3.3 ESPCP Init Gated on Network Config
- **Severity:** Medium
- **Status:** FIXED (via hook)
- **Component:** HCOM configuration
- **Description:** `hcom_nx_setup_mgr` gates `espcp_late_init` on config[8]
  (selected network type). If 0, ESPCP never initializes.
- **Workaround:** Hook at 0x08141144 forces R5=1 (WiFi) so ESPCP always inits.

### 3.4 Mono Runtime Check Gates ESP32 Operations
- **Severity:** Medium
- **Status:** FIXED (via hook)
- **Component:** HCOM runtime state
- **Description:** Firmware gates ESP32 operations behind a Mono runtime state
  check. Since Mono isn't flashed in emulation, this blocks ESPCP HCOM commands.
- **Workaround:** Hook at 0x08057bee returns 0 (not running) to bypass the check.

---

## 4. Flash and Storage

### 4.1 GenericSpiFlash Winbond Register Mismatch
- **Severity:** Medium
- **Status:** KNOWN | Upstream: `renode-upstream` U.7
- **Component:** GenericSpiFlash / Winbond W25QXXXJV
- **Description:** Renode's `GenericSpiFlash` models Micron/Numonyx register
  architecture. Winbond W25Q chips use a different 3-register model:

  | Command | GenericSpiFlash interpretation | Winbond W25Q meaning |
  |---------|------------------------------|---------------------|
  | 0x35    | EnterQuadInputOutputMode (unimpl.) | Read Status Register 2 (QE bit) |
  | 0x31    | Unrecognized (error) | Write Status Register 2 |
  | 0x15    | ReadConfigurationRegister (Micron) | Read Status Register 3 |
  | 0x11    | Not mapped | Write Status Register 3 |

- **Impact:** Firmware quad-mode configuration, write protection checks, and
  security register reads return wrong data or errors. Currently masked by
  QSPI DMA bypass hooks.
- **Errors seen in log:**
  ```
  quadspi.w25q: Command decoding failed on byte: 0x35 (EnterQuadInputOutputMode)
  quadspi.w25q: Command decoding failed on byte: 0x31 (49)
  ```
- **Future fix:** Override command handling in `Winbond_W25QXXXJV.cs` to
  implement Winbond's 3-register model with correct command mappings.

### 4.2 Flash Reset Commands Unhandled
- **Severity:** Low
- **Status:** KNOWN
- **Component:** GenericSpiFlash
- **Description:** `ResetEnable` (0x66) + `ResetMemory` (0x99) are logged as
  "unhandled parameterless command." Firmware sends these during QSPI init.
- **Impact:** Minimal — flash starts in a clean state in emulation anyway.

### 4.3 Flash Deep Power-Down Unhandled
- **Severity:** Low
- **Status:** KNOWN
- **Component:** GenericSpiFlash
- **Description:** `EnterDeepPowerDown` (0xB9) and `ReleaseFromDeepPowerdown`
  (0xAB) are unimplemented. Power state not tracked.
- **Impact:** Only matters if testing power management sleep/wake paths.

### 4.4 No Flash Persistence Across Reboots
- **Severity:** Medium
- **Status:** PLANNED
- **Component:** QSPI flash backing memory
- **Description:** The 64MB QSPI flash uses volatile `MappedMemory`. All
  filesystem changes are lost when Renode exits.
- **Impact:** Cannot test firmware update → reboot → boot-from-new-image flows,
  or configuration persistence. LittleFS auto-formats on every boot.
- **Future fix:** Use Renode's file-backed `MappedMemory` for persistence.

### 4.5 No Erase-Before-Write Enforcement
- **Severity:** Low
- **Status:** KNOWN
- **Component:** QSPI DMA bypass hook
- **Description:** Our DMA bypass hook copies bytes directly via `SystemBus`,
  bypassing flash erase semantics. Real NOR flash requires sector erase (set to
  0xFF) before programming. The bypass allows arbitrary byte overwrites.
- **Impact:** Won't catch firmware bugs that write without erasing first. Wear
  leveling and power-loss recovery testing is not faithful.

---

## 5. Firmware Architectural Constraints

### 5.1 GPIO Board Version Detection
- **Severity:** Medium
- **Status:** FIXED (via hook)
- **Component:** GPIO (board version pins)
- **Description:** Board version detected by reading 4 GPIO pins. In Renode,
  unconnected GPIO pins with pull-ups all read high → version UNKNOWN → boot
  stalls at MTD init.
- **Workaround:** Hook at `meadow_hw_version_find_device_ver` writes initialized=1
  and version=3 (CCMv2) to the version struct. Legacy address: 0x08140d64.

### 5.2 Mono Runtime Not Flashed
- **Severity:** Low
- **Status:** EXPECTED
- **Component:** Mono/.NET runtime
- **Description:** Emulator doesn't include Mono runtime in QSPI flash. Boot
  logs "Mono runtime was not found" and continues. Managed code features are
  unavailable.
- **Impact:** Expected and acceptable for OS-level emulation.

### 5.3 Hook Addresses Are Binary-Specific
- **Severity:** Medium
- **Status:** KNOWN | Future automation
- **Component:** All hooks (IronPython and C#)
- **Description:** All hook addresses (SVC, GPIO version, QSPI, ESPCP, etc.)
  are hardcoded in `.resc` scripts. When the NuttX kernel is rebuilt, all
  addresses must be re-extracted:
  ```bash
  arm-none-eabi-nm build/legacy/nuttx.elf | grep <symbol_name>
  arm-none-eabi-objdump -d build/legacy/nuttx.elf | grep 'svc\s'
  ```
- **Impact:** Boot will fail or hang silently if hooks fire at wrong addresses
  after a kernel rebuild.
- **Future fix:** Build script that auto-extracts addresses and generates `.resc`.

---

## 6. Build and Toolchain

### 6.1 ARM GCC 10.3 Required for Legacy Build
- **Severity:** Medium
- **Status:** DOCUMENTED
- **Description:** Legacy NuttX requires ARM GCC ~10.x. Newer toolchains from
  Homebrew are incompatible. Build script adds `/Applications/ARM/bin` to PATH.

### 6.2 Legacy Linker Script Requires Patching
- **Severity:** Medium
- **Status:** HANDLED
- **Description:** Legacy `user-space.ld` uses binutils 2.38+ syntax
  (EXCLUDE_FILE, archive:member, C++ comments) incompatible with GNU ld 2.35 in
  ARM GCC 10.3. Patched by `tools/patch-user-space-ld.py`.

### 6.3 Legacy Split Binary Loading
- **Severity:** Low
- **Status:** HANDLED
- **Description:** Legacy NuttX places vectors at 0x08040000 and kernel at
  0x08140000 (1MB gap). A flat `nuttx.bin` zero-fills the gap, overwriting user
  code. Build script generates split binaries (vectors, kernel, user) loaded at
  correct addresses:
  ```
  sysbus LoadBinary @build/legacy/nuttx_user.bin 0x08040000
  sysbus LoadBinary @build/legacy/nuttx_vectors.bin 0x08040000
  sysbus LoadBinary @build/legacy/nuttx_kernel.bin 0x08140000
  ```

---

## 7. Operational / Environment

### 7.1 Renode.exe Deleted by macOS
- **Severity:** Medium
- **Status:** RECURRING
- **Description:** `Renode.exe` at
  `/Applications/Renode.app/Contents/MacOS/bin/Renode.exe` gets periodically
  deleted, likely by macOS Gatekeeper/XProtect.
- **Workaround:** Re-copy from mounted DMG:
  ```bash
  cp "/Volumes/Renode_1.16.1+20260228git70fcac863/Renode.app/Contents/MacOS/bin/Renode.exe" \
     "/Applications/Renode.app/Contents/MacOS/bin/Renode.exe"
  ```

### 7.2 GetRegisterUnsafe API Deprecated
- **Severity:** Low
- **Status:** MOSTLY MITIGATED
- **Component:** IronPython hooks
- **Description:** `cpu.GetRegisterUnsafe(N)` generates a deprecation warning
  pointing to `GetRegister`. All hooks still function correctly.
- **Mitigation:** All hot-path hooks have been migrated from IronPython to compiled
  C# (SvcPrimaskFix, QspiDmaBypass, MqReceiveIntercept, EspcpSpiBypass,
  XtensaHotPathHooks). Only boot-time and rare IronPython hooks remain, where the
  deprecation warnings have negligible performance impact.
- **Note:** Named properties (`cpu.PC`, `cpu.LR`, `cpu.SP`, `cpu.PRIMASK`) work
  without warnings in both IronPython and C#.

### 7.3 IronPython Type Constraints
- **Severity:** Low
- **Status:** DOCUMENTED
- **Description:** Several IronPython/C# interop patterns don't work as expected:
  - `cpu.PC = 0x1234` fails — must use `RegisterValue.Create(0x1234, 32)`
  - `cpu.SP + 4` fails — must use `cpu.GetRegisterUnsafe(13).RawValue`
  - `Array[Byte](length)` fails — must use `Array.CreateInstance(Byte, length)`
- **Workaround:** Documented patterns in MEMORY.md IronPython tips section.

### 7.4 IronPython Conditional Register Assignment Crash
- **Severity:** Medium
- **Status:** DOCUMENTED
- **Description:** The pattern `cpu.PC = X if condition else None` crashes with
  "expected RegisterValue, got NoneType" when the condition is False. Python
  evaluates the ternary expression first, then assigns the result — so `None`
  gets assigned to `cpu.PC`.
- **Impact:** Cannot use ternary expressions for conditional register writes in
  IronPython hooks. This was a key blocker for the ESPCP mq_inject/throttle hooks.
- **Workaround:** Use list comprehensions for conditional register writes:
  ```python
  # WRONG — crashes when cond is False:
  cpu.PC = RegisterValue.Create(addr, 32) if cond else None

  # RIGHT — list comprehension executes body only when cond is True:
  [cpu.SetRegisterUnsafe(15, RegisterValue.Create(addr, 32)) for _ in [1] if cond]
  ```
- **Note:** `cpu.LR` can also return `None` in certain hook contexts, so avoid
  patterns like `cpu.SetRegisterUnsafe(15, cpu.LR) if flag else None`. Instead,
  read LR explicitly: `lr = cpu.GetRegisterUnsafe(14).RawValue`.

---

## 8. Performance

### 8.1 ESPCP Polling Overhead in Dual-Machine Mode
- **Severity:** High
- **Status:** FIXED (via usleep-based throttling)
- **Component:** MqReceiveIntercept, EspcpSpiBypass, SpiBridge
- **Description:** In dual-machine mode, `MqReceiveIntercept` was continuously
  injecting `send_response` messages into the ESPCP thread's message queue,
  driving ~3,300 SPI polls per second. Each poll triggered 32
  `SpiBridge.Transmit()` calls — totaling ~75K managed method calls per second
  when the ESP32 had nothing to send. This starved HCOM, causing Meadow.CLI
  timeouts.
- **Root cause:** The espcp_thread loop (mq_receive → process → loop) ran at
  full instruction speed. Simple counter-based throttling (returning R0=-1 for
  skipped calls) still caused ~16.7M hook invocations/sec as the thread spun
  through 6-instruction loop iterations without blocking.
- **Fix:** `MqReceiveIntercept` now redirects skipped poll calls to
  `usleep(10ms)` instead of returning an error. The thread blocks in the NuttX
  kernel, yielding CPU time to HCOM. Every 100th call (`pollDivisor=100`)
  injects a real message for SPI polling.
  ```
  cpu InstallMqIntercept <mq_receive> <espcpLR> 0x2007C010 0x2007FFF0 100 <usleep> <loopTop> 10000
  ```
- **Result:** SPI polls dropped from ~3,300/sec to ~8/sec (400x reduction).
  HCOM responds instantly. Both `device info` and `device name` verified via
  hcom_test.py.

### 8.2 IronPython Hook Overhead
- **Severity:** High
- **Status:** RESOLVED (via migration to C#)
- **Component:** IronPython scripting engine
- **Description:** IronPython hooks are 10-100x slower than compiled C# hooks.
  The ESP32 initially had ~46 IronPython hooks, many on hot paths (every SPI
  transaction, context switch, lock operation, list insertion). This dragged
  dual-machine performance to ~0.001x real-time.
- **Resolution:** All hot-path hooks migrated to compiled C# across four phases:
  - Phase 1: Removed 35 diagnostic trace hooks (dead code)
  - Phase 2: Removed `_idle_tick_direct` hack (native CCOMPARE0 timer works)
  - Phase 3: Migrated 8 functional guards to `XtensaHotPathHooks.cs`
  - Phase 4: Migrated `espcp_spi_bypass` to `EspcpSpiBypass.cs`, added idle halt
  - Only 3 IronPython hooks remain: `ets_printf` + 2 exception traces (all rare)
- **Result:** 570x speedup (0.001x → 0.57x). See [ESP_Emu_Perf.md](ESP_Emu_Perf.md).

### 8.3 Renode Quantum and Block Size Tuning
- **Severity:** Medium
- **Status:** TUNED
- **Component:** Renode time domain configuration
- **Description:** Renode's default settings (100μs quantum, 2047 MaximumBlockSize)
  impose high synchronization and translation overhead for dual-machine emulation.
- **Tuning applied in `meadow-dual.resc`:**
  ```
  emulation SetGlobalQuantum "00:00:00.010000"   # 10ms (was 100μs default)
  cpu MaximumBlockSize 10000                       # (was 2047 default)
  cpu ChainingEnabled true                         # TB chaining for longer runs
  ```
- **Impact:** Quantum increase from 100μs → 10ms reduces sync boundary overhead
  by ~100x. Larger block size improves translation block caching. Together these
  contributed to the Phase 4a improvement (0.28x → 0.45x).

### 8.4 SpiBridge Log Spam
- **Severity:** Medium
- **Status:** FIXED
- **Component:** SpiBridge.cs logging
- **Description:** `SpiBridge` logged every TX frame at `Warning` level, generating
  52K+ log lines per minute. Each log call involved string formatting with 6
  parameters. Renode's logging infrastructure serializes log calls even when
  the output is suppressed by log level filters.
- **Fix:** Downgraded TX frame logging from `LogLevel.Warning` to `LogLevel.Debug`.
  Reduced log output from ~53K lines/min to ~170 lines/min.

### 8.5 Fork Renode (.NET 8) Slower Than Installed (Mono)
- **Severity:** Low
- **Status:** IDENTIFIED | Not investigated
- **Component:** Renode runtime
- **Description:** The WildernessLabs Renode fork (built as .NET 8 native arm64
  binary) shows ~3.2x host load vs ~2.2x for the installed Mono-based Renode
  v1.16.1, for the same dual-machine workload. The .NET 8 build should
  theoretically be faster.
- **Possible causes:** Different tlib compilation flags, .NET 8 GC behavior,
  missing AOT optimizations, or runtime overhead differences.
- **Impact:** Using the installed Mono Renode is ~45% faster for now. The fork
  is needed only for tlib fixes (WAITI halt, ENTRY TCG temp allocation) that
  haven't been applied to the installed version.
