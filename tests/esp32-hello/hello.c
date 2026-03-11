/*
 * Minimal bare-metal ESP32 hello-world for Renode boot spike.
 * Writes directly to UART0 FIFO register — no ESP-IDF, no bootloader.
 *
 * ESP32 UART0 base: 0x3FF40000
 * UART_FIFO_REG offset: 0x00 (write byte to TX FIFO)
 */

#define UART0_FIFO  (*(volatile unsigned char *)0x3FF40000)

static void uart_putc(char c) {
    UART0_FIFO = (unsigned char)c;
}

static void uart_puts(const char *s) {
    while (*s) {
        uart_putc(*s++);
    }
}

/* Entry point — called from startup assembly */
void main(void) {
    uart_puts("Hello from ESP32 Xtensa on Renode!\n");

    /* Spin forever */
    while (1) {
        /* NOP loop */
        __asm__ volatile("nop");
    }
}
