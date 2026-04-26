using Coosu.Beatmap;
using Coosu.Beatmap.Sections;
using Coosu.Beatmap.Sections.HitObject;
using Coosu.Beatmap.Sections.Timing;
using System.Globalization;
using System.IO;
using System.Text;

namespace MajdataEdit;

internal static class ImportTimingFromOsuBeatmap
{
    private const double TimeEpsilon = 0.0000001d;
    private const double EventSnapEpsilon = 0.0000001d;
    private const double BpmIntegerSnapEpsilon = 0.006d;

    public static string ImportTiming(string osuFilePath)
    {
        return ImportTimingAndHitObjects(osuFilePath);
    }

    public static string ImportTimingAndHitObjects(string osuFilePath)
    {
        if (string.IsNullOrWhiteSpace(osuFilePath))
        {
            throw new ArgumentException("osu file path cannot be empty.", nameof(osuFilePath));
        }

        if (!File.Exists(osuFilePath))
        {
            throw new FileNotFoundException("osu beatmap file was not found.", osuFilePath);
        }

        OsuFile beatmap;
        try
        {
            beatmap = OsuFile.ReadFromFile(osuFilePath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse osu beatmap file: {osuFilePath}", ex);
        }

        var timingList = beatmap.TimingPoints?.TimingList
            ?? throw new InvalidDataException("osu beatmap has no TimingPoints section.");
        if (timingList.Count == 0)
        {
            throw new InvalidDataException("osu beatmap has no TimingPoints.");
        }

        var redTimingPoints = timingList
            .Select((point, index) => new IndexedTimingPoint(point, index))
            .Where(x => !x.Point.IsInherit && IsValidBpm(x.Point.Bpm))
            .OrderBy(x => x.Point.Offset)
            .ThenBy(x => x.Index)
            .GroupBy(x => x.Point.Offset)
            .Select(x => x.Last().Point)
            .OrderBy(x => x.Offset)
            .ToList();

        if (redTimingPoints.Count == 0)
        {
            throw new InvalidDataException("osu beatmap has no valid BPM timing point (uninherited red line).");
        }

        var hitObjectList = beatmap.HitObjects?.HitObjectList ?? new List<RawHitObject>();
        ComputeSliderDurations(beatmap.HitObjects, hitObjectList);
        var hitObjects = ConvertHitObjects(hitObjectList);

        var cutoffTime = Math.Max(0d, timingList.Max(x => x.Offset) / 1000d);
        if (hitObjects.Count > 0)
        {
            cutoffTime = Math.Max(cutoffTime, hitObjects.Max(x => x.EndTime));
        }

        var initialBpmTiming = redTimingPoints.LastOrDefault(x => x.Offset <= 0d) ?? redTimingPoints[0];
        var initialSignatureTiming = redTimingPoints.LastOrDefault(x => x.Offset <= 0d && x.Rhythm > 0);

        var builder = new StringBuilder();
        var currentBpm = NormalizeBpm(initialBpmTiming.Bpm);
        var currentRhythm = 4;
        var beatMode = BeatMode.Quarter;

        AppendBpm(builder, currentBpm);
        if (initialSignatureTiming is not null && initialSignatureTiming.Rhythm != currentRhythm)
        {
            AppendSignature(builder, initialSignatureTiming.Rhythm);
            currentRhythm = initialSignatureTiming.Rhythm;
        }

        AppendQuarterInterval(builder);

        var events = redTimingPoints
            .Where(x => x.Offset > 0d)
            .Select(x => new TimingEvent(x.Offset / 1000d, NormalizeBpm(x.Bpm), x.Rhythm))
            .Where(x => x.Time <= cutoffTime + TimeEpsilon)
            .ToList();

        var currentTime = 0d;
        var eventIndex = 0;
        var hitObjectIndex = 0;

        while (currentTime < cutoffTime - TimeEpsilon)
        {
            ApplyEventsAtCurrentTime(events, ref eventIndex, currentTime, builder, ref currentBpm, ref currentRhythm);
            var hitObjectText = TakeHitObjectsAtCurrentTime(hitObjects, ref hitObjectIndex, currentTime);

            var nextStopTime = GetNextStopTime(cutoffTime, events, eventIndex, hitObjects, hitObjectIndex);

            if (nextStopTime <= currentTime + TimeEpsilon)
            {
                currentTime = Math.Max(currentTime, nextStopTime);
                continue;
            }

            var quarterInterval = 60d / currentBpm;
            var distanceToNextStop = nextStopTime - currentTime;
            var isNearQuarterStop = Math.Abs(distanceToNextStop - quarterInterval) <= EventSnapEpsilon;
            var interval = distanceToNextStop < quarterInterval || isNearQuarterStop
                ? distanceToNextStop
                : quarterInterval;
            var forceSecondsInterval = isNearQuarterStop && Math.Abs(distanceToNextStop - quarterInterval) > TimeEpsilon;

            AppendIntervalComma(builder, interval, quarterInterval, hitObjectText, forceSecondsInterval, ref beatMode);
            currentTime += interval;

            if (Math.Abs(currentTime - nextStopTime) <= TimeEpsilon)
            {
                currentTime = nextStopTime;
            }
        }

        ApplyEventsAtCurrentTime(events, ref eventIndex, currentTime, builder, ref currentBpm, ref currentRhythm);
        var finalHitObjectText = TakeHitObjectsAtCurrentTime(hitObjects, ref hitObjectIndex, currentTime);
        if (!string.IsNullOrEmpty(finalHitObjectText))
        {
            AppendSecondsInterval(builder, 0d);
            beatMode = BeatMode.Seconds;
            builder.Append(finalHitObjectText);
            builder.AppendLine(",");
        }

        return builder.ToString().TrimEnd();
    }

    private static void ComputeSliderDurations(HitObjectSection? hitObjectSection, IReadOnlyList<RawHitObject> hitObjects)
    {
        if (hitObjectSection is null || !hitObjects.Any(x => x.ObjectType == HitObjectType.Slider))
        {
            return;
        }

        try
        {
            hitObjectSection.ComputeSlidersByCurrentSettings();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Failed to compute osu slider durations.", ex);
        }
    }

    private static List<ConvertedHitObject> ConvertHitObjects(IEnumerable<RawHitObject> hitObjects)
    {
        return hitObjects
            .Select((hitObject, index) => ConvertHitObject(hitObject, index))
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderBy(x => x.Time)
            .ThenBy(x => x.Index)
            .ToList();
    }

    private static ConvertedHitObject? ConvertHitObject(RawHitObject hitObject, int index)
    {
        var time = Math.Max(0d, hitObject.Offset / 1000d);
        var position = MapOsuPositionToSimaiPosition(hitObject.X, hitObject.Y);

        return hitObject.ObjectType switch
        {
            HitObjectType.Circle => new ConvertedHitObject(time, time, position.ToString(CultureInfo.InvariantCulture), index),
            HitObjectType.Slider => ConvertSliderToHold(hitObject, position, time, index),
            _ => null
        };
    }

    private static ConvertedHitObject ConvertSliderToHold(RawHitObject hitObject, int position, double time, int index)
    {
        var duration = GetSliderDurationSeconds(hitObject);
        var text = position.ToString(CultureInfo.InvariantCulture) + "h[#" + FormatSeconds(duration) + "]";

        return new ConvertedHitObject(time, time + duration, text, index);
    }

    private static double GetSliderDurationSeconds(RawHitObject hitObject)
    {
        var sliderInfo = hitObject.SliderInfo;
        var durationMs = 0d;

        if (sliderInfo is not null)
        {
            if (sliderInfo.CurrentDuration > TimeEpsilon)
            {
                durationMs = sliderInfo.CurrentDuration;
            }
            else if (sliderInfo.CurrentEndTime > hitObject.Offset)
            {
                durationMs = sliderInfo.CurrentEndTime - hitObject.Offset;
            }
        }

        return Math.Max(0d, durationMs / 1000d);
    }

    private static int MapOsuPositionToSimaiPosition(float x, float y)
    {
        var angle = Math.Atan2(y - 192d, x - 256d) * 180d / Math.PI;
        var normalized = (angle + 90d + 360d) % 360d;
        var sector = (int)Math.Floor((normalized + 22.5d) / 45d) % 8;

        return sector + 1;
    }

    private static string TakeHitObjectsAtCurrentTime(
        IReadOnlyList<ConvertedHitObject> hitObjects,
        ref int hitObjectIndex,
        double currentTime)
    {
        if (hitObjectIndex >= hitObjects.Count || hitObjects[hitObjectIndex].Time > currentTime + TimeEpsilon)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        while (hitObjectIndex < hitObjects.Count && hitObjects[hitObjectIndex].Time <= currentTime + TimeEpsilon)
        {
            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(hitObjects[hitObjectIndex].Text);
            hitObjectIndex++;
        }

        return builder.ToString();
    }

    private static double GetNextStopTime(
        double cutoffTime,
        IReadOnlyList<TimingEvent> events,
        int eventIndex,
        IReadOnlyList<ConvertedHitObject> hitObjects,
        int hitObjectIndex)
    {
        var nextStopTime = cutoffTime;

        if (eventIndex < events.Count)
        {
            nextStopTime = Math.Min(nextStopTime, events[eventIndex].Time);
        }

        if (hitObjectIndex < hitObjects.Count)
        {
            nextStopTime = Math.Min(nextStopTime, hitObjects[hitObjectIndex].Time);
        }

        return nextStopTime;
    }

    private static void ApplyEventsAtCurrentTime(
        IReadOnlyList<TimingEvent> events,
        ref int eventIndex,
        double currentTime,
        StringBuilder builder,
        ref double currentBpm,
        ref int currentRhythm)
    {
        while (eventIndex < events.Count && events[eventIndex].Time <= currentTime + TimeEpsilon)
        {
            var timingEvent = events[eventIndex];

            if (Math.Abs(timingEvent.Bpm - currentBpm) > double.Epsilon)
            {
                AppendBpm(builder, timingEvent.Bpm);
                currentBpm = timingEvent.Bpm;
            }

            if (timingEvent.Rhythm > 0 && timingEvent.Rhythm != currentRhythm)
            {
                AppendSignature(builder, timingEvent.Rhythm);
                currentRhythm = timingEvent.Rhythm;
            }

            eventIndex++;
        }
    }

    private static void AppendIntervalComma(
        StringBuilder builder,
        double interval,
        double quarterInterval,
        string hitObjectText,
        bool forceSecondsInterval,
        ref BeatMode beatMode)
    {
        if (!forceSecondsInterval && Math.Abs(interval - quarterInterval) <= TimeEpsilon)
        {
            if (beatMode != BeatMode.Quarter)
            {
                AppendQuarterInterval(builder);
                beatMode = BeatMode.Quarter;
            }
        }
        else
        {
            AppendSecondsInterval(builder, interval);
            beatMode = BeatMode.Seconds;
        }

        builder.Append(hitObjectText);
        builder.AppendLine(",");
    }

    private static bool IsValidBpm(double bpm)
    {
        return bpm > 0d && !double.IsNaN(bpm) && !double.IsInfinity(bpm);
    }

    private static double NormalizeBpm(double bpm)
    {
        var rounded = Math.Round(bpm);
        return Math.Abs(bpm - rounded) <= BpmIntegerSnapEpsilon ? rounded : bpm;
    }

    private static void AppendBpm(StringBuilder builder, double bpm)
    {
        builder.Append('(');
        builder.Append(FormatBpm(bpm));
        builder.AppendLine(")");
    }

    private static void AppendQuarterInterval(StringBuilder builder)
    {
        builder.AppendLine("{4}");
    }

    private static void AppendSecondsInterval(StringBuilder builder, double seconds)
    {
        builder.Append("{#");
        builder.Append(FormatSeconds(seconds));
        builder.AppendLine("}");
    }

    private static void AppendSignature(StringBuilder builder, int rhythm)
    {
        builder.Append("||s");
        builder.Append(rhythm.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("/4");
    }

    private static string FormatBpm(double bpm)
    {
        return bpm.ToString("G9", CultureInfo.InvariantCulture);
    }

    private static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.#########", CultureInfo.InvariantCulture);
    }

    private sealed record IndexedTimingPoint(TimingPoint Point, int Index);

    private sealed record TimingEvent(double Time, double Bpm, int Rhythm);

    private sealed record ConvertedHitObject(double Time, double EndTime, string Text, int Index);

    private enum BeatMode
    {
        Quarter,
        Seconds
    }
}
