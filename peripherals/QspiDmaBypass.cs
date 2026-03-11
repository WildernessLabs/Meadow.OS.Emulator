// QSPI DMA Bypass — C# replacement for the IronPython qspi_dma_bypass hook.
//
// The STM32H7_QuadSPI model in Renode doesn't drive DMA requests, so the NuttX
// qspi_memory() function (which uses DMA to move data between QSPI flash and RAM)
// never completes. This extension method hooks qspi_memory at its entry point and
// performs a direct memory copy between the QSPI flash backing memory and the
// destination buffer, then returns to the caller.
//
// This is 10-100x faster than the equivalent IronPython hook because it runs as
// compiled .NET code rather than being interpreted on every invocation.
//
// Usage from .resc:   cpu InstallQspiBypass 0x0816136a 0x90000000
//
// struct qspi_meminfo_s layout (ARM32, from nuttx/include/nuttx/spi/qspi.h):
//   +0   uint8_t  flags    (QSPIMEM_WRITE=0x04, QSPIMEM_READ=0x00)
//   +1   uint8_t  addrlen
//   +2   uint8_t  dummies
//   +3   (1 byte padding)
//   +4   uint16_t buflen
//   +6   uint16_t cmd
//   +8   uint32_t addr     (flash address)
//   +12  uint32_t key
//   +16  void *   buffer

using System;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class QspiDmaBypassExtension
    {
        public static void InstallQspiBypass(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress, ulong flashBase)
        {
            var arm = cpu as Arm;
            if(arm == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "QspiDmaBypass: CPU must be an ARM core");
            }

            var bus = machine.SystemBus;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb cpuArg, ulong pc) =>
            {
                // R1 = pointer to struct qspi_meminfo_s
                ulong meminfo = arm.GetRegister((int)ArmRegisters.R1).RawValue;

                // Parse struct fields
                byte flags = bus.ReadByte(meminfo);
                int buflen = (int)(bus.ReadDoubleWord(meminfo + 4) & 0xFFFF);
                ulong faddr = bus.ReadDoubleWord(meminfo + 8);
                ulong buf = bus.ReadDoubleWord(meminfo + 16);

                if(buflen > 0)
                {
                    if((flags & QSPIMEM_WRITE) != 0)
                    {
                        var data = bus.ReadBytes(buf, buflen);
                        bus.WriteBytes(data, flashBase + faddr);
                    }
                    else
                    {
                        var data = bus.ReadBytes(flashBase + faddr, buflen);
                        bus.WriteBytes(data, buf);
                    }
                }

                // Return from function — set PC = LR
                arm.PC = arm.LR;
            });

            arm.Log(LogLevel.Info,
                "QspiDmaBypass: C# hook installed at 0x{0:X8}, flash base 0x{1:X8}",
                hookAddress, flashBase);
        }

        private const byte QSPIMEM_WRITE = 0x04;
    }
}
