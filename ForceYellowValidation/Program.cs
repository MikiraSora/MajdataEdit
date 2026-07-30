using System.Globalization;
using System.IO;
using System.Text;
using MajdataEdit;
using MajdataEdit.Ma2Export;
using MajdataEdit.SyntaxModule;
using MajSimai;

var tests = new (string Name, Func<Task> Body)[]
{
    ("SyntaxCheck accepts Force Yellow", SyntaxCheckAcceptsForceYellow),
    ("SyntaxCheck rejects invalid Force Yellow", SyntaxCheckRejectsInvalidForceYellow),
    ("timeline segment analysis preserves Force Yellow scope", TimelineSegmentAnalysis),
    ("MA2 exports Force Yellow note tails", Ma2ExportsForceYellow),
    ("MA2 exports per-segment Force Yellow tails", Ma2ExportsForceYellowSegments),
    ("MA2 exports branch-local moving-star Force Yellow tails", Ma2ExportsMovingStarForceYellow),
    ("MA2 natural each does not export discarded Force Yellow", Ma2NaturalEachDiscard),
    ("MA2 exports self-returning v slides as SHL", Ma2ExportsSelfReturningVSlides),
    ("MA2 adaptive export keeps safe charts at 384", Ma2AdaptiveKeepsSafeChartAt384),
    ("MA2 adaptive export selects the first usable resolution", Ma2AdaptiveSelectsFirstUsableResolution),
    ("MA2 adaptive export repairs sub-grid durations at the maximum", Ma2AdaptiveRepairsSubGridDuration),
    ("MA2 adaptive export repairs sub-grid Hold durations", Ma2AdaptiveRepairsSubGridHold),
    ("MA2 runtime float collapse allocates multiple grids", Ma2RuntimeFloatCollapseNeedsMultipleGrids),
    ("MA2 absolute lengths cross BPM segments", Ma2LengthsCrossBpmSegments),
    ("MA2 meter denominator constrains resolution", Ma2MeterConstrainsResolution),
    ("MA2 disabled repair reports validation failure", Ma2DisabledRepairReportsFailure),
    ("MA2 rejects Int32 timing overflow", Ma2RejectsInt32Overflow),
    ("MA2 rejects resolution options outside supported bounds", Ma2RejectsInvalidResolutionOptions),
    ("MA2 blocks an unsafe explicit-zero self-returning Slide", Ma2BlocksZeroSelfReturningSlide),
    ("MA2 adaptive export reports exact root-contact repair", Ma2AdaptiveRepairsExactRootContact),
    ("MA2 adaptive export shifts a Head group when shortening is impossible", Ma2AdaptiveShiftsHeadGroup),
    ("MA2 multi-segment Slide totals count branches", Ma2MultiSegmentSlideTotals)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} validation cases passed.");
if (failures != 0)
{
    return 1;
}

if (args.Length == 4 && args[0] == "--convert-maidata")
{
    var maidataPath = args[1];
    var difficulty = int.Parse(args[2], CultureInfo.InvariantCulture);
    var outputPath = args[3];
    var chartContent = ReadMaidataChart(maidataPath, difficulty);
    var conversion = new SimaiChartConverter().ConvertChartToMa2(chartContent);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    File.WriteAllText(outputPath, conversion.Content, new UTF8Encoding(false));
    Console.WriteLine(
        $"CONVERSION resolution={conversion.Report.FinalResolution} " +
        $"attempts={conversion.Report.CandidateAttempts} " +
        $"repair={conversion.Report.UsedMinimumGridRepair} " +
        $"adjustments={conversion.Report.Adjustments.Count} " +
        $"objects={conversion.Report.AdjustedObjectCount} " +
        $"maxGrid={conversion.Report.MaximumGridAdjustment} " +
        $"maxMsec={conversion.Report.MaximumMillisecondAdjustment:F6} " +
        $"output={Path.GetFullPath(outputPath)}");
}
else if (args.Length != 0)
{
    throw new ArgumentException(
        "Usage: --convert-maidata <maidata.txt> <difficulty> <output.ma2>");
}

return 0;

static async Task SyntaxCheckAcceptsForceYellow()
{
    var valid = new[]
    {
        "1y", "1xy", "1yh[4:1]", "B1fy", "Cyh[4:1]", "1$y", "1y@600",
        "1y!-3[8:1]", "1!y-3[8:1]", "1!-3y[8:1]", "1!-3[8:1]y",
        "1-3y[8:1]-5[8:1]", "1y-3[8:1]*-5b[8:1]"
    };

    foreach (var note in valid)
    {
        await SyntaxChecker.ScanAsync($"(120){{4}}{note},E");
        Expect(SyntaxChecker.GetErrorCount() == 0, $"SyntaxCheck rejected {note}");
    }
}

static async Task SyntaxCheckRejectsInvalidForceYellow()
{
    var invalid = new[]
    {
        "1yb", "1ym", "1y-3b[8:1]", "1b-3y[8:1]", "1-3y[8:1]-5b[8:1]",
        "1yy", "1-3yy[8:1]", "1-3y[8:1]y", "Y1", "y1", "1-y3[8:1]", "1h[4:1]y"
    };

    foreach (var note in invalid)
    {
        await SyntaxChecker.ScanAsync($"(120){{4}}{note},E");
        Expect(SyntaxChecker.GetErrorCount() != 0, $"SyntaxCheck accepted {note}");
    }
}

static Task TimelineSegmentAnalysis()
{
    var chart = SimaiParser.ParseChart("(120){4}1-3y[8:1]-5[8:1]y,".AsSpan(), 0, out _);
    var timingPoint = chart.NoteTimings.ToArray().Single(point => point.Notes.Length != 0);
    var note = timingPoint.Notes.Single();
    var segments = SlideSegmentTimingAnalysis.Analyze(timingPoint, note);
    var flags = ForceYellowSlideSegmentHelper.ResolveFlags(note, segments.Length);

    Expect(segments.Length == 2, $"expected two timeline segments, found {segments.Length}");
    Expect(flags.SequenceEqual(new[] { true, true }), "timeline Force Yellow flags did not match both segments");
    Expect(segments[0].Duration > 0 && segments[1].Duration > 0, "timeline segment duration was not positive");
    Expect(Math.Abs(segments[0].StartTime + segments[0].Duration - segments[1].StartTime) <= 1e-9,
        "timeline segments were not contiguous");
    return Task.CompletedTask;
}

static Task Ma2ExportsForceYellow()
{
    var output = new SimaiChartConverter().ConvertChartToMa2Content("(120){4}1y,2y@600,");
    var lines = GetLines(output);
    Expect(lines.Any(line => line.StartsWith("NMTAP\t0\t0\t0\t!y", StringComparison.Ordinal)),
        "Force Yellow Tap did not export !y");
    Expect(lines.Any(line => line.EndsWith("\t!y#F600", StringComparison.Ordinal)),
        "Force Yellow FixedSoflan Tap did not export canonical !y#F600");
    return Task.CompletedTask;
}

static Task Ma2ExportsForceYellowSegments()
{
    var output = new SimaiChartConverter().ConvertChartToMa2Content(
        "(120){4}1y-3[8:1],2-4y[8:1],3-5[8:1]-7y[8:1],4!-6y[8:1],");
    var lines = GetLines(output);

    Expect(lines.Any(line => line.StartsWith("NMSTR\t0\t0\t0\t!y", StringComparison.Ordinal)),
        "Force Yellow Slide head did not export !y");
    Expect(lines.Any(line => line.StartsWith("NMSI_\t0\t0\t0\t", StringComparison.Ordinal) &&
                             line.EndsWith("\t!yh", StringComparison.Ordinal)),
        "Force Yellow Slide moving star did not export !yh");

    var yellowLines = lines.Where(line => line.EndsWith("\t!y", StringComparison.Ordinal)).ToArray();
    var yellowHeadLines = yellowLines.Count(line => line.StartsWith("NMSTR\t", StringComparison.Ordinal));
    Expect(yellowLines.Length == 4 && yellowHeadLines == 1,
        "Force Yellow was not emitted on exactly the requested Slide segments");
    return Task.CompletedTask;
}

static Task Ma2ExportsMovingStarForceYellow()
{
    var output = new SimaiChartConverter().ConvertChartToMa2Content(
        "(120){4}1y-3[8:1],2y!-4[8:1],3y!-5y[8:1]," +
        "4y-6[8:1]-8y[8:1],5y-7[8:1]*-1b[8:1],6yw2[8:1],");
    var lines = GetLines(output);

    var movingStarLines = lines.Where(line =>
        line.Contains("\t!yh", StringComparison.Ordinal)).ToArray();
    Expect(movingStarLines.Length == 6,
        $"expected six branch-local !yh records, found {movingStarLines.Length}");
    Expect(movingStarLines.All(line => !line.StartsWith("CN", StringComparison.Ordinal)),
        "connected Slide continuation incorrectly exported !yh");
    Expect(movingStarLines.Any(line => line.EndsWith("\t!yh!y", StringComparison.Ordinal)),
        "combined moving-star/path Yellow did not use canonical !yh!y order");

    var yellowHeadLines = lines.Where(line =>
        line.StartsWith("NMSTR\t", StringComparison.Ordinal) &&
        line.EndsWith("\t!y", StringComparison.Ordinal)).ToArray();
    Expect(yellowHeadLines.Length == 4,
        $"expected four visible Yellow star heads, found {yellowHeadLines.Length}");

    var breakBranch = lines.Single(line => line.StartsWith("BRSI_\t", StringComparison.Ordinal));
    Expect(!breakBranch.Contains("!y", StringComparison.Ordinal),
        "independent same-head Break branch inherited Force Yellow");

    return Task.CompletedTask;
}

static Task Ma2NaturalEachDiscard()
{
    var output = new SimaiChartConverter().ConvertChartToMa2Content("(120){4}1y/2,");
    var noteLines = GetLines(output).Where(line => line.StartsWith("NMTAP", StringComparison.Ordinal)).ToArray();
    Expect(noteLines.Length == 2, "natural each MA2 Tap count changed");
    Expect(noteLines.All(line => !line.Contains("!y", StringComparison.Ordinal)),
        "discarded natural-each Force Yellow leaked into MA2");
    Expect(output.Contains("TTM_EACHPAIRS\t1", StringComparison.Ordinal), "natural each pair count changed");
    return Task.CompletedTask;
}

static Task Ma2ExportsSelfReturningVSlides()
{
    var chart = string.Join(",", Enumerable.Range(1, 8).Select(position => $"{position}v{position}[8:1]")) +
                ",1v2[8:1]";
    var output = new SimaiChartConverter().ConvertChartToMa2Content($"(120){{4}}{chart},");
    var lines = GetLines(output);
    var slideLines = lines
        .Where(line => line.StartsWith("SHL\t", StringComparison.Ordinal))
        .ToArray();

    Expect(slideLines.Length == 8, $"expected 8 self-returning SHL bodies, found {slideLines.Length}");
    for (var i = 0; i < slideLines.Length; i++)
    {
        var fields = slideLines[i].Split('\t');
        Expect(fields.Length >= 7, $"self-returning SHL record was incomplete: {slideLines[i]}");
        Expect(fields[3] == i.ToString() && fields[6] == i.ToString(),
            $"self-returning SHL positions were {fields[3]} -> {fields[6]}, expected {i} -> {i}");
    }

    var ordinaryV = lines.Single(line => line.StartsWith("NMSV_\t", StringComparison.Ordinal));
    var ordinaryVFields = ordinaryV.Split('\t');
    Expect(ordinaryVFields[3] == "0" && ordinaryVFields[6] == "1",
        $"ordinary v MA2 positions were {ordinaryVFields[3]} -> {ordinaryVFields[6]}, expected 0 -> 1");

    return Task.CompletedTask;
}

static Task Ma2AdaptiveKeepsSafeChartAt384()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2("(120){4}1,2h[4:1],3-5[4:1],");
    Expect(conversion.Report.FinalResolution == 384,
        $"safe chart selected resolution {conversion.Report.FinalResolution}");
    Expect(conversion.Report.CandidateAttempts == 1, "safe chart did not stop after its first candidate");
    Expect(!conversion.Report.UsedMinimumGridRepair, "safe chart unexpectedly used minimum-grid repair");
    Expect(conversion.Report.Adjustments.Count == 0, "safe chart reported timing adjustments");
    Expect(conversion.Content.Contains("RESOLUTION\t384", StringComparison.Ordinal),
        "safe chart content did not retain resolution 384");
    return Task.CompletedTask;
}

static Task Ma2AdaptiveSelectsFirstUsableResolution()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2(
        "(120){4}1-3[0.001##0.001],",
        options: new Ma2AdaptiveResolutionOptions
        {
            MaximumResolution = 3840,
            EnableMinimumGridRepair = false
        });

    Expect(conversion.Report.FinalResolution == 1536,
        $"expected first usable resolution 1536, found {conversion.Report.FinalResolution}");
    Expect(conversion.Report.CandidateAttempts == 4,
        $"expected four candidates, found {conversion.Report.CandidateAttempts}");
    Expect(!conversion.Report.UsedMinimumGridRepair, "representable duration unexpectedly required repair");
    return Task.CompletedTask;
}

static Task Ma2AdaptiveRepairsSubGridDuration()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2(
        "(163){4}1!v3[0.000001##0.000001],");
    var slide = GetLines(conversion.Content).Single(line => line.StartsWith("NMSV_\t", StringComparison.Ordinal));
    var fields = slide.Split('\t');

    Expect(conversion.Report.FinalResolution == 192000,
        $"sub-grid duration did not reach maximum resolution: {conversion.Report.FinalResolution}");
    Expect(conversion.Report.UsedMinimumGridRepair, "sub-grid duration did not use minimum-grid repair");
    Expect(conversion.Report.Adjustments.Any(x => x.Kind == "Positive Slide wait"),
        "sub-grid positive wait repair was not reported");
    Expect(conversion.Report.Adjustments.Any(x => x.Kind == "Positive Slide duration"),
        "sub-grid positive duration repair was not reported");
    Expect(long.Parse(fields[4], CultureInfo.InvariantCulture) > 0,
        "repaired positive wait remained zero Grid");
    Expect(long.Parse(fields[5], CultureInfo.InvariantCulture) > 0,
        "repaired positive duration remained zero Grid");
    return Task.CompletedTask;
}

static Task Ma2AdaptiveRepairsSubGridHold()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2("(163){4}1h[#0.000001],");
    var hold = GetLines(conversion.Content).Single(line => line.StartsWith("NMHLD\t", StringComparison.Ordinal));
    var fields = hold.Split('\t');

    Expect(conversion.Report.FinalResolution == 192000,
        $"sub-grid Hold did not reach maximum resolution: {conversion.Report.FinalResolution}");
    Expect(conversion.Report.Adjustments.Any(x => x.Kind == "Positive Hold duration"),
        "sub-grid Hold repair was not reported");
    Expect(long.Parse(fields[4], CultureInfo.InvariantCulture) > 0,
        "repaired positive Hold duration remained zero Grid");
    return Task.CompletedTask;
}

static Task Ma2RuntimeFloatCollapseNeedsMultipleGrids()
{
    var lateTiming = string.Concat(Enumerable.Repeat(",", 128));
    var conversion = new SimaiChartConverter().ConvertChartToMa2(
        $"(120){{1}}{lateTiming}1!v3[0.000001##0.000001],");
    var slide = GetLines(conversion.Content).Single(line => line.StartsWith("NMSV_\t", StringComparison.Ordinal));
    var fields = slide.Split('\t');
    var waitGrid = long.Parse(fields[4], CultureInfo.InvariantCulture);
    var durationGrid = long.Parse(fields[5], CultureInfo.InvariantCulture);

    Expect(waitGrid >= 2, $"late positive wait used {waitGrid} Grid and still risks float collapse");
    Expect(durationGrid >= 2, $"late positive duration used {durationGrid} Grid and still risks float collapse");
    Expect(conversion.Report.MaximumGridAdjustment >= 2,
        "late float-collapse repair did not report its multi-Grid adjustment");
    return Task.CompletedTask;
}

static Task Ma2LengthsCrossBpmSegments()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2("(120){4}1h[#1],(240)2,");
    var lines = GetLines(conversion.Content);
    var hold = lines.Single(line => line.StartsWith("NMHLD\t", StringComparison.Ordinal));
    var fields = hold.Split('\t');

    Expect(conversion.Report.FinalResolution == 384, "cross-BPM Hold unexpectedly raised resolution");
    Expect(fields[4] == "288", $"cross-BPM Hold length was {fields[4]}, expected 288 Grid");
    Expect(lines.Contains("BPM\t0\t96\t240"), "BPM boundary was not emitted at Grid 96");
    return Task.CompletedTask;
}

static Task Ma2MeterConstrainsResolution()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2("(120)\n||s4/5\n{4}1,");
    var lines = GetLines(conversion.Content);
    Expect(conversion.Report.FinalResolution == 1920,
        $"4/5 meter selected {conversion.Report.FinalResolution}, expected the first legal multiple 1920");
    Expect(lines.Contains("MET_DEF\t4\t5"), "4/5 meter was not emitted in MET_DEF");
    Expect(lines.Contains("MET\t0\t0\t4\t5"), "4/5 meter was not emitted at chart start");
    return Task.CompletedTask;
}

static Task Ma2DisabledRepairReportsFailure()
{
    ExpectThrows<InvalidOperationException>(
        () => new SimaiChartConverter().ConvertChartToMa2(
            "(163){4}1!v3[0.000001##0.000001],",
            options: new Ma2AdaptiveResolutionOptions
            {
                MaximumResolution = 384,
                EnableMinimumGridRepair = false
            }),
        "修复");
    return Task.CompletedTask;
}

static Task Ma2RejectsInt32Overflow()
{
    ExpectThrows<InvalidOperationException>(
        () => new SimaiChartConverter().ConvertChartToMa2("(120){4}1h[#20000000],"),
        "Int32");
    return Task.CompletedTask;
}

static Task Ma2RejectsInvalidResolutionOptions()
{
    ExpectThrows<ArgumentOutOfRangeException>(
        () => new SimaiChartConverter().ConvertChartToMa2(
            "(120){4}1,",
            options: new Ma2AdaptiveResolutionOptions { MinimumResolution = 383 }),
        "384");
    ExpectThrows<ArgumentOutOfRangeException>(
        () => new SimaiChartConverter().ConvertChartToMa2(
            "(120){4}1,",
            options: new Ma2AdaptiveResolutionOptions { MaximumResolution = 192001 }),
        "192000");
    return Task.CompletedTask;
}

static Task Ma2BlocksZeroSelfReturningSlide()
{
    ExpectThrows<InvalidOperationException>(
        () => new SimaiChartConverter().ConvertChartToMa2(
            "(120){4}1!v1[0##0],",
            options: new Ma2AdaptiveResolutionOptions { MaximumResolution = 384 }),
        "Cannot safely separate");
    return Task.CompletedTask;
}

static Task Ma2AdaptiveRepairsExactRootContact()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2(
        "(120){4}1!-3[0##0.5],3!-5[0##0.5],",
        options: new Ma2AdaptiveResolutionOptions { MaximumResolution = 768 });

    Expect(conversion.Report.FinalResolution == 768,
        $"exact-contact test selected {conversion.Report.FinalResolution} instead of configured maximum 768");
    Expect(conversion.Report.UsedMinimumGridRepair, "exact root contact did not enter repair mode");
    Expect(conversion.Report.Adjustments.Any(x => x.Kind == "Slide root-contact separation"),
        "exact root-contact separation was not reported");
    return Task.CompletedTask;
}

static Task Ma2AdaptiveShiftsHeadGroup()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2(
        "(120){384}1!-3[0##0.005208333333333333],4/3!-5[0##0.5],",
        options: new Ma2AdaptiveResolutionOptions { MaximumResolution = 384 });
    var roots = GetLines(conversion.Content)
        .Where(line => line.StartsWith("NMSI_\t", StringComparison.Ordinal))
        .Select(line => line.Split('\t'))
        .ToArray();

    Expect(conversion.Report.UsedMinimumGridRepair, "minimum-length exact contact did not enter repair mode");
    Expect(conversion.Report.Adjustments.Any(x => x.Kind == "Slide Head/EACH group shift"),
        "minimum-length exact contact did not report a Head/EACH group shift");
    Expect(roots.Length == 2, $"expected two root Slides, found {roots.Length}");
    Expect(roots[0][1] == "0" && roots[0][2] == "0", "owner root moved unexpectedly");
    Expect(roots[1][1] == "0" && roots[1][2] == "2",
        $"victim root was not shifted from Grid 1 to Grid 2: {roots[1][1]}:{roots[1][2]}");
    var shiftedTap = GetLines(conversion.Content)
        .Single(line => line.StartsWith("NMTAP\t", StringComparison.Ordinal) && line.Split('\t')[3] == "3")
        .Split('\t');
    Expect(shiftedTap[1] == "0" && shiftedTap[2] == "2",
        $"same-time EACH Tap was not shifted with the root group: {shiftedTap[1]}:{shiftedTap[2]}");
    return Task.CompletedTask;
}

static Task Ma2MultiSegmentSlideTotals()
{
    var conversion = new SimaiChartConverter().ConvertChartToMa2("(120){4}1-3-5[4:1],");
    var lines = GetLines(conversion.Content);
    Expect(lines.Count(line => line.StartsWith("NMSI_\t", StringComparison.Ordinal)) == 1,
        "multi-segment Slide did not contain one root");
    Expect(lines.Count(line => line.StartsWith("CNSI_\t", StringComparison.Ordinal)) == 1,
        "multi-segment Slide did not contain one connected segment");
    Expect(lines.Contains("T_REC_SLD\t1"), "multi-segment Slide branch was omitted from T_REC_SLD");
    Expect(lines.Contains("T_NUM_SLD\t1"), "multi-segment Slide branch was omitted from T_NUM_SLD");
    return Task.CompletedTask;
}

static string ReadMaidataChart(string path, int difficulty)
{
    var lines = File.ReadAllLines(path, Encoding.UTF8);
    var prefix = $"&inote_{difficulty}=";
    var start = Array.FindIndex(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
    if (start < 0)
    {
        throw new InvalidOperationException($"maidata does not contain {prefix}");
    }

    var result = new List<string> { lines[start][prefix.Length..] };
    for (var i = start + 1; i < lines.Length; i++)
    {
        if (lines[i].StartsWith("&", StringComparison.Ordinal) && lines[i].Contains('='))
        {
            break;
        }
        result.Add(lines[i]);
    }

    return string.Join("\n", result);
}

static string[] GetLines(string value)
{
    return value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
}

static void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectThrows<TException>(Action action, string expectedMessagePart)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        Expect(exception.Message.Contains(expectedMessagePart, StringComparison.OrdinalIgnoreCase),
            $"{typeof(TException).Name} message did not contain '{expectedMessagePart}': {exception.Message}");
        return;
    }

    throw new InvalidOperationException($"expected {typeof(TException).Name} was not thrown");
}
