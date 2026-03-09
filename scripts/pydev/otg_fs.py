# STM32F7 USB OTG FS stub for Renode
# Handles core reset and initialization polling loops.
#
# OTG_FS base: 0x50000000, size: 0x40000
# Key registers (offsets from base):
#   0x010: GRSTCTL - bit 0 (CSRST) auto-clears, bit 31 (AHBIDL) always set
#   0x038: GINTSTS - clear-on-write status bits

if request.IsInit:
    regs = {}
    # GRSTCTL: AHBIDL (bit 31) = 1 at reset (AHB master idle)
    regs[0x010] = 0x80000000
    # GINTSTS: some default status
    regs[0x038] = 0x04000020
    # GUSBCFG default
    regs[0x00C] = 0x00001440

if request.IsRead:
    off = request.Offset
    request.Value = regs.get(off, 0)

elif request.IsWrite:
    off = request.Offset
    val = request.Value

    if off == 0x010:
        # GRSTCTL: auto-clear reset bits, keep AHBIDL set
        # Bit 0 (CSRST), bit 4 (RXFFLSH), bit 5 (TXFFLSH) auto-clear
        val &= ~0x31  # clear CSRST, RXFFLSH, TXFFLSH
        val |= 0x80000000  # AHBIDL always set
        regs[off] = val
    elif off == 0x038:
        # GINTSTS: writing 1 clears bits (W1C)
        current = regs.get(off, 0)
        regs[off] = current & ~val
    else:
        regs[off] = val
