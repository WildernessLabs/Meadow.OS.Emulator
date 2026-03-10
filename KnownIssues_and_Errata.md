# Known Issues and Errata

Consolidated list of all known Renode bugs, emulation limitations, and firmware
workarounds for the Meadow.OS Emulator. Each item includes severity, current
status, and workaround details.

Last updated: 2026-03-10

---

## Table of Contents

1. [Renode Core Bugs](#1-renode-core-bugs)
2. [Renode Peripheral Gaps](#2-renode-peripheral-gaps)
3. [ESPCP / ESP32 Coprocessor](#3-espcp--esp32-coprocessor)
4. [Flash and Storage](#4-flash-and-storage)
5. [Firmware Architectural Constraints](#5-firmware-architectural-constraints)
6. [Build and Toolchain](#6-build-and-toolchain)
7. [Operational / Environment](#7-operational--environment)

---

## 1. Renode Core Bugs

### 1.1 SVC-with-PRIMASK Silently Skipped
- **Severity:** Critical
- **Status:** FIXED (via hooks) | Upstream: `renode-upstream` U.2
- **Component:** Cortex-M7 CPU model
- **Description:** Renode v1.16.1 silently skips `SVC` instruction execution when
  `PRIMASK=1`. Per ARMv7-M spec, this should escalate to HardFault (synchronous,
  precise). This breaks any RTOS that uses PRIMASK for critical sections around
  SVC calls (NuttX, FreeRTOS).
- **Impact:** NuttX `arm_fullcontextrestore()` crashes — `enter_critical_section()`
  sets PRIMASK=1, SVC is skipped, execution falls through to `PANIC()`.
- **Workaround:** IronPython hooks at all SVC instruction addresses that clear
  PRIMASK before execution:
  ```
  set svc_fix "from Antmicro.Renode.Peripherals.CPU import RegisterValue; cpu.PRIMASK = RegisterValue.Create(0, 32)"
  ```
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
- **Status:** FIXED (via hook) | Upstream: `renode-upstream` U.6
- **Component:** STM32H7_QuadSPI + DMA
- **Description:** QUADSPI controller doesn't drive DMA request lines. DMA
  transfers to/from flash never start.
- **Impact:** LittleFS mount fails with ETIMEDOUT during flash I/O.
- **Workaround:** Hook at `qspi_memory` entry that reads `struct qspi_meminfo_s`
  from R1 and copies directly between flash backing memory (0x90000000) and the
  provided buffer via `SystemBus.ReadBytes`/`WriteBytes`.
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
- **Status:** FIXED (via hook)
- **Component:** STM32SPI
- **Description:** Renode's `STM32SPI` controller doesn't support DMA
  transactions (TXDMAEN unhandled). SPI DMA transfers never complete.
- **Impact:** ESPCP driver's `espcp_send_data_over_spi()` hangs waiting for
  DMA completion.
- **Workaround:** Hook at `espcp_send_data_over_spi` entry (0x08147c94 legacy)
  bypasses SPI controller entirely — reads TX buffer from memory, feeds bytes
  through `EspcpStub.Transmit()`, writes RX bytes back to memory.

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

### 3.1 ESPCP Control Signals Use UART, Not GPIO EXTI
- **Severity:** Medium
- **Status:** DEFERRED — mq_inject hook removed (causes crash); ESPCP thread blocks harmlessly
- **Component:** ESPCP driver, USART3
- **Description:** On real hardware, the ESP32 sends control signal lines via
  USART3 (`/dev/ttyS2`): `+++MW` (message waiting), `+++SIR` (SPI ready),
  `+++RDY` (ready), `+++RST` (reset). The user-space
  `hcom_esp32_network_monitor_thread` reads UART and dispatches to handlers.
  GPIO pins (PI0, PD2, PI10) exist as hardware signals but are NOT
  interrupt-driven via EXTI.
- **Note:** `mint_config_interrupt` is for managed code GPIO interrupts
  (STM32→Mono/.NET), NOT for ESPCP (confirmed by Wilderness Labs engineer).
- **Impact:** Without UART signaling emulation, the ESPCP thread blocks on
  `mq_receive` waiting for a message that never arrives. WiFi/BT/network
  operations are unavailable. HCOM device info works fine without it.
- **Previous approach (removed):** Hook at `mq_receive` (0x0814de46 legacy)
  injected a fake "transport/send_response" message. This caused a Usage Fault
  (UNDEFINSTR at PC 0x0814de5f) in EspcpMainThread. Root cause: NOT the FPU
  frame mismatch (legacy build uses 32-byte standard frames). Likely a stack
  or register corruption issue within `espcp_get_message` when driven by the
  injected message. The crash is reproducible and occurs regardless of which
  throttle/re-queue strategy is used.
- **Current state:** ESPCP init hooks remain (mono bypass, force init, SPI
  bypass, SPI lock skip). The thread starts, blocks on mq_receive harmlessly,
  and doesn't consume CPU. HCOM works.
- **Future fix options:**
  1. Emulate ESP32 UART output on virtual USART3 (`+++MW\n`) so the firmware's
     native monitor thread triggers naturally (avoids mq_receive injection entirely)
  2. Debug the mq_inject crash root cause (PC 0x0814de5f is between instruction
     boundaries — stack corruption or misaligned return address)

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

### 3.5 ESPCP Continuous Polling CPU Burn (Resolved)
- **Severity:** N/A (no longer applicable)
- **Status:** RESOLVED — mq_inject removed, thread blocks on mq_receive
- **Component:** ESPCP thread
- **Description:** When the mq_inject hook was active, the ESPCP thread
  continuously re-queued `send_response` messages via `espcp_add_message_to_queue`
  (0x0814dfec), creating a tight polling loop that starved HCOM.
- **Resolution:** With mq_inject removed, the thread blocks on `mq_receive`
  and consumes zero CPU. Will need to be revisited if/when full ESPCP SPI
  handshake emulation is re-enabled.

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
- **Component:** All IronPython hooks
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
- **Status:** KNOWN
- **Component:** IronPython hooks
- **Description:** `cpu.GetRegisterUnsafe(N)` generates a deprecation warning
  pointing to `GetRegister`. All hooks still function correctly.
- **Workaround:** Migrate to `GetRegister` API when convenient. Named properties
  (`cpu.PC`, `cpu.LR`, `cpu.SP`, `cpu.PRIMASK`) work without warnings.

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
