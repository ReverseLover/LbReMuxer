namespace LbReMuxer;

/// <summary>
/// A single EBML element. Stores its ID, the size of its data payload, the
/// offset in the source stream where that payload begins, and the offset of
/// the element's own header (ID + size VINT).
/// </summary>
record EbmlEntry(long Id, long Size, long FileOffset, long HeaderOffset)
{
    /// <summary>
    /// Interprets this entry's data as a fixed-length UTF-8 string.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public string ReadString(byte[] data) =>
        System.Text.Encoding.UTF8.GetString(data, (int)FileOffset, (int)Size);

    /// <summary>
    /// Interprets this entry's data as a big-endian IEEE 754 single-precision float.
    /// EBML stores floats big-endian, so the bytes are read in reverse on little-endian hosts.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public float ReadFloat(byte[] data) =>
        System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(
            data.AsSpan((int)FileOffset, (int)Size));

    /// <summary>
    /// Interprets this entry's data as a big-endian IEEE 754 double-precision float.
    /// EBML stores floats big-endian, so the bytes are read in reverse on little-endian hosts.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public double ReadDouble(byte[] data) =>
        System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(
            data.AsSpan((int)FileOffset, (int)Size));

    /// <summary>
    /// Interprets this entry's data as an EBML float, dispatching on its size.
    /// EBML permits 0-byte (value 0), 4-byte, and 8-byte floats.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public double ReadFloatingPoint(byte[] data) => Size switch
    {
        0 => 0.0,
        4 => ReadFloat(data),
        8 => ReadDouble(data),
        _ => throw new InvalidDataException($"Invalid EBML float size: {Size} bytes."),
    };

    /// <summary>
    /// Interprets this entry's data as a big-endian unsigned integer.
    /// EBML unsigned integers are 0 to 8 bytes; an empty element decodes to 0.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public ulong ReadUInteger(byte[] data)
    {
        if (Size < 0 || Size > 8)
            throw new InvalidDataException($"Invalid EBML unsigned integer size: {Size} bytes.");

        ulong value = 0;
        for (int i = 0; i < Size; i++)
            value = (value << 8) | data[FileOffset + i];
        return value;
    }

    /// <summary>
    /// Interprets this entry's data as a big-endian two's-complement signed integer.
    /// EBML signed integers are 0 to 8 bytes; an empty element decodes to 0.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public long ReadInteger(byte[] data)
    {
        if (Size < 0 || Size > 8)
            throw new InvalidDataException($"Invalid EBML signed integer size: {Size} bytes.");

        if (Size == 0)
            return 0;

        // Sign-extend from the most significant byte.
        long value = (sbyte)data[FileOffset];
        for (long i = 1; i < Size; i++)
            value = (value << 8) | data[FileOffset + i];
        return value;
    }

    /// <summary>
    /// Interprets this entry's data as an EBML date: an 8-byte signed integer count
    /// of nanoseconds relative to the Matroska epoch (2001-01-01 00:00:00 UTC).
    /// An empty element decodes to the epoch itself.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public DateTime ReadDate(byte[] data)
    {
        if (Size != 0 && Size != 8)
            throw new InvalidDataException($"Invalid EBML date size: {Size} bytes.");

        var epoch = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long nanoseconds = ReadInteger(data);
        return epoch.AddTicks(nanoseconds / 100);
    }

    /// <summary>
    /// Returns this entry's raw data payload as a copied byte array.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public byte[] ReadBinary(byte[] data) =>
        data.AsSpan((int)FileOffset, (int)Size).ToArray();

    /// <summary>
    /// Returns this entry's raw data payload as a span over the source buffer
    /// (no copy). The span is only valid while <paramref name="data"/> is unchanged.
    /// </summary>
    /// <param name="data">The bytestream this entry was read from.</param>
    public ReadOnlySpan<byte> AsSpan(byte[] data) =>
        data.AsSpan((int)FileOffset, (int)Size);
}

static class Ebml
{
    /// <summary>
    /// Sentinel returned for <see cref="EbmlEntry.Size"/> when an element declares
    /// an "unknown" size (all data bits of the size VINT set to 1).
    /// </summary>
    public const long UnknownSize = -1;

    /// <summary>
    /// Reads the sequence of EBML elements contained in <paramref name="data"/>
    /// between <paramref name="offset"/> and <paramref name="offset"/> + <paramref name="length"/>.
    /// </summary>
    /// <remarks>
    /// This walks one level only: after an element header is read, the scan skips
    /// past the element's data to the next sibling. To descend into a master
    /// element, call this again passing that element's <see cref="EbmlEntry.FileOffset"/>
    /// and <see cref="EbmlEntry.Size"/>.
    /// </remarks>
    /// <param name="data">The full EBML bytestream.</param>
    /// <param name="offset">Index to start scanning at. Defaults to the start of the stream.</param>
    /// <param name="length">
    /// Number of bytes to scan. Defaults to the remainder of the stream from <paramref name="offset"/>.
    /// </param>
    public static List<EbmlEntry> ReadEntries(byte[] data, long offset = 0, long length = -1)
    {
        var entries = new List<EbmlEntry>();
        long end = length < 0 ? data.Length : offset + length;
        if (end > data.Length)
            end = data.Length;

        long pos = offset;
        while (pos < end)
        {
            // Element ID: the length-descriptor bits are part of the ID, so keep them.
            long headerOffset = pos;
            long id = ReadVint(data, ref pos, end, keepMarker: true, out _);

            // Data size: strip the length-descriptor bits to get the value.
            long size = ReadVint(data, ref pos, end, keepMarker: false, out bool allOnes);

            // An all-ones data VINT means the size is unknown (e.g. a live-streamed
            // master element). Report it as such; we can't skip past the payload.
            if (allOnes)
                size = UnknownSize;

            entries.Add(new EbmlEntry(id, size, pos, headerOffset));

            if (size == UnknownSize)
                break;

            // Advance past this element's data to the next sibling.
            pos += size;
        }

        return entries;
    }

    /// <summary>
    /// Encodes a size value as an EBML variable-length integer (VINT).
    /// </summary>
    /// <param name="size">The non-negative size value to encode.</param>
    /// <param name="minWidth">
    /// Minimum number of bytes to use. The result is widened to this width even if
    /// the value would fit in fewer bytes (handy for reserving space). 0 picks the
    /// smallest width that fits.
    /// </param>
    /// <returns>The encoded VINT bytes, 1 to 8 bytes long.</returns>
    public static byte[] EncodeSize(long size, int minWidth = 0)
    {
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be non-negative.");

        // Each width w stores 7*w usable value bits; the all-ones pattern is reserved
        // for "unknown size", so the largest representable value is (2^(7*w) - 2).
        int width = Math.Max(minWidth, 1);
        while (width <= 8 && size > (1L << (7 * width)) - 2)
            width++;
        if (width > 8)
            throw new ArgumentOutOfRangeException(nameof(size), "Size too large for an 8-byte VINT.");

        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(size & 0xFF);
            size >>= 8;
        }

        // Set the length-descriptor marker bit (bit 8-width of the first byte).
        bytes[0] |= (byte)(0x80 >> (width - 1));
        return bytes;
    }

    /// <summary>
    /// Decodes an EBML size VINT from <paramref name="data"/>, returning the value
    /// (with the length-descriptor bit stripped) and how many bytes it occupied.
    /// </summary>
    /// <param name="data">Buffer containing the VINT.</param>
    /// <param name="offset">Index of the VINT's first byte.</param>
    /// <returns>
    /// The decoded size and its byte width. The size is <see cref="UnknownSize"/>
    /// when the reserved all-ones encoding is present.
    /// </returns>
    public static (long Size, int Width) DecodeSize(byte[] data, int offset = 0)
    {
        byte first = data[offset];
        if (first == 0)
            throw new InvalidDataException($"Invalid EBML VINT length descriptor at offset {offset}.");

        int width = System.Numerics.BitOperations.LeadingZeroCount((uint)first << 24) + 1;
        if (offset + width > data.Length)
            throw new EndOfStreamException("EBML VINT extends past the end of the buffer.");

        long value = first & (0xFF >> width);
        long allOnesMask = 0xFF >> width;
        for (int i = 1; i < width; i++)
        {
            value = (value << 8) | data[offset + i];
            allOnesMask = (allOnesMask << 8) | 0xFF;
        }

        return (value == allOnesMask ? UnknownSize : value, width);
    }

    /// <summary>
    /// Encodes an element ID as its raw bytes. The ID's VINT width is inferred from
    /// the position of the highest set bit (same encoding used when the ID was read
    /// with keepMarker=true).
    /// </summary>
    public static byte[] EncodeId(long id)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Element ID must be positive.");

        // Width is determined by how many bytes the ID occupies.
        int width = id <= 0xFF ? 1 : id <= 0xFFFF ? 2 : id <= 0xFFFFFF ? 3 : 4;
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(id & 0xFF);
            id >>= 8;
        }
        return bytes;
    }

    /// <summary>
    /// Serializes a complete EBML element: ID bytes + size VINT + payload bytes.
    /// </summary>
    public static byte[] SerializeElement(long id, ReadOnlySpan<byte> payload)
    {
        byte[] idBytes = EncodeId(id);
        byte[] sizeBytes = EncodeSize(payload.Length);
        var result = new byte[idBytes.Length + sizeBytes.Length + payload.Length];
        idBytes.CopyTo(result, 0);
        sizeBytes.CopyTo(result, idBytes.Length);
        payload.CopyTo(result.AsSpan(idBytes.Length + sizeBytes.Length));
        return result;
    }

    /// <summary>
    /// Copies the raw bytes of an element (header + payload) from its source buffer.
    /// </summary>
    public static byte[] CopyRawElement(byte[] data, EbmlEntry entry)
    {
        int start = (int)entry.HeaderOffset;
        int end = (int)(entry.FileOffset + entry.Size);
        return data.AsSpan(start, end - start).ToArray();
    }

    /// <summary>
    /// Reads one EBML variable-length integer starting at <paramref name="pos"/>,
    /// advancing <paramref name="pos"/> past it.
    /// </summary>
    /// <param name="keepMarker">
    /// When true the leading length-descriptor bit is retained in the result
    /// (used for element IDs); when false it is masked off (used for sizes).
    /// </param>
    /// <param name="allOnes">
    /// Set to true when every value bit of the VINT is 1, i.e. the reserved
    /// "unknown size" encoding.
    /// </param>
    static long ReadVint(byte[] data, ref long pos, long end, bool keepMarker, out bool allOnes)
    {
        if (pos >= end)
            throw new EndOfStreamException("Unexpected end of EBML stream while reading a VINT.");

        byte first = data[pos];
        if (first == 0)
            throw new InvalidDataException($"Invalid EBML VINT length descriptor at offset {pos}.");

        // The number of leading zero bits in the first byte gives the extra byte
        // count; the highest set bit is the length marker.
        int marker = System.Numerics.BitOperations.LeadingZeroCount((uint)first << 24);
        int width = marker + 1;
        if (pos + width > end)
            throw new EndOfStreamException("EBML VINT extends past the end of the stream.");

        // Value with the marker bit removed (used to detect the all-ones case and
        // to produce a size value).
        long value = first & (0xFF >> width);
        long allOnesMask = 0xFF >> width;
        for (int i = 1; i < width; i++)
        {
            value = (value << 8) | data[pos + i];
            allOnesMask = (allOnesMask << 8) | 0xFF;
        }

        allOnes = value == allOnesMask;

        long result;
        if (keepMarker)
        {
            // Re-read keeping the marker bit so the ID matches the canonical
            // representation used by Matroska/WebM element tables.
            result = first;
            for (int i = 1; i < width; i++)
                result = (result << 8) | data[pos + i];
        }
        else
        {
            result = value;
        }

        pos += width;
        return result;
    }
}