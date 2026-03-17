# Read _xt_interrupt_table base from literal pool at 0x4008076c
R = _bus.ReadDoubleWord
base_ptr = R(0x4008076c)
_cpu.Log(LogLevel.Error, '_xt_interrupt_table base = 0x%08x' % base_ptr)

# Table layout: for each interrupt (0-31) x 2 cores = 64 entries
# Each entry is 8 bytes: [handler_ptr, arg_ptr]
# Index = intno * 2 + core (core 0)
# So for interrupt N on core 0: offset = N * 2 * 8 = N * 16
for intno in range(32):
    offset = intno * 16  # intno * 2 * 8 (2 cores, 8 bytes per entry)
    handler = R(base_ptr + offset)
    arg = R(base_ptr + offset + 4)
    if handler != 0 or intno in [2, 3, 9, 12, 13]:
        _cpu.Log(LogLevel.Error, 'INT[%2d] core0: handler=0x%08x arg=0x%08x' % (intno, handler, arg))
