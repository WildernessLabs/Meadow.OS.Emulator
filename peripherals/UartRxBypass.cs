// UART RX Bypass — compiled C# hook for high-throughput UART receive.
//
// Intercepts the firmware's read() call on the HCOM UART and copies data
// directly from the Renode UART model's receive queue to the firmware's
// read buffer via SystemBus, bypassing the per-byte interrupt-driven serial
// driver path that is the bottleneck for bulk data transfers under emulation.
//
// Without this bypass, each byte requires: RXNE interrupt -> ISR entry ->
// USART_SR read -> USART_DR read -> ISR exit (~3 register accesses per byte).
// At emulated CPU speed, 100KB takes ~5 minutes. With the bypass, the data
// is copied in one operation.
//
// Hook fires at the `bl read` instruction in hcom_host_recv_pthread.
// At hook entry: R0 = fd, R1 = buffer address, R2 = max count (8192)
// After bypass: R0 = bytes read (or 0 if no data → falls through to real read)
//
// Usage from .resc:
//   cpu InstallUartRxBypass <read_call_addr> <read_return_addr>

using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
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
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "UartRxBypass: CPU must be an ARM core");
            }

            // Find the UART4 peripheral (HCOM transport at 0x40004C00)
            STM32F7_USART uart4 = null;
            foreach(var entry in machine.GetRegisteredPeripherals())
            {
                if(entry.Peripheral is STM32F7_USART usart)
                {
                    var regPoint = entry.RegistrationPoint.ToString();
                    if(regPoint.Contains("40004C00") || (entry.Name != null && entry.Name.Contains("uart4")))
                    {
                        uart4 = usart;
                        break;
                    }
                }
            }
            if(uart4 == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "UartRxBypass: UART4 (STM32F7_USART at 0x40004C00) not found");
            }

            // Access the TCP socket backing UART4 via reflection on the connected terminal.
            // The data path normally is: TCP -> SocketServerProvider -> BackendTerminal ->
            // time domain event -> UART.WriteChar (per byte). We bypass this by reading
            // directly from the TCP socket when the UART queue is empty.
            Socket tcpSocket = null;
            try
            {
                // uart4 is connected to a ServerSocketTerminal; get its socket via reflection
                var uartType = uart4.GetType();
                // The BackendTerminal subscribes to uart4.CharReceived; we need to find
                // the terminal through the emulation's externals manager
                var emulation = Antmicro.Renode.Core.EmulationManager.Instance.CurrentEmulation;
                foreach(var name in new[] { "hcom" })
                {
                    Antmicro.Renode.Core.IExternal ext;
                    if(emulation.ExternalsManager.TryGetByName(name, out ext))
                    {
                        var serverField = ext.GetType().GetField("server",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if(serverField != null)
                        {
                            var provider = serverField.GetValue(ext);
                            var socketField = provider.GetType().GetField("socket",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if(socketField != null)
                            {
                                tcpSocket = socketField.GetValue(provider) as Socket;
                            }
                        }
                    }
                }
            }
            catch(Exception e)
            {
                arm.Log(LogLevel.Warning, "UartRxBypass: could not find TCP socket: {0}", e.Message);
            }
            // Redirect the SocketServerProvider's data stream: instead of feeding bytes
            // to the UART model (per-byte interrupts), buffer them in a ConcurrentQueue
            // that our hook drains directly into the firmware's read buffer.
            var rxQueue = new ConcurrentQueue<byte>();
            bool bypassActive = false;

            try
            {
                var emulation = Antmicro.Renode.Core.EmulationManager.Instance.CurrentEmulation;
                Antmicro.Renode.Core.IExternal ext;
                if(emulation.ExternalsManager.TryGetByName("hcom", out ext))
                {
                    var serverField = ext.GetType().GetField("server",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if(serverField != null)
                    {
                        var provider = serverField.GetValue(ext);
                        // Replace the DataReceived handler: instead of calling
                        // CallCharReceived (which feeds bytes one at a time through
                        // time domain events), enqueue bytes for the hook to drain.
                        var dataReceivedEvent = provider.GetType().GetEvent("DataReceived");
                        if(dataReceivedEvent != null)
                        {
                            // We can't easily remove the existing handler, but we CAN
                            // add our handler which puts bytes in the queue. The original
                            // handler still fires (feeding UART for non-bypassed operations).
                            // When bypass is active, we drain from our queue FIRST.
                            Action<int> handler = (int b) => { rxQueue.Enqueue((byte)b); };
                            dataReceivedEvent.AddEventHandler(provider, handler);
                            bypassActive = true;
                            arm.Log(LogLevel.Warning,
                                "UartRxBypass: data redirect installed (rxQueue)");
                        }
                    }
                }
            }
            catch(Exception e)
            {
                arm.Log(LogLevel.Warning, "UartRxBypass: redirect setup failed: {0}", e.Message);
            }

            var bus = machine.SystemBus;
            long callCount = 0;
            long totalBytes = 0;
            var drainBuffer = new byte[8192];

            cpu.AddHook(readCallAddr, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint bufferAddr = (uint)arm.GetRegister((int)ArmRegisters.R1).RawValue;
                int maxCount = (int)(uint)arm.GetRegister((int)ArmRegisters.R2).RawValue;
                if(maxCount > drainBuffer.Length) maxCount = drainBuffer.Length;

                int count = 0;

                // Drain from our intercept queue (filled by the SocketServerProvider's
                // reader thread, running at full TCP speed). Each byte is ~0 overhead
                // since we skip the entire UART interrupt path.
                if(bypassActive)
                {
                    byte b;
                    while(count < maxCount && rxQueue.TryDequeue(out b))
                    {
                        drainBuffer[count++] = b;
                    }
                }


                callCount++;
                if(callCount <= 5 || (count > 100 && callCount % 50 == 0) || callCount % 1000 == 0)
                {
                    arm.Log(LogLevel.Warning,
                        "UartRxBypass: call #{0}, {1} bytes, total {2}KB, queueLen={3}",
                        callCount, count, totalBytes / 1024, rxQueue.Count);
                }

                if(count > 0)
                {
                    bus.WriteBytes(drainBuffer, bufferAddr, count);
                    arm.SetRegister((int)ArmRegisters.R0,
                        RegisterValue.Create((uint)count, 32));
                    arm.PC = RegisterValue.Create((uint)readReturnAddr, 32);
                    totalBytes += count;
                }
                // If count == 0: no data available, let the real read() proceed
                // (it will block until data arrives via UART interrupt)
            });

            arm.Log(LogLevel.Info,
                "UartRxBypass: installed at 0x{0:X8}, return to 0x{1:X8}",
                readCallAddr, readReturnAddr);
        }
    }
}
