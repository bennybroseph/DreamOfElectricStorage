using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Ioctl;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// Bulk-reads file sizes for a volume via FSCTL_QUERY_FILE_LAYOUT — the only way to get
/// 7M sizes in seconds (per-file stats would take minutes). Requires elevation; NTFS only.
/// </summary>
public sealed class FileLayoutSizeReader
{
    private const int ChunkSize = 1024 * 1024;

    /// <summary>Streams (FRN, size-in-bytes) pairs for every file on the volume.</summary>
    public async IAsyncEnumerable<(ulong Frn, long SizeBytes)> ReadSizesAsync(
        string volume, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using SafeFileHandle handle = VolumeHandles.Open(volume);
        byte[] buffer = new byte[ChunkSize];
        var sizes = new List<(ulong, long)>(capacity: 8192);
        bool restart = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool restartThisCall = restart;
            uint bytesReturned = await Task.Run(
                () => QueryLayoutChunk(handle, buffer, restartThisCall, volume), cancellationToken).ConfigureAwait(false);
            restart = false;

            if (bytesReturned == 0)
                yield break; // end of volume enumeration

            sizes.Clear();
            LayoutBufferParser.ParseChunk(buffer.AsSpan(0, (int)bytesReturned), sizes);
            foreach (var pair in sizes)
                yield return pair;
        }
    }

    private static unsafe uint QueryLayoutChunk(SafeFileHandle handle, byte[] buffer, bool restart, string volume)
    {
        var input = new QUERY_FILE_LAYOUT_INPUT
        {
            Flags = PInvoke.QUERY_FILE_LAYOUT_INCLUDE_STREAMS |
                    PInvoke.QUERY_FILE_LAYOUT_INCLUDE_STREAMS_WITH_NO_CLUSTERS_ALLOCATED |
                    (restart ? PInvoke.QUERY_FILE_LAYOUT_RESTART : 0),
            FilterType = QUERY_FILE_LAYOUT_FILTER_TYPE.QUERY_FILE_LAYOUT_FILTER_TYPE_NONE,
        };

        uint bytesReturned;
        BOOL ok;
        fixed (byte* bufferPtr = buffer)
        {
            ok = PInvoke.DeviceIoControl(
                handle,
                PInvoke.FSCTL_QUERY_FILE_LAYOUT,
                &input,
                (uint)sizeof(QUERY_FILE_LAYOUT_INPUT),
                bufferPtr,
                (uint)buffer.Length,
                &bytesReturned,
                null);
        }

        if (ok)
            return bytesReturned;

        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        return error == WIN32_ERROR.ERROR_HANDLE_EOF
            ? 0u
            : throw VolumeHandles.MapError("FSCTL_QUERY_FILE_LAYOUT", volume);
    }
}

/// <summary>
/// Pure span parser for FSCTL_QUERY_FILE_LAYOUT output buffers. Layouts verified against
/// the CsWin32-generated structs: QUERY_FILE_LAYOUT_OUTPUT (16 B header; FileEntryCount@0,
/// FirstFileOffset@4) → FILE_LAYOUT_ENTRY chain (NextFileOffset@4 rel. to entry, FileAttributes@12,
/// FRN@16, FirstStreamOffset@28 rel. to entry) → STREAM_LAYOUT_ENTRY chain (NextStreamOffset@4,
/// EndOfFile@24, AttributeTypeCode@36, StreamIdentifierLength@44). Offsets of 0 end a chain.
/// </summary>
internal static class LayoutBufferParser
{
    private const int OutputHeaderSize = 16;
    private const int FileEntryFixedSize = 40;
    private const int StreamEntryFixedSize = 48; // through StreamIdentifierLength

    private const uint DataAttributeTypeCode = 0x80; // $DATA
    private const uint FileAttributeDirectory = 0x10;

    internal static void ParseChunk(ReadOnlySpan<byte> chunk, List<(ulong Frn, long SizeBytes)> results)
    {
        if (chunk.Length < OutputHeaderSize)
            return;

        uint fileEntryCount = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);

        for (uint i = 0; i < fileEntryCount; i++)
        {
            if (fileOffset == 0 || fileOffset + FileEntryFixedSize > chunk.Length)
                return; // malformed tail — kernel boundary, stop

            var entry = chunk[(int)fileOffset..];
            uint nextFileOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            uint streamOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);

            if ((fileAttributes & FileAttributeDirectory) == 0 &&
                TryGetUnnamedDataStreamSize(entry, streamOffset, (int)chunk.Length - (int)fileOffset, out long size))
            {
                results.Add((frn, size));
            }

            if (nextFileOffset == 0)
                return;
            fileOffset += nextFileOffset;
        }
    }

    private static bool TryGetUnnamedDataStreamSize(ReadOnlySpan<byte> fileEntry, uint streamOffset, int available, out long size)
    {
        size = 0;
        while (streamOffset != 0 && streamOffset + StreamEntryFixedSize <= available)
        {
            var stream = fileEntry[(int)streamOffset..];
            uint nextStreamOffset = BinaryPrimitives.ReadUInt32LittleEndian(stream[4..]);
            long endOfFile = BinaryPrimitives.ReadInt64LittleEndian(stream[24..]);
            uint attributeType = BinaryPrimitives.ReadUInt32LittleEndian(stream[36..]);
            uint identifierLength = BinaryPrimitives.ReadUInt32LittleEndian(stream[44..]);

            if (attributeType == DataAttributeTypeCode && identifierLength == 0)
            {
                size = endOfFile;
                return true;
            }

            if (nextStreamOffset == 0)
                break;
            streamOffset += nextStreamOffset;
        }
        return false;
    }
}
