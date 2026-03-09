// Winbond W25QXXXJV SPI NOR Flash — used on the Meadow F7 board
// Extends Renode's GenericSpiFlash with Winbond JEDEC identifiers.
// Supports 2MB (W25Q16JV) through 64MB (W25Q512JV).

using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Exceptions;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class Winbond_W25QXXXJV : GenericSpiFlash
    {
        public Winbond_W25QXXXJV(MappedMemory underlyingMemory)
            : base(underlyingMemory, ManufacturerId, MemoryType)
        {
            if(underlyingMemory.Size < 0x200000 || underlyingMemory.Size > 0x4000000)
            {
                throw new ConstructionException(
                    $"W25QXXXJV memory size must be between 2MB and 64MB, got {underlyingMemory.Size}");
            }
        }

        // Winbond manufacturer ID
        private const byte ManufacturerId = 0xEF;
        // W25QXXXJV memory type (QE=1 fixed, quad I/O capable)
        private const byte MemoryType = 0x40;
    }
}
