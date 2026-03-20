// File Read Bypass — intercepts mono_file_map_fileio to serve pre-loaded assembly data.
//
// When the Mono runtime loads assemblies, it calls mono_file_map_fileio() which does:
//   malloc(size) → lseek(fd, 0, SEEK_END) → lseek(fd, offset, SEEK_SET) → read(fd, buf, size)
// Each read goes through NuttX VFS → LittleFS → QSPI, with 256-byte granularity.
// For System.Private.CoreLib.dll (5.76 MB), this takes ~7 minutes under emulation.
//
// This bypass hooks mono_file_map_fileio and serves the file data directly from
// Renode memory, bypassing the entire kernel I/O path. Only fires when Mono loads
// an assembly — no overhead on HCOM, ESPCP, or other reads.
//
// Usage from .resc:
//   sysbus LoadBinary @path/to/System.Private.CoreLib.dll 0xC0400000
//   cpu InstallFileMapBypass 0xC015A460 "System.Private.CoreLib.dll" 0xC0400000 6044160
//
// Toggle: comment out InstallFileMapBypass for hardware-accurate I/O timing.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Hooks
{
    public static class FileReadBypassExtension
    {
        // Cached file data (loaded once at install time)
        private static byte[] cachedFileData = null;
        private static string cachedFileName = null;

        /// <summary>
        /// Hook mono_file_map_fileio to serve assembly data from pre-loaded memory.
        ///
        /// mono_file_map_fileio(size_t size, int fd, int flags, guint64 offset, gint *error):
        ///   R0 = size (bytes to read)
        ///   R1 = allocated buffer (from caller, via function pointer)
        ///   R2 = fd
        ///   Stack: offset, error pointer
        ///
        /// Actually, looking at the disassembly more carefully:
        ///   R0 = size, R1 = (unused), R2 = fd
        ///   The function allocates a buffer internally, reads into it, returns the pointer.
        ///
        /// We hook at entry and:
        ///   1. Allocate a buffer via the same function pointer Mono uses (at [PC+0x9C])
        ///   2. Copy pre-loaded data to the buffer
        ///   3. Return the buffer pointer
        ///
        /// Simpler approach: let the function run its malloc, then hook right before the
        /// read() call and replace it with a memory copy.
        /// </summary>
        public static void InstallFileMapBypass(this ICPUWithHooks cpu,
            [AutoParameter] Machine machine,
            ulong fileMapAddr, string fileName, ulong dataAddr, int dataSize)
        {
            var arm = cpu as Arm;
            if(arm == null)
                throw new Antmicro.Renode.Exceptions.RecoverableException(
                    "FileReadBypass: CPU must be an ARM core");

            var bus = machine.SystemBus;

            // Pre-load file data from Renode memory
            cachedFileData = new byte[dataSize];
            for(int i = 0; i < dataSize; i++)
                cachedFileData[i] = bus.ReadByte(dataAddr + (ulong)i);
            cachedFileName = fileName;

            long bypassed = 0;
            var startTime = DateTime.UtcNow;

            // Hook at the read() call inside mono_file_map_fileio (offset +0x6A from entry)
            // At 0xC015A4CA: bl __Thumbv7ABSLongThunk_read
            // At this point: R0=size (in r7+12), R1=buffer (in r7+36), R2=fd (in r7+4)
            // Actually the regs are rearranged. Let me hook right at the bl read instruction.
            //
            // Before the read call at 0xC015A4CA:
            //   R0 = size (from [r7, #12])
            //   R1 = buffer pointer (from [r7, #36])
            //   R2 = fd (from [r7, #4])
            //
            // We replace the read: copy from cached data, set R3 (result) = size, skip read.
            ulong readCallAddr = fileMapAddr + 0x6A;  // 0xC015A4CA

            cpu.AddHook(readCallAddr, (ICpuSupportingGdb cpuArg, ulong pc) =>
            {
                // Read the local variables from the stack frame
                ulong r7 = arm.GetRegister(7).RawValue;
                int size = (int)bus.ReadDoubleWord(r7 + 12);
                ulong buffer = bus.ReadDoubleWord(r7 + 0x24);  // offset 36 = allocated buffer
                int fd = (int)bus.ReadDoubleWord(r7 + 4);

                // Read the file offset from the stack (passed at [r7, #64] = +0x40)
                long offset = (long)bus.ReadDoubleWord(r7 + 0x40);

                if(size > 0 && size <= cachedFileData.Length && offset >= 0 &&
                   offset + size <= cachedFileData.Length && buffer != 0)
                {
                    // Serve from cached data
                    var slice = new byte[size];
                    Array.Copy(cachedFileData, (int)offset, slice, 0, size);
                    bus.WriteBytes(slice, buffer);

                    // Store result in [r7, #20] (where the code expects bytes_read)
                    bus.WriteDoubleWord(r7 + 0x14, (uint)size);

                    // Set R0 = size (read return value) and skip past the read call
                    // The instruction after the bl read is at +0x6E: mov r3, r0
                    arm.SetRegister(0, Antmicro.Renode.Peripherals.CPU.RegisterValue.Create(
                        (uint)size, 32));

                    // Skip the bl instruction (4 bytes) — set PC to the mov r3, r0
                    arm.PC = Antmicro.Renode.Peripherals.CPU.RegisterValue.Create(
                        (uint)(readCallAddr + 4), 32);

                    bypassed++;
                    if(bypassed == 1 || bypassed % 10 == 0)
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        arm.Log(LogLevel.Info,
                            "FileReadBypass: served {0} bytes from '{1}' (offset={2}, fd={3}, #{4}, {5:F1}s)",
                            size, cachedFileName, offset, fd, bypassed, elapsed);
                    }
                }
                else
                {
                    arm.Log(LogLevel.Warning,
                        "FileReadBypass: NOT serving: size={0} offset={1} buf=0x{2:X8} fd={3} cachedSize={4}",
                        size, offset, buffer, fd, cachedFileData.Length);
                }
            });

            arm.Log(LogLevel.Info,
                "FileReadBypass: hook at mono_file_map_fileio+0x6A (0x{0:X8}), serving '{1}' ({2} bytes)",
                readCallAddr, fileName, dataSize);
        }

        // ARM register indices
        private static class ArmRegisters
        {
            public const int R0 = 0;
            public const int R7 = 7;
        }
    }
}
