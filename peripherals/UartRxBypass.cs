// UART RX Bypass — permanent suppress + wake byte
using System;
using System.Collections.Concurrent;
using Antmicro.Renode.Core;
using Antmicro.Renode.Backends.Terminals;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class UartRxBypassExtension
    {
        public static void InstallUartRxBypass(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong readCallAddr, ulong readReturnAddr)
        {
            var arm = cpu as Arm;
            if(arm == null)
                throw new Antmicro.Renode.Exceptions.RecoverableException("UartRxBypass: CPU must be ARM");

            BackendTerminal hcomTerminal = null;
            try
            {
                var emulation = EmulationManager.Instance.CurrentEmulation;
                IExternal ext;
                if(emulation.ExternalsManager.TryGetByName("hcom", out ext))
                    hcomTerminal = ext as BackendTerminal;
            }
            catch {}
            if(hcomTerminal == null) { arm.Log(LogLevel.Warning, "UartRxBypass: terminal not found"); return; }

            STM32F7_USART uart4 = null;
            foreach(var entry in machine.GetRegisteredPeripherals())
            {
                if(entry.Peripheral is STM32F7_USART usart)
                {
                    var rp = entry.RegistrationPoint.ToString();
                    if(rp.Contains("40004C00"))
                    { uart4 = usart; break; }
                }
            }
            if(uart4 == null) { arm.Log(LogLevel.Warning, "UartRxBypass: UART4 not found"); return; }

            SocketServerProvider hcomProvider = null;
            try
            {
                var field = hcomTerminal.GetType().GetField("server",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if(field != null) hcomProvider = field.GetValue(hcomTerminal) as SocketServerProvider;
            }
            catch {}
            if(hcomProvider == null) { arm.Log(LogLevel.Warning, "UartRxBypass: provider not found"); return; }

            var bus = machine.SystemBus;
            var rxQueue = new ConcurrentQueue<byte>();
            var drainBuffer = new byte[8192];
            long callCount = 0;
            long totalBytes = 0;
            bool activated = false;

            // Queue handler + wake mechanism: when data arrives at queue,
            // write a dummy byte directly to UART to wake read() from sem_wait.
            bool readBlocked = false;
            hcomProvider.DataReceived += (int b) =>
            {
                rxQueue.Enqueue((byte)b);
                // Only send wake byte when read() is actually blocking on sem_wait
                if(readBlocked && rxQueue.Count == 1)
                {
                    uart4.WriteChar(0x00);
                }
            };

            cpu.AddHook(readCallAddr, (ICpuSupportingGdb c, ulong pc) =>
            {
                // Deferred activation — don't suppress until first successful
                // queue read (after boot/init uses normal UART path)

                uint bufferAddr = (uint)arm.GetRegister((int)ArmRegisters.R1).RawValue;
                int maxCount = (int)(uint)arm.GetRegister((int)ArmRegisters.R2).RawValue;
                if(maxCount > drainBuffer.Length) maxCount = drainBuffer.Length;

                int count = 0;
                byte b;
                while(count < maxCount && rxQueue.TryDequeue(out b))
                    drainBuffer[count++] = b;

                callCount++;

                if(count > 0)
                {
                    readBlocked = false;
                    if(!activated)
                    {
                        hcomTerminal.SuppressIncomingData = true;
                        activated = true;
                        arm.Log(LogLevel.Warning, "UartRxBypass: activated after first data");
                    }

                    bus.WriteBytes(drainBuffer, bufferAddr, count);
                    arm.SetRegister((int)ArmRegisters.R0, RegisterValue.Create((uint)count, 32));
                    arm.PC = RegisterValue.Create((uint)readReturnAddr, 32);
                    totalBytes += count;
                    if(callCount <= 3 || callCount % 500 == 0)
                        arm.Log(LogLevel.Warning, "UartRxBypass: #{0} {1}B total={2}KB",
                            callCount, count, totalBytes / 1024);
                }
                else
                {
                    readBlocked = true;
                }
                // queue empty, fall through to read() which blocks on UART sem_wait.
                // When data arrives, DataReceived handler writes a wake byte to UART,
                // waking read(). read() returns the wake byte (0xFF — not real data).
                // The hook fires again on the NEXT read() and delivers the real data
                // from the queue.
            });

            arm.Log(LogLevel.Info, "UartRxBypass: installed at 0x{0:X8}", readCallAddr);
        }
    }
}
