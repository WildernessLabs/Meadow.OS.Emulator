// UART RX Bypass — disables UART RXNE interrupt when queue has data,
// preventing duplicate delivery. Re-enables when queue is empty.
using System;
using System.Collections.Concurrent;
using Antmicro.Renode.Core;
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

            // UART4 CR1 register address (for RXNEIE control)
            const uint UART4_BASE = 0x40004C00;
            const uint UART4_CR1 = UART4_BASE + 0x0C;
            const uint RXNEIE_BIT = 1u << 5;

            // Find UART4 for DrainReceiveQueue
            STM32F7_USART uart4 = null;
            foreach(var entry in machine.GetRegisteredPeripherals())
            {
                if(entry.Peripheral is STM32F7_USART usart)
                {
                    var rp = entry.RegistrationPoint.ToString();
                    if(rp.Contains("40004C00") || (entry.Name != null && entry.Name.Contains("uart4")))
                    { uart4 = usart; break; }
                }
            }
            if(uart4 == null)
                throw new Antmicro.Renode.Exceptions.RecoverableException("UartRxBypass: UART4 not found");

            // Add queue handler alongside original DataReceived
            var rxQueue = new ConcurrentQueue<byte>();
            bool bypassActive = false;
            try
            {
                var emulation = EmulationManager.Instance.CurrentEmulation;
                IExternal ext;
                if(emulation.ExternalsManager.TryGetByName("hcom", out ext))
                {
                    var field = ext.GetType().GetField("server",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if(field != null)
                    {
                        var provider = field.GetValue(ext) as SocketServerProvider;
                        if(provider != null)
                        {
                            provider.DataReceived += (int b) => rxQueue.Enqueue((byte)b);
                            bypassActive = true;
                        }
                    }
                }
            }
            catch(Exception e)
            {
                arm.Log(LogLevel.Warning, "UartRxBypass: setup failed: {0}", e.Message);
            }

            var bus = machine.SystemBus;
            var drainBuffer = new byte[8192];
            long callCount = 0;
            long totalBytes = 0;

            cpu.AddHook(readCallAddr, (ICpuSupportingGdb c, ulong pc) =>
            {
                if(!bypassActive) return; // fall through to real read()

                uint bufferAddr = (uint)arm.GetRegister((int)ArmRegisters.R1).RawValue;
                int maxCount = (int)(uint)arm.GetRegister((int)ArmRegisters.R2).RawValue;
                if(maxCount > drainBuffer.Length) maxCount = drainBuffer.Length;

                // Drain queue
                int count = 0;
                byte b;
                while(count < maxCount && rxQueue.TryDequeue(out b))
                    drainBuffer[count++] = b;

                callCount++;

                if(count > 0)
                {
                    // Disable RXNE interrupt to prevent UART from delivering duplicates
                    uint cr1 = bus.ReadDoubleWord(UART4_CR1);
                    bus.WriteDoubleWord(UART4_CR1, cr1 & ~RXNEIE_BIT);

                    // Deliver data directly
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
                    // Queue empty — re-enable RXNE so firmware can read from UART normally
                    uint cr1 = bus.ReadDoubleWord(UART4_CR1);
                    if((cr1 & RXNEIE_BIT) == 0)
                    {
                        bus.WriteDoubleWord(UART4_CR1, cr1 | RXNEIE_BIT);
                        // Drain any accumulated UART bytes to prevent stale reads
                        uart4.DrainReceiveQueue(drainBuffer, drainBuffer.Length);
                    }
                    // Fall through to real read() — blocks on UART interrupt as normal
                }
            });

            arm.Log(LogLevel.Info, "UartRxBypass: installed at 0x{0:X8}", readCallAddr);
        }
    }
}
