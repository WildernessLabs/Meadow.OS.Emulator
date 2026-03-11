# STM32F7 FMC (Flexible Memory Controller) stub for Renode
# Handles SDRAM initialization sequence without real hardware.
#
# FMC base: 0xA0000000, size: 0x1000
# SDRAM registers at offsets 0x140-0x158:
#   SDCR1  (0x140) - SDRAM Control Register, Bank 1
#   SDCR2  (0x144) - SDRAM Control Register, Bank 2
#   SDTR1  (0x148) - SDRAM Timing Register, Bank 1
#   SDTR2  (0x14C) - SDRAM Timing Register, Bank 2
#   SDCMR  (0x150) - SDRAM Command Register
#   SDRTR  (0x154) - SDRAM Refresh Timing Register
#   SDSR   (0x158) - SDRAM Status Register
#
# NuttX's stm32_enablefmc() writes SDCR/SDTR, then issues commands
# via SDCMR (CLKEN, PALL, AUTO_REFRESH, LOAD_MODE) and polls SDSR
# bit 5 for command completion.

if request.IsInit:
    regs = {}
    # SDSR reset: no command in progress (bit 5 = 0), banks idle
    regs[0x158] = 0x00000000

if request.IsRead:
    off = request.Offset
    if off == 0x158:
        # SDSR: always return "not busy" (bit 5 = 0)
        # Bits [1:0] = Bank 1 mode (00 = normal)
        # Bits [3:2] = Bank 2 mode (00 = normal)
        # Bit 5 = Busy (0 = no command in progress)
        request.Value = 0x00000000
    else:
        request.Value = regs.get(off, 0)

elif request.IsWrite:
    off = request.Offset
    val = request.Value

    if off == 0x150:
        # SDCMR: command register - accept and ignore
        # Commands: NORMAL(0), CLK_ENABLE(1), PALL(2),
        #           AUTO_REFRESH(3), LOAD_MODE(4),
        #           SELF_REFRESH(5), POWER_DOWN(6)
        regs[off] = 0  # Command completes instantly
    else:
        regs[off] = val
