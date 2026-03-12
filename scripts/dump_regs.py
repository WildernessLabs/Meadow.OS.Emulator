regs = ['EXCCAUSE', 'EPC1', 'EPC2', 'EPC3', 'EXCVADDR', 'PS', 'WINDOWBASE', 'WINDOWSTART', 'EXCSAVE1']
for name in regs:
    try:
        idx = _cpu.GetRegisters()
        # Try to find register by name in the enumeration
        found = False
        for i, r in enumerate(idx):
            if str(r) == name:
                v = _cpu.GetRegister(i).RawValue
                _cpu.Log(LogLevel.Error, '%s = 0x%08x' % (name, v))
                found = True
                break
        if not found:
            _cpu.Log(LogLevel.Error, '%s = NOT FOUND' % name)
    except Exception as e:
        _cpu.Log(LogLevel.Error, '%s error: %s' % (name, str(e)))
