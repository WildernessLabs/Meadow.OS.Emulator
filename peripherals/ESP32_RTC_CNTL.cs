// ESP32 RTC_CNTL Stub — handles clock/calibration status bits
// that firmware busy-waits on during startup.
//
// Without this, the firmware loops forever in rtc_clk_init() because
// MappedMemory never auto-sets the "ready" / "valid" status bits.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32_RTC_CNTL : IDoubleWordPeripheral, IKnownSize
    {
        public ESP32_RTC_CNTL()
        {
            registers = new Dictionary<long, uint>();
        }

        public uint ReadDoubleWord(long offset)
        {
            uint value;
            registers.TryGetValue(offset, out value);

            switch(offset)
            {
                case TimeUpdateRegOffset:
                    // Always report TIME_VALID (bit 30) as set
                    value |= TimeValidBit;
                    break;
            }

            return value;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers[offset] = value;
        }

        public void Reset()
        {
            registers.Clear();
        }

        public long Size => 0x1000;

        private const long TimeUpdateRegOffset = 0x0C;  // RTC_CNTL_TIME_UPDATE_REG
        private const uint TimeValidBit = 1u << 30;     // RTC_CNTL_TIME_VALID

        private readonly Dictionary<long, uint> registers;
    }
}
