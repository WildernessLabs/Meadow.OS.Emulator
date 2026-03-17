R = _bus.ReadDoubleWord
for addr in range(0x4000681c, 0x40006870, 4):
    v = R(addr)
    _cpu.Log(LogLevel.Error, 'ROM 0x%08x: %08x' % (addr, v))
