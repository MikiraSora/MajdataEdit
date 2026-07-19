using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MajdataEdit.CoverVideoExport;

/// <summary>
/// Writes the small, video-only Sofdec2 container needed by the cover exporter.
/// The layout and packet encryption follow https://github.com/RERASER/WannaCriCS,
/// while this writer only includes the H.264 video path required by MajdataEdit.
/// </summary>
internal static class CriUsmWriter
{
    private const byte CharType = 0x10;
    private const byte ShortType = 0x12;
    private const byte UShortType = 0x13;
    private const byte IntType = 0x14;
    private const byte UIntType = 0x15;
    private const byte LongLongType = 0x16;
    private const byte StringType = 0x1A;

    private const byte StreamPayload = 0;
    private const byte HeaderPayload = 1;
    private const byte SectionEndPayload = 2;
    private const byte MetadataPayload = 3;

    private const int UsmVersion = 16_777_984;

    public static void WriteVideo(
        string h264Path,
        string outputPath,
        IReadOnlyList<H264FrameInfo> sourceFrames,
        int width,
        int height,
        int frameRate,
        ulong key,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(h264Path))
        {
            throw new FileNotFoundException("H.264 视频流不存在。", h264Path);
        }

        if (sourceFrames.Count == 0)
        {
            throw new ArgumentException("H.264 视频流中没有可封装的帧。", nameof(sourceFrames));
        }

        if (width <= 0 || height <= 0 || frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "视频尺寸和帧率必须大于 0。");
        }

        var frames = sourceFrames.ToArray();
        if (!frames.Any(frame => frame.IsKeyFrame))
        {
            frames[0] = frames[0] with { IsKeyFrame = true };
        }

        var h264Length = new FileInfo(h264Path).Length;
        foreach (var frame in frames)
        {
            if (frame.Offset < 0 || frame.Length <= 0 || frame.Offset + frame.Length > h264Length)
            {
                throw new InvalidDataException("H.264 帧索引超出了视频流范围。");
            }
        }

        var streamOffset = 0L;
        var maximumPacketSize = 1;
        var maximumFrameSize = 0;
        var maximumPackedSize = 0;
        var keyFrames = new List<(int Index, long Offset)>();
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = frames[index];
            if (frame.IsKeyFrame)
            {
                keyFrames.Add((index, streamOffset));
            }

            var padding = PaddingToMultiple(frame.Length, 0x20);
            var packedSize = 0x20 + frame.Length + padding;
            maximumPacketSize = Math.Max(maximumPacketSize, packedSize);
            maximumFrameSize = Math.Max(maximumFrameSize, frame.Length);
            maximumPackedSize = Math.Max(maximumPackedSize, 0x18 + frame.Length + padding);
            streamOffset += packedSize;
        }

        var packedFrameRate = checked((uint)(frameRate * 100));
        var contentsEndChunk = PackChunk(
            "@SFV",
            SectionEndPayload,
            Encoding.UTF8.GetBytes("#CONTENTS END   ===============\0"),
            0,
            frameRate: packedFrameRate);
        maximumPacketSize = Math.Max(maximumPacketSize, contentsEndChunk.Length);
        var streamSectionLength = checked(streamOffset + contentsEndChunk.Length);

        var videoHeaderPage = CreateVideoHeaderPage(
            width,
            height,
            frames.Length,
            frameRate,
            keyFrames.Count,
            maximumPackedSize);
        var videoHeaderPayload = PackPages(new[] { videoHeaderPage });
        var videoHeaderChunk = PackChunk("@SFV", HeaderPayload, videoHeaderPayload, 0x18);
        var headerEndChunk = PackChunk(
            "@SFV",
            SectionEndPayload,
            Encoding.UTF8.GetBytes("#HEADER END     ===============\0"),
            0);

        var seekPages = keyFrames.Select(keyFrame =>
                new CriPage("VIDEO_SEEKINFO")
                    .Add("ofs_byte", LongLongType, keyFrame.Offset)
                    .Add("ofs_frmid", UIntType, checked((uint)keyFrame.Index))
                    .Add("num_skip", UShortType, (ushort)0)
                    .Add("resv", UShortType, (ushort)0))
            .ToArray();
        var provisionalSeekPayload = PackPages(seekPages);
        var provisionalSeekChunk = PackChunk(
            "@SFV",
            MetadataPayload,
            provisionalSeekPayload,
            MetadataPadding(0x20 + provisionalSeekPayload.Length));
        var metadataEndChunk = PackChunk(
            "@SFV",
            SectionEndPayload,
            Encoding.UTF8.GetBytes("#METADATA END   ===============\0"),
            0);
        var headerMetadataLength = videoHeaderChunk.Length
                                   + headerEndChunk.Length
                                   + provisionalSeekChunk.Length
                                   + metadataEndChunk.Length;
        var absoluteStreamOffset = 0x800L + headerMetadataLength;
        for (var index = 0; index < seekPages.Length; index++)
        {
            seekPages[index].Set(
                "ofs_byte",
                LongLongType,
                checked(keyFrames[index].Offset + absoluteStreamOffset));
        }

        var seekPayload = PackPages(seekPages);
        var seekChunk = PackChunk(
            "@SFV",
            MetadataPayload,
            seekPayload,
            MetadataPadding(0x20 + seekPayload.Length));
        var headerMetadataSection = Concat(videoHeaderChunk, headerEndChunk, seekChunk, metadataEndChunk);

        var rawVideoSize = checked((int)frames.Sum(frame => (long)frame.Length));
        var videoCridPage = new CriPage("CRIUSF_DIR_STREAM")
            .Add("fmtver", IntType, 0)
            .Add("filename", StringType, Path.GetFileName(h264Path))
            .Add("filesize", IntType, rawVideoSize)
            .Add("datasize", IntType, 0)
            .Add("stmid", IntType, 0x40534656)
            .Add("chno", ShortType, (short)0)
            .Add("minchk", ShortType, (short)3)
            .Add("minbuf", IntType, maximumFrameSize)
            .Add("avbps", IntType, 0);

        var roundedMinimumBuffer = (int)Math.Round(maximumPacketSize * 1.98746);
        roundedMinimumBuffer += PaddingToMultiple(roundedMinimumBuffer, 0x10);
        var declaredFileSize = checked((int)(0x1000L + headerMetadataSection.Length + streamSectionLength));
        var usmCridPage = new CriPage("CRIUSF_DIR_STREAM")
            .Add("fmtver", IntType, UsmVersion)
            .Add("filename", StringType, "cover.usm")
            .Add("filesize", IntType, declaredFileSize)
            .Add("datasize", IntType, 0)
            .Add("stmid", IntType, 0)
            .Add("chno", ShortType, (short)-1)
            .Add("minchk", ShortType, (short)1)
            .Add("minbuf", IntType, roundedMinimumBuffer)
            .Add("avbps", IntType, 0);

        var infoPayload = PackPages(new[] { usmCridPage, videoCridPage });
        var infoChunk = PackChunk(
            "CRID",
            HeaderPayload,
            infoPayload,
            PaddingToMultiple(0x20 + infoPayload.Length, 0x800));

        var videoKey = GenerateVideoKey(key);
        using var input = new FileStream(h264Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(infoChunk);
        output.Write(headerMetadataSection);
        for (var index = 0; index < frames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[index];
            input.Position = frame.Offset;
            var frameData = ReadExactly(input, frame.Length);
            var encryptedFrame = EncryptVideoPacket(frameData, videoKey);
            var streamChunk = PackChunk(
                "@SFV",
                StreamPayload,
                encryptedFrame,
                PaddingToMultiple(encryptedFrame.Length, 0x20),
                frameTime: checked((uint)(index * 99.9)),
                frameRate: packedFrameRate);
            output.Write(streamChunk);
        }

        output.Write(contentsEndChunk);
        output.Flush();
    }

    private static CriPage CreateVideoHeaderPage(
        int width,
        int height,
        int totalFrames,
        int frameRate,
        int keyFrameCount,
        int maximumPackedSize)
    {
        return new CriPage("VIDEO_HDRINFO")
            .Add("width", IntType, width)
            .Add("height", IntType, height)
            .Add("mat_width", IntType, width)
            .Add("mat_height", IntType, height)
            .Add("disp_width", IntType, width)
            .Add("disp_height", IntType, height)
            .Add("scrn_width", IntType, 0)
            .Add("mpeg_dcprec", CharType, (sbyte)11)
            .Add("mpeg_codec", CharType, (sbyte)5)
            .Add("alpha_type", IntType, 0)
            .Add("total_frames", IntType, totalFrames)
            .Add("framerate_n", IntType, checked(frameRate * 1000))
            .Add("framerate_d", IntType, 1000)
            .Add("metadata_count", IntType, 1)
            .Add("metadata_size", IntType, keyFrameCount)
            .Add("ixsize", IntType, maximumPackedSize)
            .Add("pre_padding", IntType, 0)
            .Add("max_picture_size", IntType, 0)
            .Add("color_space", IntType, 0)
            .Add("picture_type", IntType, 0);
    }

    private static byte[] PackChunk(
        string signature,
        byte payloadType,
        byte[] payload,
        int padding,
        byte channelNumber = 0,
        uint frameTime = 0,
        uint frameRate = 30)
    {
        if (padding is < 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("USM 分块填充长度超出范围。");
        }

        using var stream = new MemoryStream(0x20 + payload.Length + padding);
        WriteAscii(stream, signature);
        WriteUInt32(stream, checked((uint)(0x18 + payload.Length + padding)));
        stream.WriteByte(0);
        stream.WriteByte(0x18);
        WriteUInt16(stream, (ushort)padding);
        stream.WriteByte(channelNumber);
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte(payloadType);
        WriteUInt32(stream, frameTime);
        WriteUInt32(stream, frameRate);
        stream.Write(new byte[8]);
        stream.Write(payload);
        if (padding > 0)
        {
            stream.Write(new byte[padding]);
        }

        return stream.ToArray();
    }

    private static byte[] PackPages(IReadOnlyList<CriPage> pages)
    {
        if (pages.Count == 0)
        {
            return Array.Empty<byte>();
        }

        var first = pages[0];
        if (pages.Any(page => page.Name != first.Name || page.Elements.Count != first.Elements.Count))
        {
            throw new InvalidOperationException("USM 页面结构不一致。");
        }

        var elementNames = first.Elements.Select(element => element.Name).ToArray();
        if (pages.Any(page => !page.Elements.Select(element => element.Name).SequenceEqual(elementNames)))
        {
            throw new InvalidOperationException("USM 页面字段顺序不一致。");
        }

        var strings = new List<byte>();
        AddNullTerminatedString(strings, "<NULL>", Encoding.UTF8);
        var pageNameOffset = strings.Count;
        AddNullTerminatedString(strings, first.Name, Encoding.UTF8);

        var nameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in first.Elements)
        {
            nameOffsets[element.Name] = strings.Count;
            AddNullTerminatedString(strings, element.Name, Encoding.UTF8);
        }

        var commonElements = new HashSet<string>(StringComparer.Ordinal);
        if (pages.Count > 1)
        {
            foreach (var element in first.Elements)
            {
                if (pages.Skip(1).All(page => page.Get(element.Name).Equals(element.Value)))
                {
                    commonElements.Add(element.Name);
                }
            }
        }

        var shared = new List<byte>();
        var unique = new List<byte>();
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            foreach (var element in pages[pageIndex].Elements)
            {
                if (commonElements.Contains(element.Name))
                {
                    if (pageIndex != 0)
                    {
                        continue;
                    }

                    shared.Add((byte)(element.Value.Type | 0x20));
                    AddUInt32(shared, (uint)nameOffsets[element.Name]);
                    AddElementValue(shared, element.Value, strings);
                    continue;
                }

                if (pageIndex == 0)
                {
                    shared.Add((byte)(element.Value.Type | 0x40));
                    AddUInt32(shared, (uint)nameOffsets[element.Name]);
                }

                AddElementValue(unique, element.Value, strings);
            }
        }

        var dataSize = 24 + shared.Count + unique.Count + strings.Count;
        var uniqueOffset = 24 + shared.Count;
        var stringsOffset = uniqueOffset + unique.Count;
        var byteArrayOffset = stringsOffset + strings.Count;

        using var result = new MemoryStream(8 + dataSize);
        WriteAscii(result, "@UTF");
        WriteUInt32(result, (uint)dataSize);
        WriteUInt32(result, (uint)uniqueOffset);
        WriteUInt32(result, (uint)stringsOffset);
        WriteUInt32(result, (uint)byteArrayOffset);
        WriteUInt32(result, (uint)pageNameOffset);
        WriteUInt16(result, checked((ushort)first.Elements.Count));
        WriteUInt16(result, checked((ushort)(unique.Count / pages.Count)));
        WriteUInt32(result, checked((uint)pages.Count));
        result.Write(shared.ToArray());
        result.Write(unique.ToArray());
        result.Write(strings.ToArray());
        return result.ToArray();
    }

    private static void AddElementValue(List<byte> target, CriElement value, List<byte> strings)
    {
        switch (value.Type)
        {
            case CharType:
                target.Add(unchecked((byte)(sbyte)value.Value));
                break;
            case ShortType:
                AddUInt16(target, unchecked((ushort)(short)value.Value));
                break;
            case UShortType:
                AddUInt16(target, (ushort)value.Value);
                break;
            case IntType:
                AddUInt32(target, unchecked((uint)(int)value.Value));
                break;
            case UIntType:
                AddUInt32(target, (uint)value.Value);
                break;
            case LongLongType:
                AddUInt64(target, unchecked((ulong)(long)value.Value));
                break;
            case StringType:
                AddUInt32(target, checked((uint)strings.Count));
                AddNullTerminatedString(strings, (string)value.Value, Encoding.UTF8);
                break;
            default:
                throw new NotSupportedException($"不支持的 USM 字段类型：0x{value.Type:X2}");
        }
    }

    private static byte[] GenerateVideoKey(ulong keyNumber)
    {
        Span<byte> cipherKey = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(cipherKey, keyNumber);

        var key = new byte[0x20];
        key[0x00] = cipherKey[0];
        key[0x01] = cipherKey[1];
        key[0x02] = cipherKey[2];
        key[0x03] = unchecked((byte)(cipherKey[3] - 0x34));
        key[0x04] = unchecked((byte)(cipherKey[4] + 0xF9));
        key[0x05] = (byte)(cipherKey[5] ^ 0x13);
        key[0x06] = unchecked((byte)(cipherKey[6] + 0x61));
        key[0x07] = (byte)(key[0x00] ^ 0xFF);
        key[0x08] = unchecked((byte)(key[0x01] + key[0x02]));
        key[0x09] = unchecked((byte)(key[0x01] - key[0x07]));
        key[0x0A] = (byte)(key[0x02] ^ 0xFF);
        key[0x0B] = (byte)(key[0x01] ^ 0xFF);
        key[0x0C] = unchecked((byte)(key[0x0B] + key[0x09]));
        key[0x0D] = unchecked((byte)(key[0x08] - key[0x03]));
        key[0x0E] = (byte)(key[0x0D] ^ 0xFF);
        key[0x0F] = unchecked((byte)(key[0x0A] - key[0x0B]));
        key[0x10] = unchecked((byte)(key[0x08] - key[0x0F]));
        key[0x11] = (byte)(key[0x10] ^ key[0x07]);
        key[0x12] = (byte)(key[0x0F] ^ 0xFF);
        key[0x13] = (byte)(key[0x03] ^ 0x10);
        key[0x14] = unchecked((byte)(key[0x04] - 0x32));
        key[0x15] = unchecked((byte)(key[0x05] + 0xED));
        key[0x16] = (byte)(key[0x06] ^ 0xF3);
        key[0x17] = unchecked((byte)(key[0x13] - key[0x0F]));
        key[0x18] = unchecked((byte)(key[0x15] + key[0x07]));
        key[0x19] = unchecked((byte)(0x21 - key[0x13]));
        key[0x1A] = (byte)(key[0x14] ^ key[0x17]);
        key[0x1B] = unchecked((byte)(key[0x16] + key[0x16]));
        key[0x1C] = unchecked((byte)(key[0x17] + 0x44));
        key[0x1D] = unchecked((byte)(key[0x03] + key[0x04]));
        key[0x1E] = unchecked((byte)(key[0x05] - key[0x16]));
        key[0x1F] = (byte)(key[0x1D] ^ key[0x13]);

        var videoKey = new byte[0x40];
        for (var i = 0; i < 0x20; i++)
        {
            videoKey[i] = key[i];
            videoKey[0x20 + i] = (byte)(key[i] ^ 0xFF);
        }

        return videoKey;
    }

    private static byte[] EncryptVideoPacket(byte[] packet, byte[] videoKey)
    {
        var data = packet.ToArray();
        if (data.Length < 0x240)
        {
            return data;
        }

        var encryptedPartSize = data.Length - 0x40;
        var rolling = videoKey.ToArray();
        for (var i = 0; i < 0x100; i++)
        {
            rolling[i % 0x20] ^= data[0x140 + i];
            data[0x40 + i] ^= rolling[i % 0x20];
        }

        for (var i = 0x100; i < encryptedPartSize; i++)
        {
            var plainByte = data[0x40 + i];
            data[0x40 + i] ^= rolling[0x20 + i % 0x20];
            rolling[0x20 + i % 0x20] = (byte)(plainByte ^ videoKey[0x20 + i % 0x20]);
        }

        return data;
    }

    private static int MetadataPadding(int size)
    {
        return size <= 0xF0 ? 0xF0 - size : PaddingToMultiple(size, 0x8);
    }

    private static int PaddingToMultiple(int size, int multiple)
    {
        var remainder = size % multiple;
        return remainder == 0 ? 0 : multiple - remainder;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(array => array.Length)];
        var offset = 0;
        foreach (var array in arrays)
        {
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }

        return result;
    }

    private static byte[] ReadExactly(Stream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("H.264 视频帧数据不完整。");
            }

            offset += read;
        }

        return buffer;
    }

    private static void AddNullTerminatedString(List<byte> target, string value, Encoding encoding)
    {
        target.AddRange(encoding.GetBytes(value));
        target.Add(0);
    }

    private static void AddUInt16(List<byte> target, ushort value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)value);
    }

    private static void AddUInt32(List<byte> target, uint value)
    {
        target.Add((byte)(value >> 24));
        target.Add((byte)(value >> 16));
        target.Add((byte)(value >> 8));
        target.Add((byte)value);
    }

    private static void AddUInt64(List<byte> target, ulong value)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            target.Add((byte)(value >> shift));
        }
    }

    private static void WriteAscii(Stream stream, string value)
    {
        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed class CriPage
    {
        public CriPage(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public List<NamedCriElement> Elements { get; } = new();

        public CriPage Add(string name, byte type, object value)
        {
            Elements.Add(new NamedCriElement(name, new CriElement(type, value)));
            return this;
        }

        public CriElement Get(string name)
        {
            return Elements.First(element => element.Name == name).Value;
        }

        public void Set(string name, byte type, object value)
        {
            var index = Elements.FindIndex(element => element.Name == name);
            if (index < 0)
            {
                throw new KeyNotFoundException(name);
            }

            Elements[index] = new NamedCriElement(name, new CriElement(type, value));
        }
    }

    private readonly record struct NamedCriElement(string Name, CriElement Value);

    private readonly record struct CriElement(byte Type, object Value);
}
