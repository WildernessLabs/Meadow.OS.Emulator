// SysTick rate taken from the firmware's reload value.
//
// Both Meadow OSes run SysTick from the implementation-defined clock, HCLK / 8
// (CLKSOURCE = 0), and load RELOAD = (HCLK / 8 / tick rate) - 1. Renode's NVIC
// ignores CLKSOURCE and always counts at systickFrequency, so with the platform's
// 216 MHz a build expecting 24 MHz (192 MHz / 8) ticked 9 times too fast: sleeps,
// timeouts and the clock all ran ahead of virtual time. HCLK depends on the build,
// so this watches the firmware's write to RELOAD and sets the count rate that makes
// one reload last one OS tick.
//
// Usage from .resc, after the platform is loaded:
//     sysbus.nvic FollowSysTickReload [microseconds per tick, default 1000]
//
// The default is CONFIG_USEC_PER_TICK in both OSes.

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class SysTickFromReloadExtension
    {
        public static void FollowSysTickReload(this NVIC nvic, [AutoParameter] IMachine machine, ulong microsecondsPerTick = 1000)
        {
            if(microsecondsPerTick == 0)
            {
                throw new RecoverableException("FollowSysTickReload: microsecondsPerTick must not be 0");
            }

            machine.SystemBus.AddWatchpointHook(SysTickReloadAddress, SysbusAccessWidth.DoubleWord, Access.Write,
                (ICpuSupportingGdb cpu, ulong address, SysbusAccessWidth width, ulong value) =>
                {
                    var reload = value & 0xFFFFFF;
                    if(reload == 0)
                    {
                        return;
                    }
                    var frequency = (reload + 1) * 1000000 / microsecondsPerTick;
                    if(nvic.Frequency != frequency)
                    {
                        nvic.Log(LogLevel.Info, "SysTick: RELOAD {0}, counting at {1} Hz for a {2} us tick",
                            reload, frequency, microsecondsPerTick);
                        nvic.Frequency = frequency;
                    }
                });
        }

        private const ulong SysTickReloadAddress = 0xE000E014;
    }
}
