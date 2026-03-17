/*
 * Minimal linker script for bare-metal ESP32 on Renode.
 * Places code in IRAM at 0x40080000.
 */

MEMORY
{
    iram (rwx) : ORIGIN = 0x40080000, LENGTH = 0x20000
    dram (rw)  : ORIGIN = 0x3FFB0000, LENGTH = 0x30000
}

ENTRY(_start)

SECTIONS
{
    .text : {
        *(.literal.entry)
        *(.text.entry)
        *(.literal)
        *(.literal.*)
        *(.text)
        *(.text.*)
    } > iram

    .rodata : {
        *(.rodata)
        *(.rodata.*)
    } > iram

    .data : {
        *(.data)
        *(.data.*)
    } > dram

    .bss : {
        *(.bss)
        *(.bss.*)
        *(COMMON)
    } > dram
}
