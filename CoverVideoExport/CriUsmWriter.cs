using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MajdataEdit.CoverVideoExport;

/// <summary>
/// Writes the small, video-only Sofdec2 container needed by the cover exporter.
/// The layout and packet encryption follow https://github.com/RERASER/WannaCriCS,
/// while this writer intentionally supports only one H.264 key frame.
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

    public static byte[] CreateSingleFrame(byte[] h264Frame, int width, int height, ulong key)
    {
        ArgumentNullException.ThrowIfNull(h264Frame);
        if (h264Frame.Length == 0)
        {
            throw new ArgumentException("H.264 数据为空。", nameof(h264Frame));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "视频尺寸必须大于 0。");
        }

        var encryptedFrame = EncryptVideoPacket(h264Frame, GenerateVideoKey(key));
        var streamPadding = PaddingToMultiple(encryptedFrame.Length, 0x20);
        var streamChunk = PackChunk("@SFV", StreamPayload, encryptedFrame, streamPadding, frameRate: 100);
        var contentsEndChunk = PackChunk(
            "@SFV",
            SectionEndPayload,
            Encoding.UTF8.GetBytes("#CONTENTS END   ===============\0"),
            0,
            frameRate: 100);
        var streamSection = Concat(streamChunk, contentsEndChunk);
        var maxPacketSize = Math.Max(streamChunk.Length, contentsEndChunk.Length);

        var videoHeaderPage = CreateVideoHeaderPage(width, height, h264Frame.Length, streamPadding);
        var videoHeaderPayload = PackPages(new[] { videoHeaderPage });
        var videoHeaderChunk = PackChunk("@SFV", HeaderPayload, videoHeaderPayload, 0x18);
        var headerEndChunk = PackChunk(
            "@SFV",
            SectionEndPayload,
            Encoding.UTF8.GetBytes("#HEADER END     ===============\0"),
            0);

        var seekPage = new CriPage("VIDEO_SEEKINFO")
            .Add("ofs_byte", LongLongType, 0L)
            .Add("ofs_frmid", UIntType, 0U)
            .Add("num_skip", UShortType, (ushort)0)
            .Add("resv", UShortType, (ushort)0);
        var initialSeekPayload = PackPages(new[] { seekPage });
        var initialSeekPadding = MetadataPadding(0x20 + initialSeekPayload.Length);
        var initialSeekChunkLength = 0x20 + initialSeekPayload.Length + initialSeekPadding;
        var metadataEndChunk = PackChunk(
            "@SFV",
            SectionEndPayload,
            Encoding.UTF8.GetBytes("#METADATA END   ===============\0"),
            0);

        var streamOffset = 0x800L
                           + videoHeaderChunk.Length
                           + headerEndChunk.Length
                           + initialSeekChunkLength
                           + metadataEndChunk.Length;
        seekPage.Set("ofs_byte", LongLongType, streamOffset);
        var seekPayload = PackPages(new[] { seekPage });
        var seekChunk = PackChunk(
            "@SFV",
            MetadataPayload,
            seekPayload,
            MetadataPadding(0x20 + seekPayload.Length));
        var headerMetadataSection = Concat(videoHeaderChunk, headerEndChunk, seekChunk, metadataEndChunk);

        var videoCridPage = new CriPage("CRIUSF_DIR_STREAM")
            .Add("fmtver", IntType, 0)
            .Add("filename", StringType, "cover.h264")
            .Add("filesize", IntType, h264Frame.Length)
            .Add("datasize", IntType, 0)
            .Add("stmid", IntType, 0x40534656)
            .Add("chno", ShortType, (short)0)
            .Add("minchk", ShortType, (short)3)
            .Add("minbuf", IntType, h264Frame.Length)
            .Add("avbps", IntType, 0);

        var roundedMinimumBuffer = (int)Math.Round(maxPacketSize * 1.98746);
        roundedMinimumBuffer += PaddingToMultiple(roundedMinimumBuffer, 0x10);
        var declaredFileSize = 0x1000 + headerMetadataSection.Length + streamSection.Length;
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

        return Concat(infoChunk, headerMetadataSection, streamSection);
    }

    private static CriPage CreateVideoHeaderPage(int width, int height, int frameSize, int framePadding)
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
            .Add("total_frames", IntType, 1)
            .Add("framerate_n", IntType, 1000)
            .Add("framerate_d", IntType, 1000)
            .Add("metadata_count", IntType, 1)
            .Add("metadata_size", IntType, 1)
            .Add("ixsize", IntType, 0x18 + frameSize + framePadding)
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
