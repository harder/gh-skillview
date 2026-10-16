using System.Runtime.InteropServices;

namespace SkillView.Inventory;

/// Linux open flags are an architecture ABI, separate from the stat layout.
internal static class LinuxOpenFlags
{
    internal const int ReadWrite = 2;
    internal const int Create = 0x40;
    internal const int Exclusive = 0x80;
    internal const int NonBlocking = 0x800;
    internal const int CloseOnExec = 0x80000;
    internal const int Path = 0x200000;

    internal static int Directory => Current.Directory;
    internal static int NoFollow => Current.NoFollow;
    internal static int TemporaryFile => 0x400000 | Directory;

    private static (int Directory, int NoFollow) Current =>
        ForArchitecture(RuntimeInformation.ProcessArchitecture, BitConverter.IsLittleEndian);

    // Verified against Linux UAPI headers and a native ARM64 glibc probe. In ARM64's
    // ABI x64's O_DIRECTORY bit is O_DIRECT, which fails on a directory.
    internal static (int Directory, int NoFollow) ForArchitecture(Architecture architecture, bool littleEndian) =>
        (architecture, littleEndian) switch
        {
            (Architecture.X64, true) => (0x10000, 0x20000),
            (Architecture.Arm64, true) => (0x4000, 0x8000),
            _ => throw new IOException("Linux open flags are not verified for this process architecture and endianness.")
        };
}
