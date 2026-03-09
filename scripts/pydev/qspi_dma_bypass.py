# QSPI DMA bypass hook for Renode
# Hooks qspi_memory_dma() to do a direct memcpy from flash backing memory
# instead of using DMA (which never completes because STM32H7_QuadSPI
# doesn't drive DMA requests).
#
# Two hooks are needed:
# 1. At qspi_memory_dma entry (0x08036db4): do the memcpy, skip function
# 2. At the str after the bl (0x0803731e): fix return value to 0
#
# struct qspi_meminfo_s layout (ARM32, from nuttx/include/nuttx/spi/qspi.h):
#   +0   uint8_t  flags    (QSPIMEM_WRITE=0x04, QSPIMEM_READ=0x00)
#   +1   uint8_t  addrlen
#   +2   uint8_t  dummies
#   +3   (1 byte padding)
#   +4   uint16_t cmd
#   +6   (2 bytes padding)
#   +8   uint32_t buflen
#   +12  uint32_t addr     (flash address)
#   +16  uint32_t key
#   +20  void *   buffer

FLASH_BASE = 0x90000000

def qspi_dma_hook(cpu, addr):
    sysbus = self.Machine.SystemBus
    meminfo = cpu.GetRegisterUnsafe(1).RawValue

    flags  = sysbus.ReadDoubleWord(meminfo + 0) & 0xFF
    buflen = sysbus.ReadDoubleWord(meminfo + 8)
    flash_addr = sysbus.ReadDoubleWord(meminfo + 12)
    buf_ptr = sysbus.ReadDoubleWord(meminfo + 20)

    if flags & 0x04:  # QSPIMEM_WRITE
        dst = FLASH_BASE + flash_addr
        data = sysbus.ReadBytes(buf_ptr, int(buflen))
        sysbus.WriteBytes(data, dst)
    else:  # QSPIMEM_READ (flags & 0x04 == 0)
        src = FLASH_BASE + flash_addr
        data = sysbus.ReadBytes(src, int(buflen))
        sysbus.WriteBytes(data, buf_ptr)

    # Skip the function entirely - return to caller
    cpu.PC = cpu.LR

def qspi_dma_fixret(cpu, addr):
    # After bl qspi_memory_dma returns, the str r0,[sp,#44] stores
    # the return value. R0 contains 'priv' (non-zero). Overwrite
    # the stack slot with 0 (success).
    sp = cpu.GetRegisterUnsafe(13).RawValue
    self.Machine.SystemBus.WriteDoubleWord(sp + 44, 0)
