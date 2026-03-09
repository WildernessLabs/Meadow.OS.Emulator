# STM32F7 RCC peripheral stub for Renode
# Auto-sets clock ready bits when enable bits are written.
#
# RCC_CR (offset 0x00):
#   bit 0  HSION  -> bit 1  HSIRDY
#   bit 16 HSEON  -> bit 17 HSERDY
#   bit 24 PLLON  -> bit 25 PLLRDY
#   bit 26 PLLI2SON -> bit 27 PLLI2SRDY
#   bit 28 PLLSAION -> bit 29 PLLSAIRDY
#
# RCC_BDCR (offset 0x70):
#   bit 0  LSEON  -> bit 1  LSERDY
#   bit 8  RTCEN  (stored as-is)
#
# RCC_CSR (offset 0x74):
#   bit 0  LSION  -> bit 1  LSIRDY
#
# RCC_CFGR (offset 0x08):
#   SW (bits 1:0) mirrored to SWS (bits 3:2)

if request.IsInit:
    regs = {}
    # RCC_CR reset: HSION and HSIRDY set
    regs[0x00] = 0x00000003
    # RCC_PLLCFGR reset value
    regs[0x04] = 0x24003010
    # RCC_CFGR reset
    regs[0x08] = 0x00000000

if request.IsRead:
    off = request.Offset
    request.Value = regs.get(off, 0)

elif request.IsWrite:
    off = request.Offset
    val = request.Value

    if off == 0x00:
        # RCC_CR: auto-set ready bits for each enabled oscillator/PLL
        if val & (1 << 0):
            val |= (1 << 1)    # HSIRDY
        if val & (1 << 16):
            val |= (1 << 17)   # HSERDY
        if val & (1 << 24):
            val |= (1 << 25)   # PLLRDY
        if val & (1 << 26):
            val |= (1 << 27)   # PLLI2SRDY
        if val & (1 << 28):
            val |= (1 << 29)   # PLLSAIRDY
        regs[off] = val

    elif off == 0x08:
        # RCC_CFGR: mirror SW (bits 1:0) into SWS (bits 3:2)
        sw = val & 0x3
        val = (val & ~0xC) | (sw << 2)
        regs[off] = val

    elif off == 0x70:
        # RCC_BDCR: auto-set LSERDY when LSEON is set
        if val & (1 << 0):
            val |= (1 << 1)    # LSERDY
        regs[off] = val

    elif off == 0x74:
        # RCC_CSR: auto-set LSIRDY when LSION is set
        if val & (1 << 0):
            val |= (1 << 1)    # LSIRDY
        regs[off] = val

    else:
        regs[off] = val
