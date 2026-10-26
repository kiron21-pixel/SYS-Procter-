using System.Runtime.InteropServices;

namespace Guardian.Platform.MacOS;

/// <summary>
/// lstat for macOS. With 64-bit inodes (the default on arm64 and the "$INODE64" variant on x86_64),
/// struct stat starts dev(4) mode(2) nlink(2) ino(8) uid(4): the owner uid is at offset 16.
/// </summary>
internal static partial class MacNative
{
    private const int UidOffset = 16;

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_arm64(string path, byte[] buf);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_x64(string path, byte[] buf);

    [LibraryImport("libc")]
    private static partial uint geteuid();

    public static uint EffectiveUserId() => geteuid();

    public static uint? OwnerUid(string path)
    {
        try
        {
            var buf = new byte[256];
            var rc = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? lstat_arm64(path, buf) : lstat_x64(path, buf);
            return rc == 0 ? BitConverter.ToUInt32(buf, UidOffset) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}
