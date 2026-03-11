// MQ Receive Intercept — C# replacement for the IronPython mq_intercept hook.
//
// Hooks mq_receive to intercept calls from espcp_thread. When the caller is
// espcp_thread and the poll flag is set, injects a send_response message pointer
// into the message buffer and returns immediately. This drives ESPCP polling
// without real SPI DMA.
//
// The hook fires on ALL mq_receive calls system-wide, so it must be fast.
// The C# version avoids IronPython interpreter overhead (~3ms per invocation).
//
// Usage from .resc:
//   cpu InstallMqIntercept 0x08157c40 0x0814e95b 0x2007C010 0x2007FFF0

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class MqReceiveInterceptExtension
    {
        public static void InstallMqIntercept(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine,
            ulong mqReceiveAddress, ulong espcpThreadLR,
            ulong sendResponseAddr, ulong pollFlagAddr)
        {
            var arm = cpu as Arm;
            if(arm == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "MqReceiveIntercept: CPU must be an ARM core");
            }

            var bus = machine.SystemBus;
            long totalHits = 0;
            long interceptedHits = 0;

            cpu.AddHook(mqReceiveAddress, (ICpuSupportingGdb cpuArg, ulong pc) =>
            {
                totalHits++;
                var lr = arm.LR.RawValue;
                var poll = bus.ReadByte(pollFlagAddr);

                if(lr == espcpThreadLR && poll == 1)
                {
                    // Inject send_response message pointer into buffer (R1 = msg buffer)
                    ulong msgBuf = arm.GetRegister((int)ArmRegisters.R1).RawValue;
                    bus.WriteDoubleWord(msgBuf, (uint)sendResponseAddr);
                    arm.SetRegister((int)ArmRegisters.R0, RegisterValue.Create(4, 32));
                    arm.PC = arm.LR;
                    interceptedHits++;
                }

                if(totalHits % 50000 == 0)
                {
                    arm.Log(LogLevel.Info,
                        "MqReceiveIntercept: {0} total hits, {1} intercepted",
                        totalHits, interceptedHits);
                }
            });

            arm.Log(LogLevel.Info,
                "MqReceiveIntercept: C# hook installed at 0x{0:X8}, espcp LR=0x{1:X8}",
                mqReceiveAddress, espcpThreadLR);
        }
    }
}
