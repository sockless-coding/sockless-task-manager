using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>An event's payload, as its metadata describes it.</summary>
internal delegate void TraceEventHandler(EventMetadata metadata, ReadOnlySpan<byte> payload);

/// <summary>
/// Reads an EventPipe stream in the nettrace format, version 4 (dotnet/diagnostics,
/// documentation/design-docs/nettrace-format.md): a header, then serialized objects, one per block
/// of events, metadata, stacks or sequence points. Only events and their metadata are decoded.
/// </summary>
internal sealed class NettraceReader(Stream stream)
{
    private const byte NullReference = 1;
    private const byte BeginPrivateObject = 5;
    private const byte EndObject = 6;
    private const int TraceObjectSize = 48;

    // Compressed event header flags.
    private const byte HasMetadataId = 1;
    private const byte HasCaptureThreadAndSequence = 2;
    private const byte HasThreadId = 4;
    private const byte HasStackId = 8;
    private const byte HasActivityId = 16;
    private const byte HasRelatedActivityId = 32;
    private const byte HasDataLength = 128;

    private readonly Dictionary<int, EventMetadata> _metadata = [];
    private readonly Dictionary<ulong, uint> _sequences = [];
    private readonly byte[] _small = new byte[64];
    private byte[] _block = new byte[64 * 1024];
    private long _position;

    /// <summary>
    /// Events the runtime dropped, its buffer full when they were raised: seen as gaps in each
    /// thread's numbering. Reading task.
    /// </summary>
    public long LostEvents { get; private set; }

    /// <summary>The traced process's pointer size: payloads holding addresses depend on it.</summary>
    public int PointerSize { get; private set; } = 8;

    /// <summary>Calls <paramref name="handler"/> for each event until the stream ends.</summary>
    public async Task ReadAsync(TraceEventHandler handler, CancellationToken cancel)
    {
        if (!(await ReadAsync(8, cancel)).AsSpan().SequenceEqual("Nettrace"u8))
            throw new InvalidDataException("Not a nettrace stream.");
        var length = await ReadInt32Async(cancel);
        if (!(await ReadAsync(length, cancel)).AsSpan().SequenceEqual("!FastSerialization.1"u8))
            throw new InvalidDataException("Unknown nettrace serialization.");

        while (true)
        {
            var tag = (await ReadAsync(1, cancel))[0];
            if (tag == NullReference)
                return;
            if (tag != BeginPrivateObject)
                throw new InvalidDataException("Malformed nettrace object.");

            var type = await ReadTypeAsync(cancel);
            if (type == "Trace")
            {
                // Clock sync time and frequency, then the process's pointer size, id and processors.
                var trace = await ReadAsync(TraceObjectSize, cancel);
                PointerSize = BinaryPrimitives.ReadInt32LittleEndian(trace.AsSpan(32));
            }
            else
            {
                // Every other object is a block: its size, padding to a 4-byte boundary, its bytes.
                var size = await ReadInt32Async(cancel);
                var padding = (int)((4 - _position % 4) % 4);
                if (padding > 0)
                    await ReadAsync(padding, cancel);

                if (_block.Length < size)
                    _block = new byte[Math.Max(size, _block.Length * 2)];
                await stream.ReadExactlyAsync(_block.AsMemory(0, size), cancel);
                _position += size;

                try
                {
                    if (type == "EventBlock")
                        ReadEvents(_block.AsSpan(0, size), isMetadata: false, handler);
                    else if (type == "MetadataBlock")
                        ReadEvents(_block.AsSpan(0, size), isMetadata: true, handler);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // A read past the end of an event or block: not the layout it was taken for.
                    throw new InvalidDataException("Malformed nettrace event.");
                }
            }

            if ((await ReadAsync(1, cancel))[0] != EndObject)
                throw new InvalidDataException("Malformed nettrace object.");
        }
    }

    /// <summary>An object's type: a nested object holding a version and a name.</summary>
    private async Task<string> ReadTypeAsync(CancellationToken cancel)
    {
        var header = await ReadAsync(2, cancel);
        if (header[0] != BeginPrivateObject || header[1] != NullReference)
            throw new InvalidDataException("Malformed nettrace type.");

        await ReadAsync(8, cancel); // version, minimum reader version
        var length = await ReadInt32Async(cancel);
        var name = Encoding.ASCII.GetString(await ReadAsync(length, cancel));
        if ((await ReadAsync(1, cancel))[0] != EndObject)
            throw new InvalidDataException("Malformed nettrace type.");
        return name;
    }

    /// <summary>
    /// A block of events with compressed headers: each header gives only what changed since the
    /// previous event in the block.
    /// </summary>
    private void ReadEvents(ReadOnlySpan<byte> block, bool isMetadata, TraceEventHandler handler)
    {
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        if ((flags & 1) == 0)
            throw new InvalidDataException("Uncompressed nettrace events aren't supported.");

        var reader = new SpanReader(block) { Offset = headerSize };
        var metadataId = 0;
        var payloadSize = 0;
        var sequence = 0u;
        var captureThread = 0ul;
        while (reader.Offset < block.Length)
        {
            var eventFlags = reader.Byte();
            if ((eventFlags & HasMetadataId) != 0)
                metadataId = (int)reader.VarUInt();
            if ((eventFlags & HasCaptureThreadAndSequence) != 0)
            {
                sequence += (uint)reader.VarUInt() + 1;
                captureThread = reader.VarUInt();
                reader.VarUInt(); // processor
            }
            else if (metadataId != 0)
            {
                sequence++;
            }

            if ((eventFlags & HasThreadId) != 0)
                reader.VarUInt();
            if ((eventFlags & HasStackId) != 0)
                reader.VarUInt();
            reader.VarUInt(); // timestamp delta
            if ((eventFlags & HasActivityId) != 0)
                reader.Offset += 16;
            if ((eventFlags & HasRelatedActivityId) != 0)
                reader.Offset += 16;
            if ((eventFlags & HasDataLength) != 0)
                payloadSize = (int)reader.VarUInt();

            var payload = block.Slice(reader.Offset, payloadSize);
            reader.Offset += payloadSize;

            if (isMetadata)
            {
                var metadata = EventMetadata.Read(payload);
                _metadata[metadata.Id] = metadata;
                continue;
            }

            // Each thread numbers its events, dropped ones included: a jump is what was lost.
            if (_sequences.TryGetValue(captureThread, out var last) && sequence > last + 1)
                LostEvents += sequence - last - 1;
            _sequences[captureThread] = sequence;

            if (_metadata.TryGetValue(metadataId, out var known))
                handler(known, payload);
        }
    }

    private async Task<int> ReadInt32Async(CancellationToken cancel) =>
        BinaryPrimitives.ReadInt32LittleEndian(await ReadAsync(4, cancel));

    /// <summary>Small reads only; the bytes are valid until the next read.</summary>
    private async Task<byte[]> ReadAsync(int count, CancellationToken cancel)
    {
        var buffer = count <= _small.Length ? _small : new byte[count];
        await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancel);
        _position += count;
        return count == buffer.Length ? buffer : buffer[..count];
    }
}

/// <summary>One field of an event's payload, possibly a struct of further fields.</summary>
internal sealed record EventField(string Name, TypeCode Type, EventField[]? Fields);

/// <summary>Who raised an event and the layout of its payload.</summary>
internal sealed class EventMetadata
{
    private EventMetadata(int id, string provider, int eventId, string name, EventField[] fields)
    {
        Id = id;
        Provider = provider;
        EventId = eventId;
        Name = name;
        Fields = fields;
    }

    public int Id { get; }

    public string Provider { get; }

    /// <summary>The event's number in its provider's manifest; the runtime's own events are known by it.</summary>
    public int EventId { get; }

    /// <summary>Empty for the runtime's own events.</summary>
    public string Name { get; }

    /// <summary>Empty for the runtime's own events: their layouts are in its manifest, not the stream.</summary>
    public EventField[] Fields { get; }

    internal static EventMetadata Read(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        var id = reader.Int32();
        var provider = reader.String();
        var eventId = reader.Int32();
        var name = reader.String();
        reader.Offset += sizeof(long) + sizeof(int) + sizeof(int); // keywords, version, level

        // Events whose fields this layout can't describe have none here.
        EventField[] fields;
        try
        {
            fields = reader.Offset < payload.Length ? ReadFields(ref reader, reader.Int32()) : [];
        }
        catch (InvalidDataException)
        {
            fields = [];
        }

        return new EventMetadata(id, provider, eventId, name, fields);
    }

    /// <summary>
    /// The payload's values by field name, structs flattened. Stops at a field it can't size; a
    /// counter's payload never has one.
    /// </summary>
    internal Dictionary<string, object> Decode(ReadOnlySpan<byte> payload)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var reader = new SpanReader(payload);
        Decode(Fields, ref reader, values);
        return values;
    }

    private static bool Decode(EventField[] fields, ref SpanReader reader, Dictionary<string, object> values)
    {
        foreach (var field in fields)
        {
            object? value = field.Type switch
            {
                TypeCode.Object => Decode(field.Fields!, ref reader, values) ? null : false,
                TypeCode.SByte or TypeCode.Byte => (double)reader.Bytes(1)[0],
                TypeCode.Int16 => (double)BinaryPrimitives.ReadInt16LittleEndian(reader.Bytes(2)),
                TypeCode.UInt16 or TypeCode.Char => (double)BinaryPrimitives.ReadUInt16LittleEndian(reader.Bytes(2)),
                TypeCode.Int32 or TypeCode.Boolean => (double)reader.Int32(),
                TypeCode.UInt32 => (double)BinaryPrimitives.ReadUInt32LittleEndian(reader.Bytes(4)),
                TypeCode.Int64 => (double)BinaryPrimitives.ReadInt64LittleEndian(reader.Bytes(8)),
                TypeCode.UInt64 or TypeCode.DateTime => (double)BinaryPrimitives.ReadUInt64LittleEndian(reader.Bytes(8)),
                TypeCode.Single => (double)BinaryPrimitives.ReadSingleLittleEndian(reader.Bytes(4)),
                TypeCode.Double => BinaryPrimitives.ReadDoubleLittleEndian(reader.Bytes(8)),
                TypeCode.String => reader.String(),
                _ => false,
            };

            if (value is false)
                return false;
            if (value is not null)
                values[field.Name] = value;
        }

        return true;
    }

    private static EventField[] ReadFields(ref SpanReader reader, int count)
    {
        var fields = new EventField[count];
        for (var i = 0; i < count; i++)
        {
            var type = (TypeCode)reader.Int32();
            var nested = type == TypeCode.Object ? ReadFields(ref reader, reader.Int32()) : null;
            if ((int)type is < 0 or > (int)TypeCode.String)
                throw new InvalidDataException("Unsupported field type.");
            fields[i] = new EventField(reader.String(), type, nested);
        }

        return fields;
    }
}

/// <summary>Little-endian reads from a byte span, by offset.</summary>
internal ref struct SpanReader(ReadOnlySpan<byte> span)
{
    private readonly ReadOnlySpan<byte> _span = span;

    public int Offset;

    public byte Byte() => _span[Offset++];

    public ReadOnlySpan<byte> Bytes(int count)
    {
        var bytes = _span.Slice(Offset, count);
        Offset += count;
        return bytes;
    }

    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));

    public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));

    /// <summary>Seven bits per byte, low first; the high bit means more follow.</summary>
    public ulong VarUInt()
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = Byte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            if (shift > 63)
                throw new InvalidDataException("Malformed variable-length integer.");
        }
    }

    /// <summary>UTF-16, ending in a null character.</summary>
    public string String()
    {
        var chars = MemoryMarshal.Cast<byte, char>(_span[Offset..]);
        var length = chars.IndexOf('\0');
        if (length < 0)
            throw new InvalidDataException("Unterminated string.");
        Offset += (length + 1) * 2;
        return new string(chars[..length]);
    }
}
