// ESP32 DPORT Register Stub — handles cache flush/lock status bits
// and other system control registers that firmware polls.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32_DPORT : IDoubleWordPeripheral, IKnownSize
    {
        public ESP32_DPORT()
        {
            registers = new Dictionary<long, uint>();
        }

        public uint ReadDoubleWord(long offset)
        {
            uint value;
            registers.TryGetValue(offset, out value);

            switch(offset)
            {
                case ProCacheCtrlOffset:
                case ProCacheCtrl1Offset:
                    // Set PRO_CACHE_FLUSH_DONE (bit 5) — cache flush always complete
                    value |= CacheFlushDoneBit;
                    break;
                case AppCacheCtrlOffset:
                case AppCacheCtrl1Offset:
                    // Set APP_CACHE_FLUSH_DONE (bit 5)
                    value |= CacheFlushDoneBit;
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

        private const long ProCacheCtrlOffset  = 0x040;  // DPORT_PRO_CACHE_CTRL_REG
        private const long ProCacheCtrl1Offset = 0x044;  // DPORT_PRO_CACHE_CTRL1_REG
        private const long AppCacheCtrlOffset  = 0x058;  // DPORT_APP_CACHE_CTRL_REG
        private const long AppCacheCtrl1Offset = 0x05C;  // DPORT_APP_CACHE_CTRL1_REG
        private const uint CacheFlushDoneBit = 1u << 5;  // CACHE_FLUSH_DONE

        private readonly Dictionary<long, uint> registers;
    }
}
