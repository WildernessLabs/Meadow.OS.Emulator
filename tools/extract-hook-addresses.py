#!/usr/bin/env python3
"""Extract hook addresses from NuttX ELF files and generate a Renode .resc include.

Usage:
    python3 tools/extract-hook-addresses.py build/legacy/nuttx.elf build/legacy/nuttx_user.elf

Output:
    build/legacy/addresses.resc   — Renode `set` commands for all hook addresses
    build/legacy/sdram-patches.resc — SDRAM string patch commands (Mono /usr/local paths)

The generated files are included by meadow-legacy-*.resc scripts so that hook
addresses and SDRAM patches stay in sync with the firmware build automatically.
"""

import os
import re
import subprocess
import sys
from pathlib import Path


def run(cmd: list[str], **kwargs) -> str:
    result = subprocess.run(cmd, capture_output=True, text=True, **kwargs)
    if result.returncode != 0:
        print(f"ERROR: {' '.join(cmd)}\n{result.stderr}", file=sys.stderr)
        sys.exit(1)
    return result.stdout


def nm_symbols(elf: str) -> dict[str, int]:
    """Return {symbol_name: address} from nm output."""
    out = run(["arm-none-eabi-nm", elf])
    syms = {}
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 3:
            syms[parts[2]] = int(parts[0], 16)
    return syms


def objdump_function(elf: str, start: int, max_bytes: int = 0x400) -> list[tuple[int, str]]:
    """Disassemble from `start` for `max_bytes`, return [(addr, line), ...]."""
    out = run([
        "arm-none-eabi-objdump", "-d",
        f"--start-address=0x{start:08x}",
        f"--stop-address=0x{start + max_bytes:08x}",
        elf,
    ])
    insns = []
    for line in out.splitlines():
        m = re.match(r'\s+([0-9a-f]+):\s+(.*)', line)
        if m:
            insns.append((int(m.group(1), 16), m.group(2)))
    return insns


def find_insn_after(insns: list[tuple[int, str]], pattern: str) -> int | None:
    """Find the instruction AFTER the one matching `pattern` (regex)."""
    for i, (addr, text) in enumerate(insns):
        if re.search(pattern, text) and i + 1 < len(insns):
            return insns[i + 1][0]
    return None


def find_insn_at(insns: list[tuple[int, str]], pattern: str) -> int | None:
    """Find the address of the instruction matching `pattern`."""
    for addr, text in insns:
        if re.search(pattern, text):
            return addr
    return None


def find_bl_target_lr(insns: list[tuple[int, str]], target_name: str) -> int | None:
    """Find LR value (next_addr | 1 for Thumb) after a BL to target_name."""
    for i, (addr, text) in enumerate(insns):
        if re.search(rf'bl\s+[0-9a-f]+\s+<{re.escape(target_name)}>', text):
            if i + 1 < len(insns):
                return insns[i + 1][0] | 1  # Thumb bit
    return None


def read_literal_pool(elf: str, pool_addr: int) -> int:
    """Read a 32-bit word from the literal pool in the ELF."""
    insns = objdump_function(elf, pool_addr, 4)
    for addr, text in insns:
        m = re.search(r'\.word\s+0x([0-9a-f]+)', text)
        if m:
            return int(m.group(1), 16)
    return 0


def find_literal_pool_ref(insns: list[tuple[int, str]], elf: str, reg: str = "r5") -> int | None:
    """Find the address loaded via the first ldr rN, [pc, ...] and follow the literal pool."""
    for addr, text in insns:
        m = re.match(rf'\s*[0-9a-f]+\s+ldr\s+{reg},\s+\[pc,\s+#\d+\]\s+[@;]\s+\(?([0-9a-f]+)[\s<]', text)
        if not m:
            # Try matching just the instruction text (without leading address)
            m = re.search(rf'ldr\s+{reg},\s+\[pc,\s+#\d+\]\s+[@;]\s+\(?([0-9a-f]+)', text)
        if m:
            pool_addr = int(m.group(1), 16)
            return read_literal_pool(elf, pool_addr)
    return None


def require(name: str, value: int | None) -> int:
    if value is None:
        print(f"ERROR: Could not find address for {name}", file=sys.stderr)
        sys.exit(1)
    return value


def main():
    if len(sys.argv) < 3:
        print(f"Usage: {sys.argv[0]} <kernel.elf> <user.elf>", file=sys.stderr)
        sys.exit(1)

    kernel_elf = sys.argv[1]
    user_elf = sys.argv[2]
    out_dir = Path(kernel_elf).parent

    # Read board version from emulator config
    board_version = 2  # default: F7FeatherV2
    config_file = Path(__file__).parent.parent / "emulator.config.yaml"
    if config_file.exists():
        try:
            import yaml
            with open(config_file) as f:
                cfg = yaml.safe_load(f)
            board = cfg.get("board", "F7FeatherV2")
            board_map = {"F7FeatherV1": 1, "F7FeatherV2": 2, "F7CoreComputeV2": 3}
            board_version = board_map.get(board, 2)
        except Exception:
            pass

    print(f"Extracting hook addresses from:")
    print(f"  Kernel: {kernel_elf}")
    print(f"  User:   {user_elf}")

    ksyms = nm_symbols(kernel_elf)
    usyms = nm_symbols(user_elf)

    # -------------------------------------------------------------------------
    # Simple symbol lookups (address = function entry point)
    # -------------------------------------------------------------------------
    simple = {
        # Kernel symbols
        "MEADOW_HW_VERSION_FIND_DEVICE_VER": ("meadow_hw_version_find_device_ver", ksyms),
        "QSPI_WAITSTATUSFLAGS_CONSTPROP": ("qspi_waitstatusflags.constprop.0", ksyms),
        "QSPI_WAITSTATUSFLAGS_PART": ("qspi_waitstatusflags.part.0", ksyms),
        # Meadow.OS only: the bounded waits (issue #753) all poll through this
        "QSPI_WAITSTATUSFLAGS_TIMED": ("qspi_waitstatusflags_timed", ksyms),
        "QSPI_MEMORY": ("qspi_memory", ksyms),
        "HCOM_NX_CONFIG_REFRESH_MONO_VERSION": ("hcom_nx_config_refresh_mono_version", ksyms),
        "HCOM_NX_CONFIG_INIT": ("hcom_nx_config_init", ksyms),
        "HCOM_NX_CONFIG_SET_TIME": ("hcom_nx_config_set_time_to_os_build_time", ksyms),
        "HCOM_NX_SETUP_MGR": ("hcom_nx_setup_mgr", ksyms),
        "ESPCP_SEND_DATA_OVER_SPI": ("espcp_send_data_over_spi", ksyms),
        "ESPCP_SPI_INTERFACE_LOCK": ("espcp_spi_interface_lock", ksyms),
        "ESPCP_SPI_INTERFACE_UNLOCK": ("espcp_spi_interface_unlock", ksyms),
        "PWRMGMT_LSI_CALC_PREP_THREAD_FUNC": ("pwrmgmt_lsi_calc_prep_thread_func", ksyms),
        "ESPCP_THREAD": ("espcp_thread", ksyms),
        "MQ_RECEIVE": ("mq_receive", ksyms),
        "USLEEP": ("usleep", ksyms),
        "LFS_FORMAT": ("lfs_format", ksyms),
        # User symbols
        "HCOM_VIA_NX_COPY_MONO_RUNTIME_TO_RAM": ("hcom_via_nx_copy_mono_runtime_to_ram", usyms),
        "HCOM_LOGGING_SYSLOG": ("hcom_logging_syslog", usyms),
        "HCOM_HOST_WATCHDOG_TIMEOUT_EXPIRED": ("hcom_host_watchdog_timeout_expired", usyms),
        "HCOM_MONO_CTRL_START_MONO_MAIN": ("hcom_mono_ctrl_start_mono_main", usyms),
        "HCOM_MONO_CTRL_IS_MONO_ENABLED": ("hcom_mono_ctrl_is_mono_enabled", usyms),
        "HCOM_HOST_RECV_PTHREAD": ("hcom_host_recv_pthread", usyms),
    }

    # struct qspi_meminfo_s changed layout in NuttX 12 (Meadow.OS); the C# QSPI
    # bypass needs to know which one to parse.  build-meadow.os-emulated.sh
    # passes EMU_OS; anything other than "legacy" is Meadow.OS.
    meminfo_layout = 0 if os.environ.get("EMU_OS", "legacy") == "legacy" else 1

    addrs: dict[str, int] = {"BOARD_VERSION": board_version,
                             "QSPI_MEMINFO_LAYOUT": meminfo_layout}
    missing = []
    for var_name, (sym_name, sym_table) in simple.items():
        addr = sym_table.get(sym_name)
        if addr is None:
            missing.append(f"  {var_name} ({sym_name})")
        else:
            addrs[var_name] = addr

    if missing:
        print(f"WARNING: Missing symbols (hooks using these will be skipped):", file=sys.stderr)
        for m in missing:
            print(m, file=sys.stderr)

    # -------------------------------------------------------------------------
    # Data symbol lookups (for comments / struct addresses)
    # -------------------------------------------------------------------------
    data_syms = {
        "F7_SYSLOG_SEM": ("_f7syslogSem", usyms),
        "HCOM_HOST_WDOG_TIMEDOUT": ("_hcom_host_process_wdog_timedout", usyms),
    }
    for var_name, (sym_name, sym_table) in data_syms.items():
        addr = sym_table.get(sym_name)
        if addr is not None:
            addrs[var_name] = addr

    # -------------------------------------------------------------------------
    # hw_version struct address — from literal pool in find_device_ver
    # -------------------------------------------------------------------------
    if "MEADOW_HW_VERSION_FIND_DEVICE_VER" in addrs:
        func_addr = addrs["MEADOW_HW_VERSION_FIND_DEVICE_VER"]
        insns = objdump_function(kernel_elf, func_addr, 0x100)
        struct_addr = find_literal_pool_ref(insns, kernel_elf, "r5")
        if struct_addr:
            addrs["HW_VERSION_STRUCT"] = struct_addr
            addrs["HW_VERSION_INITIALIZED"] = struct_addr  # offset 0: initialized byte
            addrs["HW_VERSION_VALUE"] = struct_addr + 4     # offset 4: version word
            print(f"  hw_version struct at 0x{struct_addr:08X}")
        elif ("_meadowVersionKnown" in ksyms) and ("_meadowVer" in ksyms):
            # Meadow.OS (Rebase) keeps the result in two file-scope statics in
            # meadow_hw_version.c instead of Legacy's struct.
            addrs["HW_VERSION_INITIALIZED"] = ksyms["_meadowVersionKnown"]
            addrs["HW_VERSION_VALUE"] = ksyms["_meadowVer"]
            print(f"  hw_version statics: _meadowVersionKnown at 0x{ksyms['_meadowVersionKnown']:08X}, "
                  f"_meadowVer at 0x{ksyms['_meadowVer']:08X}")
        else:
            print("WARNING: Could not find hw_version struct address", file=sys.stderr)

    # -------------------------------------------------------------------------
    # mono_ver_copy — instruction after "str r0, [r4, #88]" in hcom_nx_config_init
    # This is where get_long_version_string result is stored to config+0x58
    # -------------------------------------------------------------------------
    if "HCOM_NX_CONFIG_INIT" in addrs:
        insns = objdump_function(kernel_elf, addrs["HCOM_NX_CONFIG_INIT"], 0x400)
        addr = find_insn_after(insns, r'str\s+r0,\s+\[r4,\s+#88\]')
        if addr:
            addrs["MONO_VER_COPY_ADDR"] = addr
            print(f"  mono_ver_copy at 0x{addr:08X} (hcom_nx_config_init+0x{addr - addrs['HCOM_NX_CONFIG_INIT']:x})")
        else:
            print("WARNING: Could not find mono_ver_copy offset in hcom_nx_config_init", file=sys.stderr)

    # -------------------------------------------------------------------------
    # espcp_force_init — "ands.w r0, r5, #255" in hcom_nx_setup_mgr
    # -------------------------------------------------------------------------
    if "HCOM_NX_SETUP_MGR" in addrs:
        insns = objdump_function(kernel_elf, addrs["HCOM_NX_SETUP_MGR"], 0x400)
        addr = find_insn_at(insns, r'ands\.w\s+r0,\s+r5,\s+#255')
        if addr:
            addrs["ESPCP_FORCE_INIT_ADDR"] = addr
            print(f"  espcp_force_init at 0x{addr:08X} (hcom_nx_setup_mgr+0x{addr - addrs['HCOM_NX_SETUP_MGR']:x})")
        else:
            print("WARNING: Could not find espcp_force_init offset in hcom_nx_setup_mgr", file=sys.stderr)

    # -------------------------------------------------------------------------
    # espcp_thread LR — return address after bl mq_receive in espcp_thread
    # -------------------------------------------------------------------------
    if "ESPCP_THREAD" in addrs:
        insns = objdump_function(kernel_elf, addrs["ESPCP_THREAD"], 0x100)
        lr = find_bl_target_lr(insns, "mq_receive")
        if lr:
            addrs["ESPCP_THREAD_LR"] = lr
            print(f"  espcp_thread LR = 0x{lr:08X}")
            # Loop top: the `bne` after `cmp r0, #4` jumps back to the loop top.
            # Pattern: bl mq_receive; cmp r0, #4; bne.n <loop_top>
            # The bne target (with Thumb bit) is the loop top address.
            for i, (iaddr, text) in enumerate(insns):
                if iaddr == (lr & ~1):  # strip Thumb bit for comparison
                    # LR points to `cmp r0, #4`, next insn is `bne <loop_top>`
                    if i + 1 < len(insns):
                        bne_text = insns[i + 1][1]
                        import re as _re
                        m = _re.search(r'bne(?:\.n)?\s+([0-9a-f]+)', bne_text)
                        if m:
                            loop_top = int(m.group(1), 16) | 1  # add Thumb bit
                            addrs["ESPCP_THREAD_LOOP_TOP"] = loop_top
                            print(f"  espcp_thread loop top = 0x{loop_top:08X}")
                    break
        else:
            print("WARNING: Could not find mq_receive call in espcp_thread", file=sys.stderr)

    # -------------------------------------------------------------------------
    # mono_str_fix — first instruction at cbnz target after deep_copy_config call
    # Pattern: bl deep_copy_config → mov rN, r0 → cbnz r0, TARGET → ... TARGET: ldr ...
    #
    # Legacy only.  The hook writes the config struct at Legacy offsets
    # (+0x54, +0x80); Meadow.OS's struct differs, and the same instruction
    # pattern exists there, so the hook overwrote unrelated fields with a
    # pointer to the firmware version string.  Freeing one of those fields
    # later corrupted the kernel heap (seen after `meadow device info`).
    # -------------------------------------------------------------------------
    if meminfo_layout != 0:
        print("  mono_str_fix skipped (Legacy config struct offsets; not valid for Meadow.OS)")
    elif "HCOM_MONO_CTRL_START_MONO_MAIN" in addrs:
        insns = objdump_function(user_elf, addrs["HCOM_MONO_CTRL_START_MONO_MAIN"], 0x500)
        # Find bl to deep_copy_config, then the cbnz after it
        for i, (iaddr, text) in enumerate(insns):
            if re.search(r'bl\s+[0-9a-f]+\s+<.*deep_copy_config', text):
                # Look for cbnz r0 in the next few instructions
                for j in range(i + 1, min(i + 5, len(insns))):
                    m = re.search(r'cbnz\s+r0,\s+([0-9a-f]+)', insns[j][1])
                    if m:
                        target = int(m.group(1), 16)
                        addrs["MONO_STR_FIX_ADDR"] = target
                        offset = target - addrs["HCOM_MONO_CTRL_START_MONO_MAIN"]
                        print(f"  mono_str_fix at 0x{target:08X} (start_mono_main+0x{offset:x})")
                        break
                break

        if "MONO_STR_FIX_ADDR" not in addrs:
            print("WARNING: Could not find mono_str_fix offset in start_mono_main", file=sys.stderr)

    # -------------------------------------------------------------------------
    # asm_scan_bypass — "cmp r4, #0" after scan loop exits (when r5 == 0)
    # Pattern: cbnz r5, ... → cmp r4, #0
    # -------------------------------------------------------------------------
    if "HCOM_MONO_CTRL_START_MONO_MAIN" in addrs:
        insns = objdump_function(user_elf, addrs["HCOM_MONO_CTRL_START_MONO_MAIN"], 0x500)
        # Find the pattern: ldr.w r5, [r7], #4 → cbnz r5, ... → cmp r4, #0
        for i, (iaddr, text) in enumerate(insns):
            if re.search(r'ldr\.w\s+r5,\s+\[r7\],\s+#4', text):
                # Next should be cbnz r5 then cmp r4, #0
                for j in range(i + 1, min(i + 3, len(insns))):
                    if re.search(r'cb(n?)z\s+r5', insns[j][1]):
                        for k in range(j + 1, min(j + 3, len(insns))):
                            if re.search(r'cmp\s+r4,\s+#0', insns[k][1]):
                                addrs["ASM_SCAN_BYPASS_ADDR"] = insns[k][0]
                                offset = insns[k][0] - addrs["HCOM_MONO_CTRL_START_MONO_MAIN"]
                                print(f"  asm_scan_bypass at 0x{insns[k][0]:08X} (start_mono_main+0x{offset:x})")
                                break
                        break
                break

    # -------------------------------------------------------------------------
    # HCOM_RECV_READ — `bl read` call site in hcom_host_recv_pthread
    # Pattern: ldr r1, [r4, #20]; bl read; subs r5, r0, #0
    # Hook at bl, return to subs (instruction after bl)
    # -------------------------------------------------------------------------
    if "HCOM_HOST_RECV_PTHREAD" in addrs:
        insns = objdump_function(user_elf, addrs["HCOM_HOST_RECV_PTHREAD"], 0x200)
        read_addr = find_insn_at(insns, r'bl\s+[0-9a-f]+\s+<read>')
        if read_addr:
            addrs["HCOM_RECV_READ"] = read_addr
            # Return address is the next instruction after the bl
            for i, (iaddr, text) in enumerate(insns):
                if iaddr == read_addr and i + 1 < len(insns):
                    addrs["HCOM_RECV_READ_RETURN"] = insns[i + 1][0]
                    break
            print(f"  hcom_recv_read at 0x{read_addr:08X}")
        else:
            print("WARNING: Could not find read() call in hcom_host_recv_pthread", file=sys.stderr)

    # -------------------------------------------------------------------------
    # SDRAM string patches — find /usr/local/lib and /usr/local/etc in mono binary
    # Mono was built with --prefix=/usr/local, so these compiled-in paths need
    # patching to /meadow0 so Mono looks at /meadow0/mono/4.5/ for BCL assemblies.
    # -------------------------------------------------------------------------
    sdram_patches: dict[str, int] = {}
    mono_bin = out_dir / "nuttx_mono.bin"
    SDRAM_BASE = 0xC0000000
    # "/meadow0\0" encoded as little-endian 32-bit words for sysbus WriteDoubleWord
    MEADOW0_WORDS = [0x61656D2F, 0x30776F64, 0x00000000]  # "/mea", "dow0", "\0\0\0\0"

    if mono_bin.exists():
        mono_data = mono_bin.read_bytes()
        for needle, tag in [(b"/usr/local/lib\x00", "USR_LOCAL_LIB"),
                            (b"/usr/local/etc\x00", "USR_LOCAL_ETC")]:
            offset = mono_data.find(needle)
            if offset >= 0:
                sdram_addr = SDRAM_BASE + offset
                sdram_patches[tag] = sdram_addr
                print(f"  {tag} string at SDRAM 0x{sdram_addr:08X} (mono offset 0x{offset:X})")
            else:
                print(f"  WARNING: '{needle.decode()}' not found in {mono_bin}", file=sys.stderr)
    else:
        print(f"  NOTE: {mono_bin} not found — SDRAM string patches skipped")

    # -------------------------------------------------------------------------
    # Write addresses.resc — reference/documentation only
    # -------------------------------------------------------------------------
    out_file = out_dir / "addresses.resc"
    lines = []
    lines.append("# AUTO-GENERATED by tools/extract-hook-addresses.py — DO NOT EDIT")
    lines.append(f"# Source: {kernel_elf}, {user_elf}")
    lines.append("# NOTE: These are for reference only. Renode's `set` variables cannot be")
    lines.append("# used as numeric parameters (UInt64/UInt32). The actual hook commands are")
    lines.append("# generated in build/legacy/hooks.resc with hardcoded hex addresses.")
    lines.append("")
    lines.append("# Hook addresses (function entry points and computed offsets)")
    for var_name in sorted(addrs):
        lines.append(f"# hook_{var_name} = 0x{addrs[var_name]:08X}")
    lines.append("")
    out_file.write_text("\n".join(lines) + "\n")
    print(f"\nGenerated {out_file} ({len(addrs)} hook addresses)")

    # -------------------------------------------------------------------------
    # Write hooks.resc — process template with $hook_* replaced by hex literals
    # Renode's `set` variables cannot be used as numeric command parameters
    # (UInt64, UInt32) or inside IronPython code strings. We process the
    # hand-maintained template and substitute all $hook_* references.
    # -------------------------------------------------------------------------
    script_dir = Path(__file__).parent.parent / "scripts"
    hooks_template = script_dir / "meadow-legacy-hooks.resc"
    hooks_out = out_dir / "hooks.resc"

    if hooks_template.exists():
        template_text = hooks_template.read_text()
        # Replace $hook_VARNAME with literal hex addresses, skipping comment lines
        def replace_hook_var(m):
            var_name = m.group(1)
            return f"0x{addrs[var_name]:08X}"

        set_re = re.compile(r'^\s*set\s+(\w+)\s+')
        skipped_vars: set[str] = set()
        result_lines = []
        for line in template_text.splitlines():
            if line.lstrip().startswith("#"):
                result_lines.append(line)  # preserve comment lines unchanged
                continue
            missing = [v for v in re.findall(r'\$hook_(\w+)', line) if v not in addrs]
            # A line can also reference a plain `set`-defined variable (not
            # $hook_*) whose own defining line was skipped above — e.g.
            # `cpu AddHook $hook_X $some_fix` where `set some_fix ...` was
            # itself skipped for missing a $hook_* symbol. That leaves
            # $some_fix undefined, so cascade the skip.
            other_refs = [v for v in re.findall(r'\$(\w+)', line) if not v.startswith('hook_')]
            depends_on_skipped = [v for v in other_refs if v in skipped_vars]
            if missing or depends_on_skipped:
                # Renode can't use an unresolved $hook_* (or a $variable whose
                # `set` line was skipped) as a command argument — comment the
                # line out instead of emitting a broken reference. Missing
                # symbols were already reported in the WARNING list above.
                reasons = []
                if missing:
                    reasons.append(f"missing symbol: {', '.join(missing)}")
                if depends_on_skipped:
                    reasons.append(f"depends on skipped: {', '.join(depends_on_skipped)}")
                result_lines.append(f"# SKIPPED ({'; '.join(reasons)}): {line}")
                m = set_re.match(line)
                if m:
                    skipped_vars.add(m.group(1))
            else:
                result_lines.append(re.sub(r'\$hook_(\w+)', replace_hook_var, line))
        processed = "\n".join(result_lines) + "\n"
        # Add auto-generated header
        header = (
            "# AUTO-GENERATED by tools/extract-hook-addresses.py — DO NOT EDIT\n"
            f"# Template: {hooks_template}\n"
            f"# Source: {kernel_elf}, {user_elf}\n"
            "# Edit scripts/meadow-legacy-hooks.resc (template), then re-run extraction.\n"
        )
        hooks_out.write_text(header + processed)
        print(f"Generated {hooks_out} (hooks with resolved addresses)")
    else:
        print(f"  WARNING: {hooks_template} not found — hooks.resc not generated", file=sys.stderr)

    # -------------------------------------------------------------------------
    # Write sdram-patches.resc — standalone Renode commands with hardcoded hex
    # Renode's WriteDoubleWord doesn't accept `set` variable expansion for
    # numeric parameters, so we generate the commands with literal addresses.
    # -------------------------------------------------------------------------
    sdram_file = out_dir / "sdram-patches.resc"
    sdram_lines = []
    sdram_lines.append("# AUTO-GENERATED by tools/extract-hook-addresses.py — DO NOT EDIT")
    sdram_lines.append(f"# Source: {mono_bin}")
    sdram_lines.append("# Patches Mono's compiled-in /usr/local paths to /meadow0")
    sdram_lines.append("")
    for tag, sdram_addr in sorted(sdram_patches.items()):
        sdram_lines.append(f'# Patch "{tag}" at SDRAM 0x{sdram_addr:08X} -> "/meadow0\\0"')
        sdram_lines.append(f"sysbus WriteDoubleWord 0x{sdram_addr:08X} 0x61656D2F")
        sdram_lines.append(f"sysbus WriteDoubleWord 0x{sdram_addr + 4:08X} 0x30776F64")
        sdram_lines.append(f"sysbus WriteDoubleWord 0x{sdram_addr + 8:08X} 0x00000000")
        sdram_lines.append(f"sysbus WriteByte 0x{sdram_addr + 12:08X} 0x00")
        sdram_lines.append(f"sysbus WriteByte 0x{sdram_addr + 13:08X} 0x00")
        sdram_lines.append(f"sysbus WriteByte 0x{sdram_addr + 14:08X} 0x00")
        sdram_lines.append("")
    sdram_file.write_text("\n".join(sdram_lines) + "\n")
    print(f"Generated {sdram_file} ({len(sdram_patches)} string patches)")

    # Also write indented version for embedding in reset macros
    reset_sdram_file = out_dir / "reset-sdram-patches.resc"
    reset_lines = []
    reset_lines.append("    # AUTO-GENERATED by tools/extract-hook-addresses.py — DO NOT EDIT")
    reset_lines.append("    # SDRAM string patches (Mono /usr/local → /meadow0)")
    for tag, sdram_addr in sorted(sdram_patches.items()):
        reset_lines.append(f"    sysbus WriteDoubleWord 0x{sdram_addr:08X} 0x61656D2F")
        reset_lines.append(f"    sysbus WriteDoubleWord 0x{sdram_addr + 4:08X} 0x30776F64")
        reset_lines.append(f"    sysbus WriteDoubleWord 0x{sdram_addr + 8:08X} 0x00000000")
        reset_lines.append(f"    sysbus WriteByte 0x{sdram_addr + 12:08X} 0x00")
        reset_lines.append(f"    sysbus WriteByte 0x{sdram_addr + 13:08X} 0x00")
        reset_lines.append(f"    sysbus WriteByte 0x{sdram_addr + 14:08X} 0x00")
    reset_sdram_file.write_text("\n".join(reset_lines) + "\n")
    print(f"Generated {reset_sdram_file} (for reset macro)")

    # Also write a validation marker (ELF modification times)
    marker = out_dir / ".addresses-generated"
    marker.write_text(f"{kernel_elf}\n{user_elf}\n")


if __name__ == "__main__":
    main()
