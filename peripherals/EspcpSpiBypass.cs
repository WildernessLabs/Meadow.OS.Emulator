// ESPCP SPI Bypass — compiled C# replacement for IronPython espcp_spi_bypass_dual hook.
//
// Intercepts espcp_send_data_over_spi (STM32 side) and performs SPI data exchange
// directly through the SpiBridge peripheral, bypassing the unimplemented STM32 SPI DMA.
//
// R0 = tx buffer (0 for receive-only), R1 = rx buffer (0 for send-only), R2 = length
// After exchange: PC = LR (return to caller)
//
// Usage from .resc (on stm32 machine):
//   cpu InstallEspcpSpiBypass 0x081488A0

using System;
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

            // Find the SPI peripheral (SpiBridge for dual-machine, EspcpStub for single-machine)
            ISPIPeripheral spiPeripheral = null;
            foreach(var entry in machine.GetRegisteredPeripherals())
            {
                var name = entry.Peripheral.GetType().Name;
                if(name == "SpiBridge" || name == "EspcpStub")
                {
                    spiPeripheral = entry.Peripheral as ISPIPeripheral;
                    break;
                }
            }
            if(spiPeripheral == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "EspcpSpiBypass: No SpiBridge or EspcpStub peripheral found");
            }

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

                    var rxarr = new byte[length];
                    for(int i = 0; i < length; i++)
                    {
                        rxarr[i] = bridge.Transmit(txdata[i]);
                    }
                    bridge.FinishTransmission();

                    if(rx != 0)
                    {
                        bus.WriteBytes(rxarr, rx);
                    }

                    // Log non-poll SPI exchanges for debugging
                    if(tx != 0 && txdata.Length > 1 && txdata[0] != 0x00)
                    {
                        bool isSendResponse = (txdata[0] == 0x20 && txdata[1] == 0x05);
                        if(!isSendResponse)
                        {
                            arm.Log(LogLevel.Info,
                                "EspcpSpiBypass: #{0} NON-POLL type=0x{1:X2} iface={2} len={3} LR=0x{4:X8}",
                                callCount + 1, txdata[0], txdata[1], length, arm.LR.RawValue);
                        }
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
