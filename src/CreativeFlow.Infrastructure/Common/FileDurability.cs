using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CreativeFlow.Infrastructure.Common;

/// <summary>在 Linux/macOS 将 rename/unlink 的目录项同步到磁盘，避免仅文件正文已持久化。</summary>
internal static partial class FileDurability
{
    internal static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var descriptor = Open(path, 0);
        if (descriptor < 0) throw new IOException("无法同步存储目录。", new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (Fsync(descriptor) != 0) throw new IOException("存储目录同步失败。", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { Close(descriptor); }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);
}
