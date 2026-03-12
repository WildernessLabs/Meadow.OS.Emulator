/*
 * Retw-bypass trampoline verification assembly.
 * Assemble to get correct byte encodings.
 */
    .section .text, "ax"
    .global _start
    .align 4

_start:
    wsr     a0, excsave1        /* save return addr */
    rsr     a0, windowbase      /* a0 = WB */
    addi    a0, a0, -2          /* a0 = WB - 2 */
    wsr     a0, windowbase      /* switch to caller's window */
    rsync
    rsr     a8, excsave1        /* a8 = return addr */
    movi    a9, -1              /* a9 = 0xFFFFFFFF */
    slli    a9, a9, 30          /* a9 = 0xC0000000 */
    xor     a8, a8, a9          /* strip CALLINC bits */
    jx      a8                  /* jump to return PC */
