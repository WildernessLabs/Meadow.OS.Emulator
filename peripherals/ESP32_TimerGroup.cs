// ESP32 Timer Group Stub — handles RTC calibration ready bit
// that firmware busy-waits on during clock initialization.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Timers
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32_TimerGroup : IDoubleWordPeripheral, IKnownSize
    {
        public ESP32_TimerGroup()
        {
            registers = new Dictionary<long, uint>();
        }

        public uint ReadDoubleWord(long offset)
        {
            uint value;
            registers.TryGetValue(offset, out value);

            switch(offset)
            {
                case RtcCaliCfgOffset:
                    // Always report RTC_CALI_RDY (bit 15) as set
                    value |= RtcCaliRdyBit;
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

        private const long RtcCaliCfgOffset = 0x68;  // TIMG_RTCCALICFG_REG
        private const uint RtcCaliRdyBit = 1u << 15; // TIMG_RTC_CALI_RDY

        private readonly Dictionary<long, uint> registers;
    }
}
