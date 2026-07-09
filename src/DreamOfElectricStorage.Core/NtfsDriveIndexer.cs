using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// Enumerates every file/directory on an NTFS volume by reading the MFT via
/// FSCTL_ENUM_USN_DATA — the "Everything" approach. Requires elevation.
/// </summary>
public sealed class NtfsDriveIndexer : IDriveIndexer
{
    private const int ChunkSize = 1024 * 1024;

    public IAsyncEnumerable<FileNode> EnumerateAsync(string volume, CancellationToken cancellationToken = default)
    {
        // Validate eagerly so bad arguments throw at call time, not first MoveNextAsync.
        string volumePath = NormalizeVolume(volume);
        return EnumerateCoreAsync(volumePath, cancellationToken);
    }

    private static async IAsyncEnumerable<FileNode> EnumerateCoreAsync(
        string volumePath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using SafeFileHandle volumeHandle = OpenVolume(volumePath);

        var enumData = new MFT_ENUM_DATA_V0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue,
        };
        byte[] buffer = new byte[ChunkSize];
        var nodes = new List<FileNode>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localEnumData = enumData;
            uint bytesReturned = await Task.Run(
                () => FetchChunk(volumeHandle, localEnumData, buffer), cancellationToken).ConfigureAwait(false);
            if (bytesReturned == 0)
                yield break; // ERROR_HANDLE_EOF — enumeration complete

            nodes.Clear();
            enumData.StartFileReferenceNumber = UsnRecordParser.ParseChunk(buffer.AsSpan(0, (int)bytesReturned), nodes);

            foreach (var node in nodes)
                yield return node;
        }
    }

    /// <summary>Runs one FSCTL_ENUM_USN_DATA call. Returns bytes written, or 0 at end of MFT.</summary>
    private static unsafe uint FetchChunk(SafeFileHandle volumeHandle, MFT_ENUM_DATA_V0 enumData, byte[] buffer)
    {
        uint bytesReturned;
        BOOL ok;
        fixed (byte* bufferPtr = buffer)
        {
            ok = PInvoke.DeviceIoControl(
                volumeHandle,
                PInvoke.FSCTL_ENUM_USN_DATA,
                &enumData,
                (uint)sizeof(MFT_ENUM_DATA_V0),
                bufferPtr,
                (uint)buffer.Length,
                &bytesReturned,
                null);
        }

        if (ok)
            return bytesReturned;

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        return error switch
        {
            WIN32_ERROR.ERROR_HANDLE_EOF => 0,
            WIN32_ERROR.ERROR_INVALID_FUNCTION => throw new NotSupportedException(
                "The volume does not support MFT enumeration (NTFS only)."),
            WIN32_ERROR.ERROR_ACCESS_DENIED => throw new UnauthorizedAccessException(
                "Access to the volume was denied. MFT enumeration requires an elevated (administrator) process."),
            _ => throw new IOException($"FSCTL_ENUM_USN_DATA failed with Win32 error {(uint)error}."),
        };
    }

    private static SafeFileHandle OpenVolume(string volumePath)
    {
        SafeFileHandle handle = PInvoke.CreateFile(
            volumePath,
            (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            dwFlagsAndAttributes: 0,
            hTemplateFile: null);

        if (!handle.IsInvalid)
            return handle;

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw error switch
        {
            WIN32_ERROR.ERROR_ACCESS_DENIED => new UnauthorizedAccessException(
                $"Access to {volumePath} was denied. MFT enumeration requires an elevated (administrator) process."),
            _ => new IOException($"Failed to open volume {volumePath} (Win32 error {(uint)error})."),
        };
    }

    /// <summary>Accepts "C", "C:", or "C:\" (any case) and yields the raw volume path \\.\C:.</summary>
    private static string NormalizeVolume(string volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);

        string trimmed = volume.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':')
            trimmed = trimmed[..1];

        if (trimmed.Length != 1 || !char.IsAsciiLetter(trimmed[0]))
            throw new ArgumentException($"'{volume}' is not a drive letter (expected e.g. \"C:\").", nameof(volume));

        return $@"\\.\{char.ToUpperInvariant(trimmed[0])}:";
    }
}
