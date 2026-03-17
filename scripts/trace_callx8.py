# Register indices: A0=89..A15=104, PC=0, EPC1=51, EXCCAUSE=77, WINDOWBASE=34
import clr
clr.AddReference('Renode')
from Antmicro.Renode.Peripherals.CPU import RegisterValue

_callx8_count = [0]
_seq = [0]

def _trace_seq(label):
    _seq[0] += 1
    s = _seq[0]
    if s <= 50:
        _cpu.Log(LogLevel.Error, 'SEQ#%d %s' % (s, label))

def _trace_callx8():
    _callx8_count[0] += 1
    c = _callx8_count[0]
    if c <= 10:
        a8 = _cpu.GetRegister(97).RawValue
        a12 = _cpu.GetRegister(101).RawValue
        _trace_seq('CALLX8_intr_matrix A8=0x%08x A12=0x%x' % (a8, a12))

def _trace_trampoline_entry():
    a0 = _cpu.GetRegister(89).RawValue
    wb = _cpu.GetRegister(34).RawValue
    _trace_seq('TRAMPOLINE A0=0x%08x WB=%d' % (a0, wb))

def _trace_trampoline_retw():
    a0 = _cpu.GetRegister(89).RawValue
    wb = _cpu.GetRegister(34).RawValue
    _trace_seq('TRAMPOLINE_RETW A0=0x%08x WB=%d' % (a0, wb))

def _trace_exc_first():
    epc1 = _cpu.GetRegister(51).RawValue
    cause = _cpu.GetRegister(77).RawValue
    _trace_seq('USER_EXC EPC1=0x%08x CAUSE=%d' % (epc1, cause))

__builtin__._trace_seq = _trace_seq
__builtin__._seq = _seq
__builtin__._trace_callx8 = _trace_callx8
__builtin__._callx8_count = _callx8_count
__builtin__._trace_trampoline_entry = _trace_trampoline_entry
__builtin__._trace_trampoline_retw = _trace_trampoline_retw
__builtin__._trace_exc_first = _trace_exc_first
