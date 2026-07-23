using System.Globalization;
using MajSimai;

namespace MajdataEdit;

internal readonly struct SlideSegmentTiming
{
    public double StartTime { get; }
    public double Duration { get; }

    public SlideSegmentTiming(double startTime, double duration)
    {
        StartTime = startTime;
        Duration = duration;
    }
}

internal static class SlideSegmentTimingAnalysis
{
    public static SlideSegmentTiming[] Analyze(SimaiTimingPoint timingPoint, SimaiNote note)
    {
        if (note.Type != SimaiNoteType.Slide || string.IsNullOrWhiteSpace(note.RawContent))
        {
            return Array.Empty<SlideSegmentTiming>();
        }

        var durationBodies = ParseDurationBodies(note.RawContent);
        if (durationBodies.Count == 0)
        {
            throw new InvalidOperationException("Slide 轨迹拆分结果为空。");
        }

        var durationCount = durationBodies.Count(body => body is not null);
        var durations = new double[durationBodies.Count];
        if (durationBodies.Count == 1 || durationCount == 1)
        {
            var duration = note.SlideTime / durationBodies.Count;
            Array.Fill(durations, duration);
        }
        else if (durationCount == durationBodies.Count)
        {
            for (var i = 0; i < durationBodies.Count; i++)
            {
                durations[i] = ParseDuration(durationBodies[i]!, timingPoint.Bpm);
            }
        }
        else
        {
            throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
        }

        var result = new SlideSegmentTiming[durations.Length];
        var startTime = note.SlideStartTime;
        for (var i = 0; i < durations.Length; i++)
        {
            result[i] = new SlideSegmentTiming(startTime, durations[i]);
            startTime += durations[i];
        }

        return result;
    }

    private static List<string?> ParseDurationBodies(string noteContent)
    {
        var result = new List<string?>();
        var cursor = 1;
        while (cursor < noteContent.Length)
        {
            var mark = noteContent[cursor++];
            if (!IsSlideMark(mark))
            {
                throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
            }

            if ((mark == 'p' || mark == 'q') && cursor < noteContent.Length && noteContent[cursor] == mark)
            {
                cursor++;
            }

            var targetDigitCount = mark == 'V' ? 2 : 1;
            for (var i = 0; i < targetDigitCount; i++)
            {
                if (cursor >= noteContent.Length || noteContent[cursor] < '1' || noteContent[cursor] > '8')
                {
                    throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
                }
                cursor++;
            }

            string? durationBody = null;
            if (cursor < noteContent.Length && noteContent[cursor] == '[')
            {
                var closeIndex = noteContent.IndexOf(']', cursor + 1);
                if (closeIndex < 0)
                {
                    throw new InvalidOperationException("星星时长参数缺少 ]。");
                }
                durationBody = noteContent[(cursor + 1)..closeIndex];
                cursor = closeIndex + 1;
            }

            result.Add(durationBody);
        }

        return result;
    }

    private static double ParseDuration(string body, float currentBpm)
    {
        var parts = body.Split('#');
        return parts.Length switch
        {
            1 => ParseRatio(parts[0], currentBpm),
            2 => parts[1].Contains(':')
                ? ParseRatio(parts[1], ParsePositive(parts[0]))
                : ParseNonNegative(parts[1]),
            3 when parts[1].Length == 0 => parts[2].Contains(':')
                ? ParseRatio(parts[2], currentBpm)
                : ParseNonNegative(parts[2]),
            4 when parts[1].Length == 0 => ParseRatio(parts[3], ParsePositive(parts[2])),
            _ => throw new InvalidOperationException("星星时长参数不合法。")
        };
    }

    private static double ParseRatio(string ratio, double bpm)
    {
        var parts = ratio.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var divide) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            divide <= 0 || count < 0 || bpm <= 0)
        {
            throw new InvalidOperationException("星星时长比例不合法。");
        }

        return 60d / bpm * 4d / divide * count;
    }

    private static double ParsePositive(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || result <= 0)
        {
            throw new InvalidOperationException("星星 BPM 参数不合法。");
        }
        return result;
    }

    private static double ParseNonNegative(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || result < 0)
        {
            throw new InvalidOperationException("星星时长参数不合法。");
        }
        return result;
    }

    private static bool IsSlideMark(char value)
    {
        return value is '-' or '^' or 'v' or '<' or '>' or 'V' or 'p' or 'q' or 's' or 'z' or 'w';
    }
}
