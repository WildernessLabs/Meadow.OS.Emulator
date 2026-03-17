regs = _cpu.GetRegisters()
targets = ['AR0','AR1','AR2','AR3','AR4','AR8','AR48','A0','WINDOWBASE']
for i, r in enumerate(regs):
    n = str(r)
    if n in targets:
        _cpu.Log(LogLevel.Error, 'REG_INDEX %s = %d' % (n, i))
