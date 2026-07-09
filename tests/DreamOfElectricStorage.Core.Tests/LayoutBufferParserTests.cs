using System.Buffers.Binary;
using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class LayoutBufferParserTests
{
    private const uint DataAttribute = 0x80;
    private const uint NamedStreamIdBytes = 8; // e.g. "Zone" as UTF-16

    [Fact]
    public void SingleFile_UnnamedDataStream_YieldsSize()
    {
        byte[] chunk = BuildChunk(File(frn: 42, streams: [Stream(DataAttribute, endOfFile: 1234, identifierLength: 0)]));

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(chunk, results);

        Assert.Equal((42ul, 1234L), Assert.Single(results));
    }

    [Fact]
    public void NamedStreams_AreIgnored_UnnamedWins()
    {
        byte[] chunk = BuildChunk(File(7, streams:
        [
            Stream(DataAttribute, endOfFile: 999, identifierLength: NamedStreamIdBytes), // Zone.Identifier etc.
            Stream(DataAttribute, endOfFile: 555, identifierLength: 0),
        ]));

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(chunk, results);

        Assert.Equal((7ul, 555L), Assert.Single(results));
    }

    [Fact]
    public void NonDataAttributes_AreIgnored()
    {
        byte[] chunk = BuildChunk(File(7, streams: [Stream(attributeType: 0x30 /* $FILE_NAME */, endOfFile: 111, identifierLength: 0)]));

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(chunk, results);

        Assert.Empty(results);
    }

    [Fact]
    public void Directories_AreSkipped()
    {
        byte[] chunk = BuildChunk(File(9, streams: [Stream(DataAttribute, 42, 0)], attributes: 0x10));

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(chunk, results);

        Assert.Empty(results);
    }

    [Fact]
    public void MultipleFiles_Chained_AllParsed()
    {
        byte[] chunk = BuildChunk(
            File(1, streams: [Stream(DataAttribute, 10, 0)]),
            File(2, streams: [Stream(DataAttribute, 20, 0)]),
            File(3, streams: [Stream(DataAttribute, 30, 0)]));

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(chunk, results);

        Assert.Equal([(1ul, 10L), (2ul, 20L), (3ul, 30L)], results);
    }

    [Fact]
    public void FileWithoutStreams_YieldsNothing()
    {
        byte[] chunk = BuildChunk(File(5, streams: []));

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(chunk, results);

        Assert.Empty(results);
    }

    [Fact]
    public void TruncatedBuffer_StopsWithoutThrowing()
    {
        byte[] full = BuildChunk(File(1, streams: [Stream(DataAttribute, 10, 0)]));
        byte[] truncated = full[..(full.Length - 20)];

        var results = new List<(ulong, long)>();
        LayoutBufferParser.ParseChunk(truncated, results);
        // No assertion on contents — must simply not throw or overrun.
    }

    // --- builders matching the generated struct layouts ---
    // QUERY_FILE_LAYOUT_OUTPUT: 16B header (FileEntryCount@0, FirstFileOffset@4)
    // FILE_LAYOUT_ENTRY: 40B fixed (NextFileOffset@4, FileAttributes@12, FRN@16, FirstStreamOffset@28)
    // STREAM_LAYOUT_ENTRY: 48B fixed used here (NextStreamOffset@4, EndOfFile@24, AttributeTypeCode@36, StreamIdentifierLength@44)

    private sealed record FileSpec(ulong Frn, uint Attributes, byte[][] Streams);

    private static FileSpec File(ulong frn, byte[][] streams, uint attributes = 0x80 /* NORMAL */) =>
        new(frn, attributes, streams);

    private static byte[] Stream(uint attributeType, long endOfFile, uint identifierLength)
    {
        byte[] stream = new byte[48 + (int)identifierLength];
        var span = stream.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0); // NextStreamOffset patched by builder
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], endOfFile);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], attributeType);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], identifierLength);
        return stream;
    }

    private static byte[] BuildChunk(params FileSpec[] files)
    {
        var chunk = new List<byte>(new byte[16]);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)files.Length);         // FileEntryCount
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 16);               // FirstFileOffset
        chunk.Clear();
        chunk.AddRange(header);

        for (int f = 0; f < files.Length; f++)
        {
            FileSpec file = files[f];
            int streamsSize = file.Streams.Sum(s => s.Length);
            int entrySize = 40 + streamsSize;

            byte[] entry = new byte[entrySize];
            var span = entry.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], f < files.Length - 1 ? (uint)entrySize : 0); // NextFileOffset (rel.)
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..], file.Attributes);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], file.Frn);
            BinaryPrimitives.WriteUInt32LittleEndian(span[28..], file.Streams.Length > 0 ? 40u : 0u); // FirstStreamOffset (rel.)

            int offset = 40;
            for (int s = 0; s < file.Streams.Length; s++)
            {
                byte[] stream = file.Streams[s];
                stream.CopyTo(span[offset..]);
                // Patch NextStreamOffset (relative to this stream entry).
                BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], s < file.Streams.Length - 1 ? (uint)stream.Length : 0);
                offset += stream.Length;
            }

            chunk.AddRange(entry);
        }

        return [.. chunk];
    }
}
