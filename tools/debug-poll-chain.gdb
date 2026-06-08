# debug-poll-chain.gdb — Trace the ESPCP polling activation chain
#
# Hook flags are in SDRAM gap at 0xC02FFC00 (between Mono binary end and heap start).
# Previously at 0x2007FFF0 (SRAM2) — was being clobbered by NuttX user heap.

target remote :3333
set pagination off
set confirm off
symbol-file build/legacy/nuttx.elf

define check_flags
    printf "poll_flag    (0xC02FFC00) = %d\n", *(unsigned char*)0xC02FFC00
    printf "mono_ready   (0xC02FFC04) = %d\n", *(unsigned char*)0xC02FFC04
    printf "g_espcp_config           = 0x%08x\n", *(unsigned int*)0x20020F74
end

define show_esp_not_responding
    set $cfg = *(unsigned int*)0x20020F74
    if $cfg != 0
        printf "esp_not_responding (config+0x30) = %d\n", *(unsigned char*)($cfg + 0x30)
    else
        printf "g_espcp_config is NULL\n"
    end
end

# Watchpoints on flag bytes
awatch *(unsigned char*)0xC02FFC00
awatch *(unsigned char*)0xC02FFC04

# upd_handle_esp32_command entry
break *0x08140CD0
commands
    printf "\n=== upd_handle_esp32_command HIT ===\n"
    info registers r0 r1 r2 r3 lr
    check_flags
    show_esp_not_responding
    continue
end

# Hook point inside upd_handle_esp32_command
break *0x08140CEA
commands
    printf "\n=== espcp_managed_cmd_fix HOOK POINT ===\n"
    printf "R0 (config ptr) = 0x%08x\n", $r0
    check_flags
    show_esp_not_responding
    continue
end

# espcp_queue_message
break *0x081484E4
commands
    printf "\n=== espcp_queue_message HIT ===\n"
    check_flags
    continue
end

# start_mono_main
break *0x080618F8
commands
    printf "\n=== start_mono_main HIT ===\n"
    check_flags
    continue
end

printf "\nType 'continue' to resume.\n"
check_flags
