// MQ Receive Intercept — C# replacement for the IronPython mq_intercept hook.
//
// Hooks mq_receive to intercept calls from espcp_thread. When the caller is
// espcp_thread and the poll flag is set, injects a send_response message pointer
// into the message buffer and returns immediately. This drives ESPCP polling
// without real SPI DMA.
//
// Throttling: only injects a real message every `pollDivisor` calls from
// espcp_thread. For skipped calls, redirects the thread to usleep(pollSleepUs)
// so it blocks in the kernel instead of spinning. This yields CPU time to HCOM
// and other threads. Without throttling, the tight mq_receive loop runs at
// ~16M iterations/sec (each firing the C# hook), starving all other work.
//
// The hook fires on ALL mq_receive calls system-wide, so it must be fast.
// The C# version avoids IronPython interpreter overhead (~3ms per invocation).
//
// Usage from .resc:
//   cpu InstallMqIntercept <mq_receive> <espcpLR> <sendResponseAddr> <pollFlagAddr> <pollDivisor> <usleepAddr> <loopTopAddr> <pollSleepUs>
//   cpu InstallMqIntercept 0x08157C40 0x0814E95B 0x2007C010 0x2007FFF0 100 0x0815F77C 0x0814E94D 10000

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
            ulong sendResponseAddr, ulong pollFlagAddr,
            uint pollDivisor = 100,
            ulong usleepAddr = 0, ulong loopTopAddr = 0,
            uint pollSleepUs = 10000,
            ulong spiDispatchAddr = 0, ulong gRequestResponseMsg = 0)
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
            long sleepHits = 0;
            long espcpCalls = 0;
            long passthroughHits = 0;
            bool canDirectDispatch = spiDispatchAddr != 0 && gRequestResponseMsg != 0;
            uint divisor = pollDivisor > 0 ? pollDivisor : 1;
            bool canSleep = usleepAddr != 0 && loopTopAddr != 0;

            cpu.AddHook(mqReceiveAddress, (ICpuSupportingGdb cpuArg, ulong pc) =>
            {
                totalHits++;
                var lr = arm.LR.RawValue;
                var poll = bus.ReadByte(pollFlagAddr);

                if(lr == espcpThreadLR && poll == 1)
                {
                    espcpCalls++;

                    // Check if there are real messages in the queue (from managed code).
                    // R0 = mqd_t (struct mq_des *):
                    //   offset 0: flink (4 bytes)
                    //   offset 4: msgq (pointer to mqueue_inode_s)
                    // mqueue_inode_s:
                    //   offset 0: inode (4)
                    //   offset 4: msglist.head (4)
                    //   offset 8: msglist.tail (4)
                    //   offset 12: maxmsgs (int16)
                    //   offset 14: nmsgs (int16)
                    // If nmsgs > 0, let the real mq_receive run to process the command.
                    ulong mqd = arm.GetRegister((int)ArmRegisters.R0).RawValue;
                    uint msgqPtr = bus.ReadDoubleWord(mqd + 4);  // mq_des.msgq at offset 4
                    short nmsgs = (short)bus.ReadWord(msgqPtr + 14);

                    if(nmsgs > 0)
                    {
                        // Real message waiting — let mq_receive execute normally
                        passthroughHits++;
                        return;
                    }

                    if(canDirectDispatch)
                    {
                        // Direct dispatch: skip mq_receive entirely, redirect PC to
                        // the SPI dispatch point in espcp_thread. Write the static
                        // send_response message pointer to [sp, #4] (where the
                        // dispatch reads it) and jump to espcp_spi_interface_lock.
                        // This makes espcp_thread call espcp_get_message immediately
                        // without going through the NuttX scheduler or mq_receive.
                        uint msgPtr = bus.ReadDoubleWord(gRequestResponseMsg);
                        ulong sp = arm.SP.RawValue;
                        bus.WriteDoubleWord(sp + 4, msgPtr);
                        arm.PC = RegisterValue.Create((uint)spiDispatchAddr | 1, 32);
                        interceptedHits++;
                    }
                    else if(espcpCalls % divisor == 0)
                    {
                        // Legacy inject: return from mq_receive with send_response pointer
                        ulong msgBuf = arm.GetRegister((int)ArmRegisters.R1).RawValue;
                        bus.WriteDoubleWord(msgBuf, (uint)sendResponseAddr);
                        arm.SetRegister((int)ArmRegisters.R0, RegisterValue.Create(4, 32));
                        arm.PC = arm.LR;
                        interceptedHits++;
                    }
                    else if(canSleep)
                    {
                        arm.SetRegister((int)ArmRegisters.R0,
                            RegisterValue.Create(pollSleepUs, 32));
                        arm.SetRegister((int)ArmRegisters.LR,
                            RegisterValue.Create((uint)loopTopAddr, 32));
                        arm.PC = RegisterValue.Create((uint)usleepAddr | 1, 32);
                        sleepHits++;
                    }
                    else
                    {
                        arm.SetRegister((int)ArmRegisters.R0,
                            RegisterValue.Create(unchecked((uint)-1), 32));
                        arm.PC = arm.LR;
                        sleepHits++;
                    }
                }

                if(totalHits % 50000 == 0)
                {
                    arm.Log(LogLevel.Info,
                        "MqReceiveIntercept: {0} total, {1} intercepted, {2} slept, {3} passthrough (divisor={4})",
                        totalHits, interceptedHits, sleepHits, passthroughHits, divisor);
                }
            });

            arm.Log(LogLevel.Info,
                "MqReceiveIntercept: C# hook installed at 0x{0:X8}, espcp LR=0x{1:X8}, pollDivisor={2}, usleep=0x{3:X8}, sleep={4}us",
                mqReceiveAddress, espcpThreadLR, divisor, usleepAddr, pollSleepUs);
        }
    }
}
