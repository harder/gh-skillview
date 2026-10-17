using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static bool StateDirectoryIsMissing(string path, CancellationToken token)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 128) throw new IOException("Desired-state presence path exceeds its component contract.");
        using var buffer = new StatBuffer();
        var current = CreateHandle(OpenWithRetry("/", UnixConstants.DirectoryFollowOpenFlags), "open presence root failed");
        try
        {
            foreach (var part in parts)
            {
                token.ThrowIfCancellationRequested();
                var parent = ReadStat(current, buffer.Pointer);
                if (FStatAtWithRetry(current.FileDescriptor, part, buffer.Pointer, UnixConstants.NoFollowFlag) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 2) throw new IOException("Desired-state presence is unknown: " + new Win32Exception(error).Message);
                    token.ThrowIfCancellationRequested();
                    if (!Matches(parent, ReadStat(current, buffer.Pointer))) throw new IOException("Desired-state absence parent changed.");
                    return true;
                }
                var named = ParseStat(buffer.Pointer);
                if (named.FileType != DirectoryType) throw new IOException("Desired-state presence has a link or non-directory ancestor.");
                var next = OpenDirectoryAt(current, part, refuseNestedMount: false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!Matches(named, ReadStat(next, buffer.Pointer)) || !Matches(parent, ReadStat(current, buffer.Pointer)))
                        throw new IOException("Desired-state presence changed during native inspection.");
                    current.Dispose(); current = next; next = null!;
                }
                finally { next?.Dispose(); }
            }
            return false;
        }
        finally { current.Dispose(); }
    }
}
