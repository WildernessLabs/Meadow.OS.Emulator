/*
 * Window register init trampoline for ESP32 Renode emulation.
 *
 * Placed at 0x4009F000 in IRAM (unused region after firmware code).
 * Literal pool at 0x4009F000, code at 0x4009F00C.
 *
 * Initializes WINDOWBASE, WINDOWSTART, PS (with WOE=1), sets up
 * a stack pointer, then jumps to call_start_cpu0 (the ESP-IDF entry point).
 *
 * The boot script patches callx8 -> jx at 0x4009F02D to save one window
 * pane (call_start_cpu0 never returns, so the extra window is wasted).
 */

    .section .literal, "a"
    .align 4
.Lentry_addr:
    .word 0x40081BBC        /* call_start_cpu0 address */
.Lstack_addr:
    .word 0x3FFE5F00        /* stack pointer (in DRAM) */
.Lps_val:
    .word 0x00040000        /* PS: WOE=1, CALLINC=1 */

    .section .text, "ax"
    .global _start
    .align 4

_start:
    /* WINDOWBASE = 0 */
    movi    a0, 0
    wsr     a0, windowbase
    rsync

    /* WINDOWSTART = 1 (only window 0 is valid) */
    movi    a0, 1
    wsr     a0, windowstart
    rsync

    /* PS: WOE=1 (bit 18), enables window overflow/underflow exceptions */
    l32r    a0, .Lps_val
    wsr     a0, ps
    rsync

    /* Set up stack pointer */
    l32r    a1, .Lstack_addr

    /* Load entry point address */
    l32r    a8, .Lentry_addr

    /* Clear return address */
    movi    a0, 0

    /* Jump to call_start_cpu0 (callx8 patched to jx by boot script) */
    callx8  a8
