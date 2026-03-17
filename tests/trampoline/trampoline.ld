/*
 * Linker script for ESP32 window init trampoline.
 * Places literal pool at 0x4009F000, code at 0x4009F00C.
 */

MEMORY
{
    trampoline (rwx) : ORIGIN = 0x4009F000, LENGTH = 0x1000
}

ENTRY(_start)

SECTIONS
{
    .trampoline : {
        *(.literal)
        . = ALIGN(4);
        *(.text)
        *(.text.*)
    } > trampoline
}
