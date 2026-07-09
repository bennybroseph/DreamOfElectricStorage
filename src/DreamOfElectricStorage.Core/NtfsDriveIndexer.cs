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
        string volumePath = VolumeHandles.Normalize(volume);
        return EnumerateCoreAsync(volumePath, cancellationToken);
    }

    private static async IAsyncEnumerable<FileNode> EnumerateCoreAsync(
        string volumePath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using SafeFileHandle volumeHandle = VolumeHandles.Open(volumePath);

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

}
