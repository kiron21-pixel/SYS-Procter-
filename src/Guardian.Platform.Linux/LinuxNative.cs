using System.Runtime.InteropServices;

namespace Guardian.Platform.Linux;

/// <summary>Minimal libc calls used for file-ownership checks. statx's layout is architecture-independent.</summary>
internal static partial class LinuxNative
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxUid = 0x8;
    private const int StatxUidOffset = 20;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, byte[] buffer);

    [LibraryImport("libc")]
    private static partial uint geteuid();

    public static uint EffectiveUserId() => geteuid();

    /// <summary>Owner uid of the path itself (links are not followed), or null if it cannot be read.</summary>
    public static uint? OwnerUid(string path)
    {
        try
        {
            var buf = new byte[256];
            if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxUid, buf) != 0) return null;
            var mask = BitConverter.ToUInt32(buf, 0);
            if ((mask & StatxUid) == 0) return null;
            return BitConverter.ToUInt32(buf, StatxUidOffset);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
