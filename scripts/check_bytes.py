R = _bus.ReadByte
# Check j instruction at 0x400d580d
b0 = R(0x400d580d)
b1 = R(0x400d580e)
b2 = R(0x400d580f)
_cpu.Log(LogLevel.Error, 'j at 400d580d: %02x %02x %02x' % (b0, b1, b2))

# Verify trampoline at 0x400C0080
b0 = R(0x400C0080)
b1 = R(0x400C0081)
b2 = R(0x400C0082)
_cpu.Log(LogLevel.Error, 'trampoline 400C0080: %02x %02x %02x (expect 00 d1 13)' % (b0, b1, b2))

# Verify retw.n at end of trampoline
b0 = R(0x400C009D)
b1 = R(0x400C009E)
_cpu.Log(LogLevel.Error, 'trampoline retw.n: %02x %02x (expect 1d f0)' % (b0, b1))
