using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// Parses FSCTL_ENUM_USN_DATA output buffers: an 8-byte next-start FRN header
/// followed by packed variable-length USN_RECORD_V2 records (MS-FSCC 2.3.62.2).
/// Pure span code — unit-testable without volume handles or elevation.
/// </summary>
internal static class UsnRecordParser
{
    private const int HeaderSize = 8;          // leading DWORDLONG: next StartFileReferenceNumber
    private const int FixedRecordSize = 60;    // USN_RECORD_V2 up to and including FileNameOffset

    // USN_RECORD_V2 field offsets (fixed by MS-FSCC; do not derive from any C struct).
    private const int RecordLengthOffset = 0;   // u32
    private const int MajorVersionOffset = 4;   // u16
    private const int FrnOffset = 8;            // u64
    private const int ParentFrnOffset = 16;     // u64
    private const int FileAttributesOffset = 52; // u32
    private const int FileNameLengthOffset = 56; // u16, bytes
    private const int FileNameOffsetOffset = 58; // u16, from record start

    private const uint FileAttributeDirectory = 0x10;

    /// <summary>
    /// Parses one DeviceIoControl output chunk, appending nodes to <paramref name="results"/>.
    /// Returns the FRN to use as the next enumeration start point.
    /// Malformed tails (zero/overrunning lengths) end parsing rather than throw — the
    /// buffer boundary is kernel-provided, so truncation means "done with this chunk".
    /// </summary>
    internal static ulong ParseChunk(ReadOnlySpan<byte> chunk, List<FileNode> results)
    {
        if (chunk.Length < HeaderSize)
            return 0;

        ulong nextStartFrn = BinaryPrimitives.ReadUInt64LittleEndian(chunk);
        var records = chunk[HeaderSize..];

        while (records.Length >= FixedRecordSize)
        {
            uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(records[RecordLengthOffset..]);
            if (recordLength < FixedRecordSize || recordLength > (uint)records.Length)
                break;

            ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(records[MajorVersionOffset..]);
            if (majorVersion == 2)
            {
                ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(records[FrnOffset..]);
                ulong parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(records[ParentFrnOffset..]);
                uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(records[FileAttributesOffset..]);
                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(records[FileNameLengthOffset..]);
                ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(records[FileNameOffsetOffset..]);

                if (nameOffset + nameLength <= recordLength)
                {
                    // UTF-16, not null-terminated — length is authoritative (MS-FSCC).
                    string name = new(MemoryMarshal.Cast<byte, char>(records.Slice(nameOffset, nameLength)));
                    results.Add(new FileNode(
                        Id: frn,
                        ParentId: parentFrn,
                        Name: name,
                        SizeBytes: 0, // USN records carry no size; filled by a later slice
                        IsDirectory: (attributes & FileAttributeDirectory) != 0));
                }
            }

            records = records[(int)recordLength..];
        }

        return nextStartFrn;
    }
}
