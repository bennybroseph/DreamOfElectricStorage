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
    private const int ReasonOffset = 40;        // u32 (0 in MFT-enum output, set in journal reads)
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

        while (TryParseRecord(ref records, out FileNode? node, out _))
        {
            if (node is not null)
                results.Add(node);
        }

        return nextStartFrn;
    }

    /// <summary>
    /// Parses one FSCTL_READ_USN_JOURNAL output chunk (same layout, Reason populated).
    /// Returns the USN to pass as the next read's StartUsn.
    /// </summary>
    internal static long ParseJournalChunk(ReadOnlySpan<byte> chunk, List<UsnJournalEntry> results)
    {
        if (chunk.Length < HeaderSize)
            return 0;

        long nextUsn = BinaryPrimitives.ReadInt64LittleEndian(chunk);
        var records = chunk[HeaderSize..];

        while (TryParseRecord(ref records, out FileNode? node, out uint reason))
        {
            if (node is not null)
                results.Add(new UsnJournalEntry(node, reason));
        }

        return nextUsn;
    }

    /// <summary>
    /// Reads one USN_RECORD_V2 and advances <paramref name="records"/> past it.
    /// False = end of buffer or malformed tail (kernel-provided boundary → stop, don't throw).
    /// A true return with null node means "well-formed but skipped" (non-V2, bad name bounds).
    /// </summary>
    private static bool TryParseRecord(ref ReadOnlySpan<byte> records, out FileNode? node, out uint reason)
    {
        node = null;
        reason = 0;

        if (records.Length < FixedRecordSize)
            return false;

        uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(records[RecordLengthOffset..]);
        if (recordLength < FixedRecordSize || recordLength > (uint)records.Length)
            return false;

        ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(records[MajorVersionOffset..]);
        if (majorVersion == 2)
        {
            ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(records[FrnOffset..]);
            ulong parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(records[ParentFrnOffset..]);
            reason = BinaryPrimitives.ReadUInt32LittleEndian(records[ReasonOffset..]);
            uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(records[FileAttributesOffset..]);
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(records[FileNameLengthOffset..]);
            ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(records[FileNameOffsetOffset..]);

            if (nameOffset + nameLength <= recordLength)
            {
                // UTF-16, not null-terminated — length is authoritative (MS-FSCC).
                string name = new(MemoryMarshal.Cast<byte, char>(records.Slice(nameOffset, nameLength)));
                node = new FileNode(
                    Id: frn,
                    ParentId: parentFrn,
                    Name: name,
                    SizeBytes: 0, // USN records carry no size; filled by a later slice
                    IsDirectory: (attributes & FileAttributeDirectory) != 0);
            }
        }

        records = records[(int)recordLength..];
        return true;
    }
}

/// <summary>One USN change-journal record: the affected node plus USN_REASON_* flags.</summary>
public sealed record UsnJournalEntry(FileNode Node, uint Reason);
