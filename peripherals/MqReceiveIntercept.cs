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
//   cpu InstallMqIntercept 0x08157C40 0x0814E95B 0x2007C010 0xC02FFC00 100 0x0815F77C 0x0814E94D 10000

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

                    // Drain feedback send_response messages from the queue.
                    // The MessageWaiting GPIO ISR re-queues a send_response message
                    // after every SPI exchange, keeping nmsgs > 0. Without draining,
                    // the hook always passes through and espcp_thread runs a tight
                    // loop instead of sleeping.
                    //
                    // NuttX legacy mqueue layout (singly-linked, ARM32):
                    //   mq_des: +0 flink(4), +4 msgq(4) → mqueue_inode_s
                    //   mqueue_inode_s: +0 inode(4), +4 msglist.head(4),
                    //     +8 msglist.tail(4), +12 maxmsgs(i16), +14 nmsgs(i16),
                    //     +16 nwaitnotfull(i16), +18 nwaitnotempty(i16),
                    //     +20 freelist.head(4), +24 freelist.tail(4)
                    //   mqueue_msg_s: +0 next(4), +4 type(1), +5 priority(1),
                    //     +6 msglen(1), +7 mail[] (message data)
                    ulong mqd = arm.GetRegister((int)ArmRegisters.R0).RawValue;
                    uint msgqPtr = bus.ReadDoubleWord(mqd + 4);
                    short nmsgs = (short)bus.ReadWord(msgqPtr + 14);

                    // Drain all feedback messages (mail == sendResponseAddr).
                    // Stop at the first real command message and passthrough.
                    while(nmsgs > 0)
                    {
                        uint headMsg = bus.ReadDoubleWord(msgqPtr + 4);
                        if(headMsg == 0) break;

                        // Peek at mail content at offset +7 (unaligned, read bytes)
                        uint msgContent = (uint)(
                            bus.ReadByte(headMsg + 7) |
                            (bus.ReadByte(headMsg + 8) << 8) |
                            (bus.ReadByte(headMsg + 9) << 16) |
                            (bus.ReadByte(headMsg + 10) << 24));

                        if(msgContent != (uint)sendResponseAddr)
                        {
                            // Real command — let mq_receive process it
                            passthroughHits++;
                            return;
                        }

                        // Drain this feedback message from the queue
                        uint nextMsg = bus.ReadDoubleWord(headMsg);
                        bus.WriteDoubleWord(msgqPtr + 4, nextMsg);   // msglist.head = next
                        if(nextMsg == 0)
                            bus.WriteDoubleWord(msgqPtr + 8, 0);     // msglist.tail = NULL
                        nmsgs--;
                        bus.WriteWord(msgqPtr + 14, (ushort)nmsgs);

                        // Return message node to the freelist
                        uint freeHead = bus.ReadDoubleWord(msgqPtr + 20);
                        bus.WriteDoubleWord(headMsg, freeHead);      // msg->next = freelist.head
                        bus.WriteDoubleWord(msgqPtr + 20, headMsg);  // freelist.head = msg
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
