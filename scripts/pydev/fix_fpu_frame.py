# Renode CPU hook script to fix the FPU frame mismatch.
#
# Problem: Renode's exception entry uses standard frames (EXC_RETURN bit4=1)
# even when CONTROL.FPCA=1, but NuttX's exception_common always calculates
# REG_R13 assuming extended frames (adds 104 instead of 32).
#
# This hook runs at 0x080009B6 (right before bl arm_doirq) and corrects
# REG_R13 when the saved EXC_RETURN indicates a standard frame.
#
# At hook point:
#   R1 = context base pointer (first reg in saved array)
#   Context layout: [R13, PRIMASK, R4-R11, CONTROL, EXC_RETURN, ...]
#   REG_R13 at offset 0, REG_EXC_RETURN at offset 44 (11*4)

ctx = cpu.GetRegisterUnsafe(1).RawValue
exc_ret = machine.SystemBus.ReadDoubleWord(ctx + 44)

# Check bit 4: if set (standard frame), REG_R13 is 72 bytes too high
if exc_ret & 0x10:
    old_sp = machine.SystemBus.ReadDoubleWord(ctx)
    new_sp = old_sp - 72
    machine.SystemBus.WriteDoubleWord(ctx, new_sp)
