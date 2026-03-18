// STM32F7 RCC peripheral for Renode
// Replaces the IronPython rcc.py stub with a compiled C# peripheral.
// Auto-sets clock ready bits when enable bits are written (instant startup).
// Mirrors SW -> SWS in RCC_CFGR.

using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32F7_RCC_Custom : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32F7_RCC_Custom(IMachine machine) : base(machine)
        {
            DefineRegisters();
            Reset();
        }

        public long Size => 0x400;

        public override uint ReadDoubleWord(long offset)
        {
            if(RegistersCollection.HasRegisterAtOffset(offset))
            {
                return RegistersCollection.Read(offset);
            }

            // Fallback: return stored value for undefined registers
            uint val;
            if(undefinedRegisters.TryGetValue(offset, out val))
            {
                return val;
            }
            return 0;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            if(RegistersCollection.HasRegisterAtOffset(offset))
            {
                RegistersCollection.Write(offset, value);
                return;
            }

            // Fallback: store value for undefined registers
            undefinedRegisters[offset] = value;
        }

        public override void Reset()
        {
            undefinedRegisters.Clear();
            base.Reset();
        }

        private void DefineRegisters()
        {
            // RCC_CR (0x00) — Clock Control Register
            // Reset value: 0x00000003 (HSION=1, HSIRDY=1)
            Registers.CR.Define(this, 0x00000003)
                .WithFlag(0, out hsiOn, name: "HSION")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => hsiOn.Value, name: "HSIRDY")
                .WithReservedBits(2, 1)
                .WithFlag(3, name: "HSITRIM0")  // HSI trimming bits [3:7]
                .WithValueField(4, 4, name: "HSITRIM")
                .WithValueField(8, 8, name: "HSICAL")
                .WithFlag(16, out hseOn, name: "HSEON")
                .WithFlag(17, FieldMode.Read, valueProviderCallback: _ => hseOn.Value, name: "HSERDY")
                .WithFlag(18, name: "HSEBYP")
                .WithFlag(19, name: "CSSON")
                .WithReservedBits(20, 4)
                .WithFlag(24, out pllOn, name: "PLLON")
                .WithFlag(25, FieldMode.Read, valueProviderCallback: _ => pllOn.Value, name: "PLLRDY")
                .WithFlag(26, out plli2sOn, name: "PLLI2SON")
                .WithFlag(27, FieldMode.Read, valueProviderCallback: _ => plli2sOn.Value, name: "PLLI2SRDY")
                .WithFlag(28, out pllsaiOn, name: "PLLSAION")
                .WithFlag(29, FieldMode.Read, valueProviderCallback: _ => pllsaiOn.Value, name: "PLLSAIRDY")
                .WithReservedBits(30, 2)
            ;

            // RCC_PLLCFGR (0x04) — PLL Configuration Register
            // Reset value: 0x24003010
            Registers.PLLCFGR.Define(this, 0x24003010)
                .WithValueField(0, 6, name: "PLLM")
                .WithValueField(6, 9, name: "PLLN")
                .WithReservedBits(15, 1)
                .WithValueField(16, 2, name: "PLLP")
                .WithReservedBits(18, 4)
                .WithFlag(22, name: "PLLSRC")
                .WithReservedBits(23, 1)
                .WithValueField(24, 4, name: "PLLQ")
                .WithReservedBits(28, 4)
            ;

            // RCC_CFGR (0x08) — Clock Configuration Register
            // SW (bits 1:0) mirrored to SWS (bits 3:2)
            Registers.CFGR.Define(this)
                .WithValueField(0, 2, out systemClockSwitch, name: "SW")
                .WithValueField(2, 2, FieldMode.Read, name: "SWS",
                    valueProviderCallback: _ => systemClockSwitch.Value)
                .WithValueField(4, 4, name: "HPRE")
                .WithValueField(8, 3, name: "PPRE1")
                .WithValueField(11, 3, name: "PPRE2")
                .WithValueField(14, 2, name: "RTCPRE_LO")
                .WithValueField(16, 5, name: "MCO1")
                .WithValueField(21, 3, name: "I2SSCR")
                .WithValueField(24, 3, name: "MCO1PRE")
                .WithValueField(27, 3, name: "MCO2PRE")
                .WithValueField(30, 2, name: "MCO2")
            ;

            // RCC_CIR (0x0C) — Clock Interrupt Register
            Registers.CIR.Define(this)
                .WithValueField(0, 32, name: "CIR")
            ;

            // RCC_AHB1RSTR (0x10)
            Registers.AHB1RSTR.Define(this)
                .WithValueField(0, 32, name: "AHB1RSTR")
            ;

            // RCC_AHB2RSTR (0x14)
            Registers.AHB2RSTR.Define(this)
                .WithValueField(0, 32, name: "AHB2RSTR")
            ;

            // RCC_AHB3RSTR (0x18)
            Registers.AHB3RSTR.Define(this)
                .WithValueField(0, 32, name: "AHB3RSTR")
            ;

            // 0x1C reserved
            Registers.Reserved1C.Define(this)
                .WithValueField(0, 32, name: "RESERVED_1C")
            ;

            // RCC_APB1RSTR (0x20)
            Registers.APB1RSTR.Define(this)
                .WithValueField(0, 32, name: "APB1RSTR")
            ;

            // RCC_APB2RSTR (0x24)
            Registers.APB2RSTR.Define(this)
                .WithValueField(0, 32, name: "APB2RSTR")
            ;

            // 0x28, 0x2C reserved
            Registers.Reserved28.Define(this)
                .WithValueField(0, 32, name: "RESERVED_28")
            ;
            Registers.Reserved2C.Define(this)
                .WithValueField(0, 32, name: "RESERVED_2C")
            ;

            // RCC_AHB1ENR (0x30)
            Registers.AHB1ENR.Define(this)
                .WithValueField(0, 32, name: "AHB1ENR")
            ;

            // RCC_AHB2ENR (0x34)
            Registers.AHB2ENR.Define(this)
                .WithValueField(0, 32, name: "AHB2ENR")
            ;

            // RCC_AHB3ENR (0x38)
            Registers.AHB3ENR.Define(this)
                .WithValueField(0, 32, name: "AHB3ENR")
            ;

            // 0x3C reserved
            Registers.Reserved3C.Define(this)
                .WithValueField(0, 32, name: "RESERVED_3C")
            ;

            // RCC_APB1ENR (0x40)
            Registers.APB1ENR.Define(this)
                .WithValueField(0, 32, name: "APB1ENR")
            ;

            // RCC_APB2ENR (0x44)
            Registers.APB2ENR.Define(this)
                .WithValueField(0, 32, name: "APB2ENR")
            ;

            // 0x48, 0x4C reserved
            Registers.Reserved48.Define(this)
                .WithValueField(0, 32, name: "RESERVED_48")
            ;
            Registers.Reserved4C.Define(this)
                .WithValueField(0, 32, name: "RESERVED_4C")
            ;

            // RCC_AHB1LPENR (0x50)
            Registers.AHB1LPENR.Define(this, 0x7EF7B7FF)
                .WithValueField(0, 32, name: "AHB1LPENR")
            ;

            // RCC_AHB2LPENR (0x54)
            Registers.AHB2LPENR.Define(this, 0x000000F1)
                .WithValueField(0, 32, name: "AHB2LPENR")
            ;

            // RCC_AHB3LPENR (0x58)
            Registers.AHB3LPENR.Define(this, 0x00000003)
                .WithValueField(0, 32, name: "AHB3LPENR")
            ;

            // 0x5C reserved
            Registers.Reserved5C.Define(this)
                .WithValueField(0, 32, name: "RESERVED_5C")
            ;

            // RCC_APB1LPENR (0x60)
            Registers.APB1LPENR.Define(this, 0x3FFFC9FF)
                .WithValueField(0, 32, name: "APB1LPENR")
            ;

            // RCC_APB2LPENR (0x64)
            Registers.APB2LPENR.Define(this, 0x04F77F33)
                .WithValueField(0, 32, name: "APB2LPENR")
            ;

            // 0x68, 0x6C reserved
            Registers.Reserved68.Define(this)
                .WithValueField(0, 32, name: "RESERVED_68")
            ;
            Registers.Reserved6C.Define(this)
                .WithValueField(0, 32, name: "RESERVED_6C")
            ;

            // RCC_BDCR (0x70) — Backup Domain Control Register
            Registers.BDCR.Define(this)
                .WithFlag(0, out lseOn, name: "LSEON")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => lseOn.Value, name: "LSERDY")
                .WithFlag(2, name: "LSEBYP")
                .WithFlag(3, name: "LSEDRV0")
                .WithFlag(4, name: "LSEDRV1")
                .WithReservedBits(5, 3)
                .WithValueField(8, 2, name: "RTCSEL")
                .WithReservedBits(10, 5)
                .WithFlag(15, name: "RTCEN")
                .WithFlag(16, name: "BDRST")
                .WithReservedBits(17, 15)
            ;

            // RCC_CSR (0x74) — Clock Control & Status Register
            Registers.CSR.Define(this)
                .WithFlag(0, out lsiOn, name: "LSION")
                .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => lsiOn.Value, name: "LSIRDY")
                .WithReservedBits(2, 22)
                .WithFlag(24, name: "RMVF")
                .WithFlag(25, name: "BORRSTF")
                .WithFlag(26, name: "PINRSTF")
                .WithFlag(27, name: "PORRSTF")
                .WithFlag(28, name: "SFTRSTF")
                .WithFlag(29, name: "IWDGRSTF")
                .WithFlag(30, name: "WWDGRSTF")
                .WithFlag(31, name: "LPWRRSTF")
            ;

            // RCC_SSCGR (0x80) — Spread Spectrum Clock Generation Register
            Registers.SSCGR.Define(this)
                .WithValueField(0, 32, name: "SSCGR")
            ;

            // RCC_PLLI2SCFGR (0x84)
            Registers.PLLI2SCFGR.Define(this, 0x24003000)
                .WithValueField(0, 32, name: "PLLI2SCFGR")
            ;

            // RCC_PLLSAICFGR (0x88)
            Registers.PLLSAICFGR.Define(this, 0x24003000)
                .WithValueField(0, 32, name: "PLLSAICFGR")
            ;

            // RCC_DCKCFGR1 (0x8C)
            Registers.DCKCFGR1.Define(this)
                .WithValueField(0, 32, name: "DCKCFGR1")
            ;

            // RCC_DCKCFGR2 (0x90)
            Registers.DCKCFGR2.Define(this)
                .WithValueField(0, 32, name: "DCKCFGR2")
            ;
        }

        // ── Enable flag fields ───────────────────────────────────────────

        private IFlagRegisterField hsiOn;
        private IFlagRegisterField hseOn;
        private IFlagRegisterField pllOn;
        private IFlagRegisterField plli2sOn;
        private IFlagRegisterField pllsaiOn;
        private IFlagRegisterField lseOn;
        private IFlagRegisterField lsiOn;
        private IValueRegisterField systemClockSwitch;

        // Fallback storage for registers not explicitly defined
        private readonly Dictionary<long, uint> undefinedRegisters = new Dictionary<long, uint>();

        // ── Register map ─────────────────────────────────────────────────

        private enum Registers : long
        {
            CR          = 0x00,
            PLLCFGR     = 0x04,
            CFGR        = 0x08,
            CIR         = 0x0C,
            AHB1RSTR    = 0x10,
            AHB2RSTR    = 0x14,
            AHB3RSTR    = 0x18,
            Reserved1C  = 0x1C,
            APB1RSTR    = 0x20,
            APB2RSTR    = 0x24,
            Reserved28  = 0x28,
            Reserved2C  = 0x2C,
            AHB1ENR     = 0x30,
            AHB2ENR     = 0x34,
            AHB3ENR     = 0x38,
            Reserved3C  = 0x3C,
            APB1ENR     = 0x40,
            APB2ENR     = 0x44,
            Reserved48  = 0x48,
            Reserved4C  = 0x4C,
            AHB1LPENR   = 0x50,
            AHB2LPENR   = 0x54,
            AHB3LPENR   = 0x58,
            Reserved5C  = 0x5C,
            APB1LPENR   = 0x60,
            APB2LPENR   = 0x64,
            Reserved68  = 0x68,
            Reserved6C  = 0x6C,
            BDCR        = 0x70,
            CSR         = 0x74,
            // 0x78, 0x7C reserved (handled by fallback)
            SSCGR       = 0x80,
            PLLI2SCFGR  = 0x84,
            PLLSAICFGR  = 0x88,
            DCKCFGR1    = 0x8C,
            DCKCFGR2    = 0x90,
        }
    }
}
