// ESP32 Hot-Path Hooks — compiled C# replacements for IronPython hooks
//
// Migrates the highest-frequency functional hooks from IronPython to compiled
// C# for 10-100x speedup. These hooks fix FreeRTOS data structure corruption
// caused by Xtensa tlib translation bugs (TCG stale-globals, window rotation
// edge cases) during ESP32 emulation.
//
// Usage from .resc (on ESP32 machine):
//   cpu InstallDispatchFixup 0x40091360
//   cpu InstallVListInsertEndGuard 0x400941bb
//   cpu InstallLockValidate 0x40082c14
//   cpu InstallFixPdA8 0x4009f031
//   cpu InstallXqrDiag 0x40091cb8
//   cpu InstallQueueRecvSchedCheck 0x40091cf8 0x40091d83
//   cpu InstallThreadTaskGuard 0x400e154c 0x400e154f
//   cpu InstallSetRegZero 0x40157d12 99

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class XtensaHotPathHooksExtension
    {
        // Xtensa register indices (from XtensaRegisters.cs CPURegister mapping)
        private const int REG_PC  = 0;
        private const int REG_A0  = 89;
        private const int REG_A2  = 91;
        private const int REG_A3  = 92;
        private const int REG_A8  = 97;
        private const int REG_A10 = 99;

        // SRAM valid address range for ESP32
        private const uint SRAM_LOW  = 0x3FF00000;
        private const uint SRAM_HIGH = 0x40000000;

        private static uint GetReg(ICpuSupportingGdb cpu, int regIdx)
        {
            return (uint)cpu.GetRegister(regIdx).RawValue;
        }

        private static void SetReg(ICpuSupportingGdb cpu, int regIdx, uint value)
        {
            cpu.SetRegister(regIdx, RegisterValue.Create(value, 32));
        }

        private static void LogInstall(ICPUWithHooks cpu, string name, ulong address)
        {
            cpu.Log(LogLevel.Info,
                "XtensaHotPathHooks: {0} installed at 0x{1:X8}", name, address);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _dispatch_fixup — fires on every vTaskSwitchContext
        //
        // 1. Demotes IPC tasks from priority 24→0 (up to 6 tasks)
        // 2. Sets xSchedulerRunning = 1
        // 3. Fixes corrupt pxIndex pointers in FreeRTOS ready lists
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallDispatchFixup(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress)
        {
            var bus = machine.SystemBus;
            int totalDemoted = 0;
            bool schedulerSet = false;

            const uint ReadyListBase = 0x3FFC9384;
            const uint TopReadyPriority = 0x3FFC92EC;
            const uint SchedulerRunning = 0x3FFC92BC;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                // Demote IPC tasks from priority 24 ready list to priority 0
                if(totalDemoted < 6)
                {
                    uint rl24 = ReadyListBase + 24 * 20;
                    uint cnt24 = bus.ReadDoubleWord(rl24);
                    if(cnt24 > 0)
                    {
                        uint sentinel24 = rl24 + 8;
                        uint item = bus.ReadDoubleWord(rl24 + 12);
                        for(int iter = 0; iter <= cnt24 && totalDemoted < 6; iter++)
                        {
                            if(item == sentinel24 || item == 0)
                                break;
                            uint nextItem = bus.ReadDoubleWord(item + 4);
                            uint tcb = bus.ReadDoubleWord(item + 12);
                            uint pri = bus.ReadDoubleWord(tcb + 48);
                            if(pri == 24)
                            {
                                bus.WriteDoubleWord(tcb + 48, 0);
                                uint nx = bus.ReadDoubleWord(item + 4);
                                uint pv = bus.ReadDoubleWord(item + 8);
                                bus.WriteDoubleWord(nx + 8, pv);
                                bus.WriteDoubleWord(pv + 4, nx);
                                uint pi = bus.ReadDoubleWord(rl24 + 4);
                                if(pi == item)
                                    bus.WriteDoubleWord(rl24 + 4, pv);
                                bus.WriteDoubleWord(item + 16, 0);
                                bus.WriteDoubleWord(rl24, bus.ReadDoubleWord(rl24) - 1);
                                uint rl0 = ReadyListBase;
                                uint ri = bus.ReadDoubleWord(rl0 + 4);
                                uint rp = bus.ReadDoubleWord(ri + 8);
                                bus.WriteDoubleWord(item + 4, ri);
                                bus.WriteDoubleWord(item + 8, rp);
                                bus.WriteDoubleWord(rp + 4, item);
                                bus.WriteDoubleWord(ri + 8, item);
                                bus.WriteDoubleWord(item + 16, rl0);
                                bus.WriteDoubleWord(rl0, bus.ReadDoubleWord(rl0) + 1);
                                totalDemoted++;
                                c.Log(LogLevel.Error,
                                    "DEMOTED IPC task #{0} TCB=0x{1:X} from pri24 to pri0", totalDemoted, tcb);
                            }
                            item = nextItem;
                        }
                        if(bus.ReadDoubleWord(rl24) == 0)
                        {
                            for(int p = 24; p >= 0; p--)
                            {
                                if(bus.ReadDoubleWord(ReadyListBase + (uint)p * 20) > 0)
                                {
                                    bus.WriteDoubleWord(TopReadyPriority, (uint)p);
                                    break;
                                }
                            }
                        }
                    }
                }

                if(!schedulerSet)
                {
                    schedulerSet = true;
                    bus.WriteDoubleWord(SchedulerRunning, 1);
                }

                for(int p = 0; p < 25; p++)
                {
                    uint listBase = ReadyListBase + (uint)p * 20;
                    uint pi = bus.ReadDoubleWord(listBase + 4);
                    if(pi < SRAM_LOW || pi > SRAM_HIGH)
                    {
                        bus.WriteDoubleWord(listBase + 4, listBase + 8);
                    }
                }
            });

            LogInstall(cpu, "InstallDispatchFixup", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _vListInsertEnd_guard — fires on every vListInsertEnd call
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallVListInsertEndGuard(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress)
        {
            var bus = machine.SystemBus;
            long fixCount = 0;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint a2 = GetReg(c, REG_A2);
                if(a2 < SRAM_LOW || a2 >= SRAM_HIGH)
                    return;

                uint pxIndex = bus.ReadDoubleWord(a2 + 4);
                if(pxIndex < SRAM_LOW || pxIndex >= SRAM_HIGH)
                {
                    bus.WriteDoubleWord(a2 + 4, a2 + 8);
                    fixCount++;
                    if(fixCount <= 10)
                        c.Log(LogLevel.Warning,
                            "vListInsertEnd: corrupt pxIndex=0x{0:X8}, fixed (#{1})", pxIndex, fixCount);
                    return;
                }

                uint pxNext = bus.ReadDoubleWord(pxIndex + 8);
                if(pxNext < SRAM_LOW || pxNext >= SRAM_HIGH)
                {
                    bus.WriteDoubleWord(pxIndex + 8, a2 + 8);
                    fixCount++;
                    if(fixCount <= 10)
                        c.Log(LogLevel.Warning,
                            "vListInsertEnd: corrupt pxNext=0x{0:X8}, fixed (#{1})", pxNext, fixCount);
                }
            });

            LogInstall(cpu, "InstallVListInsertEndGuard", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _lock_validate — fires on every lock/mutex operation
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallLockValidate(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress)
        {
            var bus = machine.SystemBus;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint a10 = GetReg(c, REG_A10);
                uint a2 = GetReg(c, REG_A2);

                uint lockptr = a10;
                if(lockptr < SRAM_LOW || lockptr >= SRAM_HIGH)
                    lockptr = a2;
                if(lockptr < SRAM_LOW || lockptr >= SRAM_HIGH)
                    return;

                uint handle = bus.ReadDoubleWord(lockptr);
                if(handle == 0)
                    return;
                if(handle < SRAM_LOW || handle >= SRAM_HIGH)
                {
                    bus.WriteDoubleWord(a2, 0);
                    return;
                }

                uint wts_pi = bus.ReadDoubleWord(handle + 20);
                uint wtr_pi = bus.ReadDoubleWord(handle + 40);
                if(wts_pi == 0 && wtr_pi == 0)
                {
                    bus.WriteDoubleWord(a2, 0);
                }
            });

            LogInstall(cpu, "InstallLockValidate", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _fix_pd_a8 — fixes TCG stale-global bug in spi_slave_hal_prepare_data
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallFixPdA8(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress)
        {
            var bus = machine.SystemBus;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint a2 = GetReg(c, REG_A2);
                if(a2 < SRAM_LOW || a2 >= SRAM_HIGH)
                    return;

                uint hw = bus.ReadDoubleWord(a2);
                uint a8 = GetReg(c, REG_A8);
                if(a8 != hw && hw >= SRAM_LOW)
                {
                    SetReg(c, REG_A8, hw);
                }
            });

            LogInstall(cpu, "InstallFixPdA8", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _xqr_diag — initializes uninitialized queue structures
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallXqrDiag(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress)
        {
            var bus = machine.SystemBus;
            long fixCount = 0;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint q = GetReg(c, REG_A10);
                if(q < SRAM_LOW || q >= SRAM_HIGH)
                    return;

                uint wts_pi = bus.ReadDoubleWord(q + 20);
                uint wtr_pi = bus.ReadDoubleWord(q + 40);
                if(wts_pi != 0 || wtr_pi != 0)
                    return;

                fixCount++;
                c.Log(LogLevel.Error, "XQR_FIX #{0}: queue=0x{1:X8} uninit", fixCount, q);

                uint wts = q + 16;
                bus.WriteDoubleWord(wts, 0);
                bus.WriteDoubleWord(wts + 4, wts + 8);
                bus.WriteDoubleWord(wts + 8, 0xFFFFFFFF);
                bus.WriteDoubleWord(wts + 12, wts + 8);
                bus.WriteDoubleWord(wts + 16, wts + 8);

                uint wtr = q + 36;
                bus.WriteDoubleWord(wtr, 0);
                bus.WriteDoubleWord(wtr + 4, wtr + 8);
                bus.WriteDoubleWord(wtr + 8, 0xFFFFFFFF);
                bus.WriteDoubleWord(wtr + 12, wtr + 8);
                bus.WriteDoubleWord(wtr + 16, wtr + 8);

                bus.WriteDoubleWord(q, q + 80);
                bus.WriteDoubleWord(q + 4, q + 80);
                bus.WriteDoubleWord(q + 8, q + 80);
                bus.WriteDoubleWord(q + 12, 0);
                bus.WriteDoubleWord(q + 56, 0);
                bus.WriteDoubleWord(q + 60, 1);
                bus.WriteDoubleWord(q + 64, 0);
            });

            LogInstall(cpu, "InstallXqrDiag", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _queue_recv_sched_check — redirects PC when scheduler not running
        // Python code: _cpu.SetRegister(0, ...) where 0 = PC register index
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallQueueRecvSchedCheck(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress, ulong targetPC)
        {
            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint a10 = GetReg(c, REG_A10);
                if(a10 != 2)
                {
                    SetReg(c, REG_PC, (uint)targetPC);
                }
            });

            LogInstall(cpu, "InstallQueueRecvSchedCheck", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _thread_task_callx8_guard — guards against NULL function pointers
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallThreadTaskGuard(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress, ulong skipTarget)
        {
            long nullCount = 0;

            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                uint a3 = GetReg(c, REG_A3);
                if(a3 == 0 || a3 < 0x40000000 || a3 >= 0x40200000)
                {
                    nullCount++;
                    if(nullCount <= 5)
                        c.Log(LogLevel.Warning,
                            "Thread::Task: null method a3=0x{0:X8}, skip (#{1})", a3, nullCount);
                    SetReg(c, REG_PC, (uint)skipTarget);
                }
            });

            LogInstall(cpu, "InstallThreadTaskGuard", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // _set_a10_zero — simple register zeroing
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallSetRegZero(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress, int regIdx)
        {
            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                c.SetRegister(regIdx, RegisterValue.Create(0, 32));
            });

            LogInstall(cpu, "InstallSetRegZero", hookAddress);
        }

        // ─────────────────────────────────────────────────────────────────────
        // IdleSkip — makes esp_vApplicationIdleHook return immediately
        //
        // The ESP32 idle task spends ~90% of host CPU time looping through
        // idle hook callbacks (watchdog reset, etc.) that aren't needed in
        // emulation. This hook skips the callback loop by returning immediately
        // via RETW (Xtensa windowed return), saving the host from translating
        // millions of idle instructions per second.
        //
        // The CCOMPARE0 timer still fires at 100Hz to drive FreeRTOS ticks,
        // and the idle task loop naturally calls this function again.
        // ─────────────────────────────────────────────────────────────────────
        public static void InstallIdleSkip(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine, ulong hookAddress, ulong retwAddress)
        {
            // Hook at the WAITI instruction inside esp_pm_impl_waiti.
            // When the idle task reaches WAITI, we halt the CPU entirely.
            // The CCOMPARE0 timer interrupt will unhalt it every 10ms virtual.
            // This prevents the ESP32 from burning host CPU in the idle loop.
            cpu.AddHook(hookAddress, (ICpuSupportingGdb c, ulong pc) =>
            {
                cpu.IsHalted = true;
            });

            LogInstall(cpu, "InstallIdleSkip (halt on WAITI)", hookAddress);
        }
    }
}
