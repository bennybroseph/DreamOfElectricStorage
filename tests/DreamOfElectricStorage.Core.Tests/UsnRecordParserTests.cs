using System.Buffers.Binary;
using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class UsnRecordParserTests
{
    private const uint DirectoryAttribute = 0x10;

    [Fact]
    public void SingleRecord_RoundTrips()
    {
        byte[] chunk = Chunk(nextStartFrn: 777, Record(frn: 42, parentFrn: 5, name: "report.pdf"));

        var nodes = new List<FileNode>();
        ulong next = UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Equal(777ul, next);
        var node = Assert.Single(nodes);
        Assert.Equal(42ul, node.Id);
        Assert.Equal(5ul, node.ParentId);
        Assert.Equal("report.pdf", node.Name);
        Assert.Equal(0, node.SizeBytes);
        Assert.False(node.IsDirectory);
    }

    [Fact]
    public void DirectoryAttribute_SetsIsDirectory()
    {
        byte[] chunk = Chunk(1, Record(1, 0, "Windows", attributes: DirectoryAttribute));

        var nodes = new List<FileNode>();
        UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.True(Assert.Single(nodes).IsDirectory);
    }

    [Fact]
    public void MultiplePackedRecords_AllParsed_InOrder()
    {
        byte[] chunk = Chunk(9,
            Record(1, 0, "a"),
            Record(2, 1, "bb", attributes: DirectoryAttribute),
            Record(3, 2, "ccc.txt"));

        var nodes = new List<FileNode>();
        UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(["a", "bb", "ccc.txt"], nodes.Select(n => n.Name));
        Assert.Equal([1ul, 2ul, 3ul], nodes.Select(n => n.Id));
    }

    [Fact]
    public void NameLength_IsAuthoritative_NoNullTerminatorNeeded()
    {
        // Give the record trailing padding bytes that would read as garbage
        // if the parser relied on a null terminator instead of FileNameLength.
        byte[] chunk = Chunk(1, Record(1, 0, "exact", trailingPadding: 6, paddingFill: 0x58 /* 'X' */));

        var nodes = new List<FileNode>();
        UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Equal("exact", Assert.Single(nodes).Name);
    }

    [Fact]
    public void NonV2Record_IsSkipped_ParsingContinues()
    {
        byte[] v3 = Record(1, 0, "ghost");
        BinaryPrimitives.WriteUInt16LittleEndian(v3.AsSpan(4), 3); // MajorVersion = 3
        byte[] chunk = Chunk(1, v3, Record(2, 0, "real.txt"));

        var nodes = new List<FileNode>();
        UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Equal("real.txt", Assert.Single(nodes).Name);
    }

    [Fact]
    public void ZeroRecordLength_StopsWithoutThrowing()
    {
        byte[] good = Record(1, 0, "ok");
        byte[] corrupt = new byte[64]; // RecordLength = 0
        byte[] chunk = Chunk(1, good, corrupt);

        var nodes = new List<FileNode>();
        ulong next = UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Equal(1ul, next);
        Assert.Single(nodes);
    }

    [Fact]
    public void RecordLengthOverrunningBuffer_StopsWithoutThrowing()
    {
        byte[] record = Record(1, 0, "ok");
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0), 4096); // claims more than the chunk holds
        byte[] chunk = Chunk(1, record);

        var nodes = new List<FileNode>();
        UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Empty(nodes);
    }

    [Fact]
    public void BufferSmallerThanHeader_ReturnsZero()
    {
        var nodes = new List<FileNode>();
        ulong next = UsnRecordParser.ParseChunk(new byte[4], nodes);

        Assert.Equal(0ul, next);
        Assert.Empty(nodes);
    }

    [Fact]
    public void HeaderOnly_NoRecords_ReturnsNextFrn()
    {
        byte[] chunk = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(chunk, 12345);

        var nodes = new List<FileNode>();
        ulong next = UsnRecordParser.ParseChunk(chunk, nodes);

        Assert.Equal(12345ul, next);
        Assert.Empty(nodes);
    }

    // --- journal chunk parsing (same layout, Reason populated) ---

    [Fact]
    public void JournalChunk_ParsesReasonAndNextUsn()
    {
        byte[] record = Record(7, 5, "made.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), UsnReasons.FileCreate); // Reason @40
        byte[] chunk = Chunk(nextStartFrn: 987654, record);

        var entries = new List<UsnJournalEntry>();
        long nextUsn = UsnRecordParser.ParseJournalChunk(chunk, entries);

        Assert.Equal(987654, nextUsn);
        var entry = Assert.Single(entries);
        Assert.Equal("made.txt", entry.Node.Name);
        Assert.Equal(UsnReasons.FileCreate, entry.Reason);
    }

    [Fact]
    public void JournalChunk_MixedReasons_AllParsedInOrder()
    {
        byte[] created = Record(1, 5, "a.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(created.AsSpan(40), UsnReasons.FileCreate);
        byte[] deleted = Record(2, 5, "b.txt");
        BinaryPrimitives.WriteUInt32LittleEndian(deleted.AsSpan(40), UsnReasons.FileDelete | UsnReasons.FileCreate);

        var entries = new List<UsnJournalEntry>();
        UsnRecordParser.ParseJournalChunk(Chunk(1, created, deleted), entries);

        Assert.Equal(2, entries.Count);
        Assert.Equal(UsnReasons.FileCreate, entries[0].Reason);
        Assert.Equal(UsnReasons.FileDelete | UsnReasons.FileCreate, entries[1].Reason);
    }

    [Fact]
    public void JournalChunk_HeaderOnly_ReturnsNextUsnNoEntries()
    {
        byte[] chunk = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(chunk, 42);

        var entries = new List<UsnJournalEntry>();
        Assert.Equal(42, UsnRecordParser.ParseJournalChunk(chunk, entries));
        Assert.Empty(entries);
    }

    // --- test buffer builders (layout per MS-FSCC 2.3.62.2) ---

    /// <summary>Builds a USN_RECORD_V2 byte image with the name placed at offset 60.</summary>
    private static byte[] Record(
        ulong frn, ulong parentFrn, string name, uint attributes = 0x80 /* FILE_ATTRIBUTE_NORMAL */,
        int trailingPadding = 0, byte paddingFill = 0)
    {
        const int fixedSize = 60;
        int nameBytes = name.Length * 2;
        // Real records are 8-byte aligned; emulate with explicit padding.
        int recordLength = fixedSize + nameBytes + trailingPadding;

        byte[] record = new byte[recordLength];
        if (paddingFill != 0)
            record.AsSpan(fixedSize + nameBytes).Fill(paddingFill);

        var span = record.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], (uint)recordLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 2);  // MajorVersion
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0);  // MinorVersion
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], frn);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], parentFrn);
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], (ushort)nameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(span[58..], fixedSize);
        System.Text.Encoding.Unicode.GetBytes(name, span[fixedSize..]);

        return record;
    }

    /// <summary>Prepends the 8-byte next-start-FRN header and concatenates records.</summary>
    private static byte[] Chunk(ulong nextStartFrn, params byte[][] records)
    {
        byte[] chunk = new byte[8 + records.Sum(r => r.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(chunk, nextStartFrn);
        int offset = 8;
        foreach (byte[] record in records)
        {
            record.CopyTo(chunk, offset);
            offset += record.Length;
        }
        return chunk;
    }
}
