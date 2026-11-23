// ESPCP SPI Bypass — compiled C# replacement for IronPython espcp_spi_bypass_dual hook.
//
// Intercepts espcp_send_data_over_spi (STM32 side) and performs SPI data exchange
// directly through the SpiBridge peripheral, bypassing the unimplemented STM32 SPI DMA.
// With EspEmuBridge (the ESP in esp-emu) the whole buffer goes in one transfer through
// its TransferBuffer method, rather than one byte at a time.
//
// R0 = tx buffer (0 for receive-only), R1 = rx buffer (0 for send-only), R2 = length
// After exchange: PC = LR (return to caller)
//
// Usage from .resc (on stm32 machine):
//   cpu InstallEspcpSpiBypass 0x081488A0

using System;
using System.Reflection;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class EspcpSpiBypassExtension
    {
        public static void InstallEspcpSpiBypass(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress)
        {
            var arm = cpu as Arm;
            if(arm == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "EspcpSpiBypass: CPU must be an ARM core");
            }

            // Find the SPI peripheral (SpiBridge for dual-machine, EspEmuBridge for esp-emu,
            // EspcpStub for single-machine)
            ISPIPeripheral spiPeripheral = null;
            foreach(var entry in machine.GetRegisteredPeripherals())
            {
                var name = entry.Peripheral.GetType().Name;
                if(name == "SpiBridge" || name == "EspEmuBridge" || name == "EspcpStub")
                {
                    spiPeripheral = entry.Peripheral as ISPIPeripheral;
                    break;
                }
            }
            if(spiPeripheral == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "EspcpSpiBypass: No SpiBridge, EspEmuBridge or EspcpStub peripheral found");
            }

            // EspEmuBridge is compiled on its own, so its whole-buffer transfer is found by name.
            var transferBuffer = spiPeripheral.GetType().GetMethod("TransferBuffer",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]) }, null);

            var bridge = spiPeripheral;
            var bus = machine.SystemBus;
            long callCount = 0;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint tx = (uint)arm.GetRegister((int)ArmRegisters.R0).RawValue;
                uint rx = (uint)arm.GetRegister((int)ArmRegisters.R1).RawValue;
                int length = (int)(uint)arm.GetRegister((int)ArmRegisters.R2).RawValue;

                if(length > 0)
                {
                    byte[] txdata;
                    if(tx != 0)
                    {
                        txdata = bus.ReadBytes(tx, length);
                    }
                    else
                    {
                        txdata = new byte[length];
                    }

                    byte[] rxarr;
                    if(transferBuffer != null)
                    {
                        rxarr = (byte[])transferBuffer.Invoke(bridge, new object[] { txdata });
                    }
                    else
                    {
                        rxarr = new byte[length];
                        for(int i = 0; i < length; i++)
                        {
                            rxarr[i] = bridge.Transmit(txdata[i]);
                        }
                        bridge.FinishTransmission();
                    }

                    if(rx != 0)
                    {
                        bus.WriteBytes(rxarr, rx);
                    }
                }

                callCount++;
                if(callCount % 10000 == 0)
                {
                    arm.Log(LogLevel.Info,
                        "EspcpSpiBypass: {0} calls", callCount);
                }

                // Return from function
                arm.PC = arm.LR;
            });

            arm.Log(LogLevel.Info,
                "EspcpSpiBypass: C# hook installed at 0x{0:X8}", hookAddress);
        }
    }
}
