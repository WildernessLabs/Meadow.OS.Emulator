// SVC PRIMASK Fix — C# replacement for the IronPython svc_fix hook.
//
// Renode skips SVC instructions when PRIMASK=1 instead of escalating to
// HardFault as real ARM hardware does. NuttX sets PRIMASK=1 in critical
// sections and then executes SVC to dispatch syscalls. This hook clears
// PRIMASK before each SVC instruction so Renode processes it correctly.
//
// This is critical for Mono JIT performance: during assembly loading Mono
// makes thousands of syscalls per second. Each IronPython hook invocation
// adds ~3ms overhead, throttling the system to ~300 syscalls/sec. The C#
// version eliminates this bottleneck.
//
// Usage from .resc:
//   cpu InstallSvcFix 0x08163b5a 0x08163da6 0x08163db6 ...

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class SvcPrimaskFixExtension
    {
        public static void InstallSvcFix(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, params ulong[] addresses)
        {
            var cortexM = cpu as CortexM;
            if(cortexM == null)
            {
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "SvcPrimaskFix: CPU must be a Cortex-M core");
            }

            long totalHits = 0;
            var startTime = DateTime.UtcNow;

            foreach(var addr in addresses)
            {
                cpu.AddHook(addr, (ICpuSupportingGdb cpuArg, ulong pc) =>
                {
                    cortexM.PRIMASK = 0;
                    totalHits++;

                    if(totalHits % 50000 == 0)
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        cortexM.Log(LogLevel.Info,
                            "SvcPrimaskFix: {0} hits, {1:F1}s wall time, {2:F0} hits/sec",
                            totalHits, elapsed, totalHits / elapsed);
                    }
                });
            }

            cortexM.Log(LogLevel.Info,
                "SvcPrimaskFix: C# hook installed at {0} SVC addresses", addresses.Length);
        }
    }
}
