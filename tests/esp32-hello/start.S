/*
 * Minimal ESP32 Xtensa startup assembly.
 * Initializes the register window mechanism and calls main().
 *
 * ESP32 Xtensa always uses windowed ABI, so we must set up:
 * - WINDOWBASE = 0
 * - WINDOWSTART = 1 (window 0 is valid)
 * - PS.WOE = 1 (Window Overflow Enable)
 */

    .section .text.entry, "ax"
    .global _start
    .align 4

_start:
    /* Initialize window registers */
    movi    a0, 0
    wsr     a0, windowbase      /* WINDOWBASE = 0 */
    rsync

    movi    a0, 1
    wsr     a0, windowstart     /* WINDOWSTART = 1 (window 0 valid) */
    rsync

    /* Set PS: WOE=1 (bit 18), CALLINC=1 (bits 17:16), UM=0, INTLEVEL=0 */
    movi    a0, 0x00040000
    wsr     a0, ps
    rsync

    /* Set up stack pointer — top of SRAM1 data region */
    movi    a1, 0x3FFFFFF0

    /* Clear a0 (return address) */
    movi    a0, 0

    /* Call main using windowed call */
    call4   main

    /* If main returns, spin */
_halt:
    j       _halt
