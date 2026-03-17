# Test: can we set PC and WINDOWBASE from Python?
import clr
clr.AddReference('Renode')
from Antmicro.Renode.Peripherals.CPU import RegisterValue

# PC=0, WINDOWBASE=34, WINDOWSTART=35, A0=89
try:
    pc = _cpu.GetRegister(0).RawValue
    wb = _cpu.GetRegister(34).RawValue
    a0 = _cpu.GetRegister(89).RawValue
    _cpu.Log(LogLevel.Error, 'PC=0x%08x WB=%d A0=0x%08x' % (pc, wb, a0))

    # Test writing PC
    _cpu.SetRegister(0, RegisterValue.Create(0x40080000, 32))
    new_pc = _cpu.GetRegister(0).RawValue
    _cpu.Log(LogLevel.Error, 'PC after set = 0x%08x' % new_pc)

    # Restore
    _cpu.SetRegister(0, RegisterValue.Create(pc, 32))
except Exception as e:
    _cpu.Log(LogLevel.Error, 'Test failed: %s' % str(e))
