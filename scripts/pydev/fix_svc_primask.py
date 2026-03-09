# Renode CPU hook to work around SVC-with-PRIMASK bug.
#
# Problem: On real ARMv7-M hardware, executing SVC while PRIMASK=1
# causes priority escalation to HardFault, which NuttX handles.
# Renode silently skips the SVC instead, causing arm_fullcontextrestore()
# to return (it shouldn't) and triggering PANIC().
#
# Fix: Clear PRIMASK before SVC executes so the exception fires normally.
# The saved context will restore PRIMASK on exception return.
#
# Must be hooked at every SVC instruction address in the kernel binary.

from Antmicro.Renode.Peripherals.CPU import RegisterValue

if cpu.PRIMASK.RawValue != 0:
    cpu.PRIMASK = RegisterValue.Create(0, 32)
