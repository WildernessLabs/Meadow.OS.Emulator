// ESP32 DPORT Register — system control + interrupt matrix
//
// Handles:
// 1. Cache flush/lock status bits (firmware polls these during init)
// 2. PRO CPU interrupt matrix: maps peripheral interrupt sources to CPU interrupt lines
//
// Interrupt matrix: 69 peripheral sources, each with a 5-bit mapping register
// at offsets 0x104-0x214 (PRO CPU). When a peripheral asserts its source GPIO,
// the matrix looks up the mapped CPU interrupt number and asserts that line on the CPU.
// Multiple sources can map to the same CPU interrupt (level-OR'd).

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32_DPORT : IDoubleWordPeripheral, IKnownSize, IGPIOReceiver
    {
        public ESP32_DPORT(IMachine machine)
        {
            this.machine = machine;
            backingStore = new Dictionary<long, uint>();
            interruptMap = new int[NumSources];
            sourceActive = new bool[NumSources];

            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            backingStore.TryGetValue(offset, out var value);

            switch(offset)
            {
                case ProCacheCtrlOffset:
                case ProCacheCtrl1Offset:
                    value |= CacheFlushDoneBit;
                    break;
                case AppCacheCtrlOffset:
                case AppCacheCtrl1Offset:
                    value |= CacheFlushDoneBit;
                    break;
            }

            // Interrupt map registers: return the mapped CPU interrupt number
            if(offset >= ProIntrMapStart && offset <= ProIntrMapEnd)
            {
                int source = (int)(offset - ProIntrMapStart) / 4;
                if(source >= 0 && source < NumSources)
                {
                    return (uint)interruptMap[source];
                }
            }

            return value;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            backingStore[offset] = value;

            // Interrupt map registers: update the source→CPU interrupt mapping
            if(offset >= ProIntrMapStart && offset <= ProIntrMapEnd)
            {
                int source = (int)(offset - ProIntrMapStart) / 4;
                if(source >= 0 && source < NumSources)
                {
                    int cpuInt = (int)(value & 0x1F);
                    int oldCpuInt = interruptMap[source];
                    interruptMap[source] = cpuInt;

                    this.Log(LogLevel.Debug,
                        "DPORT: Interrupt source {0} mapped to CPU interrupt {1} (was {2})",
                        source, cpuInt, oldCpuInt);

                    // If this source is currently active, update the CPU interrupt lines
                    if(sourceActive[source])
                    {
                        // Deassert old mapping
                        if(oldCpuInt != cpuInt)
                        {
                            UpdateCpuInterrupt(oldCpuInt);
                        }
                        // Assert new mapping
                        UpdateCpuInterrupt(cpuInt);
                    }
                }
            }
        }

        // ── IGPIOReceiver — peripheral interrupt sources ────────────────────

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= NumSources)
            {
                this.Log(LogLevel.Warning, "DPORT: Invalid interrupt source {0}", number);
                return;
            }

            bool wasActive = sourceActive[number];
            sourceActive[number] = value;

            if(value != wasActive)
            {
                int cpuInt = interruptMap[number];
                this.Log(LogLevel.Debug,
                    "DPORT: Source {0} {1}, mapped to CPU interrupt {2}",
                    number, value ? "asserted" : "deasserted", cpuInt);
                UpdateCpuInterrupt(cpuInt);
            }
        }

        // ── Reset ───────────────────────────────────────────────────────────

        public void Reset()
        {
            backingStore.Clear();
            // Default mapping: all sources map to CPU interrupt 16 (disabled/unused)
            for(int i = 0; i < NumSources; i++)
            {
                interruptMap[i] = DefaultCpuInterrupt;
                sourceActive[i] = false;
            }
        }

        public long Size => 0x1000;

        // ── Internal ────────────────────────────────────────────────────────

        /// <summary>
        /// Recalculate the state of a CPU interrupt line based on all sources
        /// currently mapped to it. Level-OR: asserted if ANY active source maps to it.
        /// Uses TlibSetIrqPendingBit on the CPU instead of GPIO.Set to avoid
        /// multi-driver conflicts (timer CCOMPARE0 also drives interrupt 6 directly).
        /// </summary>
        private void UpdateCpuInterrupt(int cpuInt)
        {
            if(cpuInt < 0 || cpuInt >= NumCpuInterrupts)
                return;

            // Check if any active source maps to this CPU interrupt
            bool shouldAssert = false;
            for(int src = 0; src < NumSources; src++)
            {
                if(sourceActive[src] && interruptMap[src] == cpuInt)
                {
                    shouldAssert = true;
                    break;
                }
            }

            // Use OnGPIO on the CPU to set/clear the interrupt pending bit.
            // This goes through TlibSetIrqPendingBit which correctly handles
            // INTSET (level-OR with timer sources that latch).
            if(cpu == null)
            {
                // Find the CPU on first use
                foreach(var c in machine.SystemBus.GetCPUs())
                {
                    cpu = c as IGPIOReceiver;
                    break;
                }
            }
            cpu?.OnGPIO(cpuInt, shouldAssert);
        }

        private IMachine machine;
        private IGPIOReceiver cpu;

        // ── Constants ───────────────────────────────────────────────────────

        private const long ProCacheCtrlOffset  = 0x040;
        private const long ProCacheCtrl1Offset = 0x044;
        private const long AppCacheCtrlOffset  = 0x058;
        private const long AppCacheCtrl1Offset = 0x05C;
        private const uint CacheFlushDoneBit = 1u << 5;

        // PRO CPU interrupt map registers: 0x104 to 0x214 (69 sources × 4 bytes)
        private const long ProIntrMapStart = 0x104;
        private const long ProIntrMapEnd   = 0x214;
        private const int NumSources = 69;
        private const int NumCpuInterrupts = 32;
        private const int DefaultCpuInterrupt = 16; // ESP32 default: unmapped

        // ── State ───────────────────────────────────────────────────────────

        private readonly Dictionary<long, uint> backingStore;
        private readonly int[] interruptMap;     // source[0..68] → CPU interrupt number
        private readonly bool[] sourceActive;    // source[0..68] → currently asserted?
    }
}
