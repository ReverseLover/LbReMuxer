namespace LbReMuxer;

static class WebmPatcher
{
    // Well-known EBML/Matroska element IDs
    const long IdEbmlHeader    = 0x1A45DFA3;
    const long IdSegment       = 0x18538067;
    const long IdInfo          = 0x1549A966;
    const long IdTimestampScale = 0x2AD7B1;
    const long IdDuration      = 0x4489;
    const long IdTracks        = 0x1654AE6B;
    const long IdTrackEntry    = 0xAE;
    const long IdTrackType     = 0x83;
    const long IdVideo         = 0xE0;
    const long IdAudio         = 0xE1;
    const long IdPixelWidth    = 0xB0;
    const long IdPixelHeight   = 0xBA;
    const long IdChannels      = 0x9F;
    const long IdSamplingRate  = 0xB5;
    const long IdCodecPrivate  = 0x63A2;
    const long IdCluster       = 0x1F43B675;
    const long IdSeekHead      = 0x114D9B74;
    const long IdCues          = 0x1C53BB6B;
    const long IdVoid          = 0xEC;

    const ulong TrackTypeVideo = 1;
    const ulong TrackTypeAudio = 2;

    /// <summary>
    /// Produces a new WebM file by combining the structural header from
    /// <paramref name="template"/> with the media clusters from <paramref name="source"/>.
    /// Fields that describe the source content (dimensions, sample rate, codec
    /// private data, duration, …) are patched with values read from the source file.
    /// </summary>
    public static byte[] Patch(byte[] template, byte[] source)
    {
        // --- Parse template ---
        var tmplTop = Ebml.ReadEntries(template);
        var tmplEbmlHeader = tmplTop.First(e => e.Id == IdEbmlHeader);
        var tmplSegment    = tmplTop.First(e => e.Id == IdSegment);
        var tmplChildren   = Ebml.ReadEntries(template, tmplSegment.FileOffset,
            tmplSegment.Size == Ebml.UnknownSize ? -1 : tmplSegment.Size);

        // --- Parse source ---
        var srcTop      = Ebml.ReadEntries(source);
        var srcSegment  = srcTop.First(e => e.Id == IdSegment);
        var srcChildren = Ebml.ReadEntries(source, srcSegment.FileOffset,
            srcSegment.Size == Ebml.UnknownSize ? -1 : srcSegment.Size);

        // --- Extract source metadata ---
        var srcInfo     = srcChildren.First(e => e.Id == IdInfo);
        var srcInfoKids = Ebml.ReadEntries(source, srcInfo.FileOffset, srcInfo.Size);

        var srcTracks     = srcChildren.First(e => e.Id == IdTracks);
        var srcTrackKids  = Ebml.ReadEntries(source, srcTracks.FileOffset, srcTracks.Size);
        var srcTrackList  = srcTrackKids.Where(e => e.Id == IdTrackEntry).ToList();

        var srcClusters = srcChildren.Where(e => e.Id == IdCluster).ToList();

        // --- Build the new segment body ---
        var segmentParts = new List<byte[]>();

        foreach (var child in tmplChildren)
        {
            if (child.Id is IdSeekHead or IdCues or IdVoid or IdCluster)
                continue; // SeekHead/Cues would have stale offsets; clusters come from source

            if (child.Id == IdInfo)
            {
                var tmplInfoKids = Ebml.ReadEntries(template, child.FileOffset, child.Size);
                byte[] infoPayload = PatchInfo(template, tmplInfoKids, source, srcInfoKids);
                segmentParts.Add(Ebml.SerializeElement(IdInfo, infoPayload));
            }
            else if (child.Id == IdTracks)
            {
                var tmplTrackKids = Ebml.ReadEntries(template, child.FileOffset, child.Size);
                var tmplTrackList = tmplTrackKids.Where(e => e.Id == IdTrackEntry).ToList();
                byte[] tracksPayload = PatchTracks(template, tmplTrackList, source, srcTrackList);
                segmentParts.Add(Ebml.SerializeElement(IdTracks, tracksPayload));
            }
            else
            {
                segmentParts.Add(Ebml.CopyRawElement(template, child));
            }
        }

        // Append all clusters verbatim from source
        foreach (var cluster in srcClusters)
            segmentParts.Add(Ebml.CopyRawElement(source, cluster));

        // Combine segment parts and wrap in a Segment element
        byte[] segmentBody = Combine(segmentParts);
        byte[] segmentElement = Ebml.SerializeElement(IdSegment, segmentBody);

        // Copy the EBML header verbatim from the template and prepend it
        byte[] ebmlHeaderBytes = Ebml.CopyRawElement(template, tmplEbmlHeader);
        return Combine([ebmlHeaderBytes, segmentElement]);
    }

    // ------------------------------------------------------------------
    // Patch helpers — each returns the payload bytes (no outer wrapper)
    // ------------------------------------------------------------------

    static byte[] PatchInfo(
        byte[] tmplData, List<EbmlEntry> tmplKids,
        byte[] srcData,  List<EbmlEntry> srcKids)
    {
        var parts = new List<byte[]>();
        foreach (var kid in tmplKids)
        {
            if (kid.Id == IdTimestampScale)
            {
                ulong val = srcKids.First(e => e.Id == IdTimestampScale).ReadUInteger(srcData);
                parts.Add(Ebml.SerializeElement(IdTimestampScale, EncodeUInt(val, (int)kid.Size)));
            }
            else if (kid.Id == IdDuration)
            {
                double val = srcKids.First(e => e.Id == IdDuration).ReadFloatingPoint(srcData);
                parts.Add(Ebml.SerializeElement(IdDuration, EncodeFloat(val, (int)kid.Size)));
            }
            else
            {
                parts.Add(Ebml.CopyRawElement(tmplData, kid));
            }
        }
        return Combine(parts);
    }

    static byte[] PatchTracks(
        byte[] tmplData, List<EbmlEntry> tmplTracks,
        byte[] srcData,  List<EbmlEntry> srcTracks)
    {
        var parts = new List<byte[]>();
        for (int i = 0; i < tmplTracks.Count; i++)
        {
            var tmplEntry = tmplTracks[i];
            var tmplKids  = Ebml.ReadEntries(tmplData, tmplEntry.FileOffset, tmplEntry.Size);

            var trackTypeEntry = tmplKids.FirstOrDefault(e => e.Id == IdTrackType);
            ulong trackType = trackTypeEntry?.ReadUInteger(tmplData) ?? 0;

            // Find the matching source track by type
            EbmlEntry? srcEntry = null;
            List<EbmlEntry>? srcKids = null;
            foreach (var st in srcTracks)
            {
                var stKids = Ebml.ReadEntries(srcData, st.FileOffset, st.Size);
                var stType = stKids.FirstOrDefault(e => e.Id == IdTrackType);
                if (stType != null && stType.ReadUInteger(srcData) == trackType)
                {
                    srcEntry = st;
                    srcKids  = stKids;
                    break;
                }
            }

            if (srcEntry == null || srcKids == null)
            {
                // No matching source track — copy template track verbatim
                parts.Add(Ebml.CopyRawElement(tmplData, tmplEntry));
                continue;
            }

            byte[] entryPayload = PatchTrackEntry(tmplData, tmplKids, srcData, srcKids, trackType);
            parts.Add(Ebml.SerializeElement(IdTrackEntry, entryPayload));
        }
        return Combine(parts);
    }

    static byte[] PatchTrackEntry(
        byte[] tmplData, List<EbmlEntry> tmplKids,
        byte[] srcData,  List<EbmlEntry> srcKids,
        ulong trackType)
    {
        var parts = new List<byte[]>();
        foreach (var kid in tmplKids)
        {
            if (kid.Id == IdVideo && trackType == TrackTypeVideo)
            {
                var tmplVKids = Ebml.ReadEntries(tmplData, kid.FileOffset, kid.Size);
                var srcVEntry = srcKids.FirstOrDefault(e => e.Id == IdVideo);
                if (srcVEntry == null) { parts.Add(Ebml.CopyRawElement(tmplData, kid)); continue; }
                var srcVKids = Ebml.ReadEntries(srcData, srcVEntry.FileOffset, srcVEntry.Size);
                byte[] videoPayload = PatchVideoElement(tmplData, tmplVKids, srcData, srcVKids);
                parts.Add(Ebml.SerializeElement(IdVideo, videoPayload));
            }
            else if (kid.Id == IdAudio && trackType == TrackTypeAudio)
            {
                var tmplAKids = Ebml.ReadEntries(tmplData, kid.FileOffset, kid.Size);
                var srcAEntry = srcKids.FirstOrDefault(e => e.Id == IdAudio);
                if (srcAEntry == null) { parts.Add(Ebml.CopyRawElement(tmplData, kid)); continue; }
                var srcAKids = Ebml.ReadEntries(srcData, srcAEntry.FileOffset, srcAEntry.Size);
                byte[] audioPayload = PatchAudioElement(tmplData, tmplAKids, srcData, srcAKids);
                parts.Add(Ebml.SerializeElement(IdAudio, audioPayload));
            }
            else if (kid.Id == IdCodecPrivate)
            {
                // CodecPrivate at the TrackEntry level (some encoders place it here)
                var srcCp = srcKids.FirstOrDefault(e => e.Id == IdCodecPrivate);
                if (srcCp == null) { parts.Add(Ebml.CopyRawElement(tmplData, kid)); continue; }
                parts.Add(Ebml.SerializeElement(IdCodecPrivate, srcCp.AsSpan(srcData)));
            }
            else
            {
                parts.Add(Ebml.CopyRawElement(tmplData, kid));
            }
        }
        return Combine(parts);
    }

    static byte[] PatchVideoElement(
        byte[] tmplData, List<EbmlEntry> tmplKids,
        byte[] srcData,  List<EbmlEntry> srcKids)
    {
        var parts = new List<byte[]>();
        foreach (var kid in tmplKids)
        {
            if (kid.Id == IdPixelWidth)
            {
                ulong val = srcKids.First(e => e.Id == IdPixelWidth).ReadUInteger(srcData);
                parts.Add(Ebml.SerializeElement(IdPixelWidth, EncodeUInt(val, (int)kid.Size)));
            }
            else if (kid.Id == IdPixelHeight)
            {
                ulong val = srcKids.First(e => e.Id == IdPixelHeight).ReadUInteger(srcData);
                parts.Add(Ebml.SerializeElement(IdPixelHeight, EncodeUInt(val, (int)kid.Size)));
            }
            else
            {
                parts.Add(Ebml.CopyRawElement(tmplData, kid));
            }
        }
        return Combine(parts);
    }

    static byte[] PatchAudioElement(
        byte[] tmplData, List<EbmlEntry> tmplKids,
        byte[] srcData,  List<EbmlEntry> srcKids)
    {
        var parts = new List<byte[]>();
        foreach (var kid in tmplKids)
        {
            if (kid.Id == IdChannels)
            {
                ulong val = srcKids.First(e => e.Id == IdChannels).ReadUInteger(srcData);
                parts.Add(Ebml.SerializeElement(IdChannels, EncodeUInt(val, (int)kid.Size)));
            }
            else if (kid.Id == IdSamplingRate)
            {
                double val = srcKids.First(e => e.Id == IdSamplingRate).ReadFloatingPoint(srcData);
                parts.Add(Ebml.SerializeElement(IdSamplingRate, EncodeFloat(val, (int)kid.Size)));
            }
            else if (kid.Id == IdCodecPrivate)
            {
                var srcCp = srcKids.FirstOrDefault(e => e.Id == IdCodecPrivate);
                if (srcCp == null) { parts.Add(Ebml.CopyRawElement(tmplData, kid)); continue; }
                parts.Add(Ebml.SerializeElement(IdCodecPrivate, srcCp.AsSpan(srcData)));
            }
            else
            {
                parts.Add(Ebml.CopyRawElement(tmplData, kid));
            }
        }
        return Combine(parts);
    }

    // ------------------------------------------------------------------
    // Encoding helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Encodes an unsigned integer as big-endian bytes.
    /// Uses <paramref name="preferredWidth"/> if the value fits, otherwise widens.
    /// </summary>
    static byte[] EncodeUInt(ulong value, int preferredWidth)
    {
        int width = preferredWidth;
        // Widen if the value doesn't fit in the preferred width
        while (width < 8 && value >= (1UL << (width * 8)))
            width++;
        var bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        return bytes;
    }

    /// <summary>
    /// Encodes a double as big-endian EBML float bytes.
    /// Matches the byte width of the template element (4 or 8 bytes).
    /// </summary>
    static byte[] EncodeFloat(double value, int preferredWidth)
    {
        if (preferredWidth <= 4)
        {
            var buf = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(buf, (float)value);
            return buf;
        }
        else
        {
            var buf = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(buf, value);
            return buf;
        }
    }

    /// <summary>Concatenates a list of byte arrays into one.</summary>
    static byte[] Combine(IEnumerable<byte[]> parts)
    {
        var list = parts as IReadOnlyList<byte[]> ?? parts.ToList();
        int total = list.Sum(p => p.Length);
        var result = new byte[total];
        int pos = 0;
        foreach (var part in list)
        {
            part.CopyTo(result, pos);
            pos += part.Length;
        }
        return result;
    }
}
