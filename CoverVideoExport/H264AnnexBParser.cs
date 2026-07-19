using System.IO;

namespace MajdataEdit.CoverVideoExport;

internal readonly record struct H264FrameInfo(long Offset, int Length, bool IsKeyFrame);

internal static class H264AnnexBParser
{
    private const byte AccessUnitDelimiterNalType = 9;
    private const byte IdrPictureNalType = 5;

    public static IReadOnlyList<H264FrameInfo> Parse(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new InvalidDataException("FFmpeg 没有生成有效的 H.264 视频流。");
        }

        var nalUnits = ScanNalUnits(path);
        var accessUnitDelimiters = nalUnits
            .Where(unit => unit.Type == AccessUnitDelimiterNalType)
            .ToArray();
        if (accessUnitDelimiters.Length == 0)
        {
            return new[]
            {
                new H264FrameInfo(
                    0,
                    checked((int)fileInfo.Length),
                    nalUnits.Any(unit => unit.Type == IdrPictureNalType))
            };
        }

        var frames = new List<H264FrameInfo>(accessUnitDelimiters.Length);
        for (var index = 0; index < accessUnitDelimiters.Length; index++)
        {
            var start = index == 0 ? 0 : accessUnitDelimiters[index].Offset;
            var end = index + 1 < accessUnitDelimiters.Length
                ? accessUnitDelimiters[index + 1].Offset
                : fileInfo.Length;
            var isKeyFrame = nalUnits.Any(unit =>
                unit.Offset >= start && unit.Offset < end && unit.Type == IdrPictureNalType);
            frames.Add(new H264FrameInfo(start, checked((int)(end - start)), isKeyFrame));
        }

        if (frames.Count == 0 || frames.Any(frame => frame.Length <= 0))
        {
            throw new InvalidDataException("无法从 H.264 视频流中识别有效帧。");
        }

        if (!frames.Any(frame => frame.IsKeyFrame))
        {
            frames[0] = frames[0] with { IsKeyFrame = true };
        }

        return frames;
    }

    private static IReadOnlyList<H264NalUnit> ScanNalUnits(string path)
    {
        var nalUnits = new List<H264NalUnit>();
        var buffer = new byte[1024 * 1024];
        var zeroCount = 0;
        var awaitingNalHeader = false;
        var nalOffset = 0L;
        var absolutePosition = 0L;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.SequentialScan);
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < bytesRead; index++, absolutePosition++)
            {
                var value = buffer[index];
                if (awaitingNalHeader)
                {
                    nalUnits.Add(new H264NalUnit(nalOffset, (byte)(value & 0x1F)));
                    awaitingNalHeader = false;
                    zeroCount = 0;
                    continue;
                }

                if (value == 0)
                {
                    zeroCount++;
                    continue;
                }

                if (value == 1 && zeroCount >= 2)
                {
                    nalOffset = absolutePosition - zeroCount;
                    awaitingNalHeader = true;
                }

                zeroCount = 0;
            }
        }

        return nalUnits;
    }

    private readonly record struct H264NalUnit(long Offset, byte Type);
}
