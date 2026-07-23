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
    ("MA2 natural each does not export discarded Force Yellow", Ma2NaturalEachDiscard)
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
        Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} validation cases passed.");
return failures == 0 ? 0 : 1;

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

    var yellowLines = lines.Where(line => line.EndsWith("\t!y", StringComparison.Ordinal)).ToArray();
    var yellowHeadLines = yellowLines.Count(line => line.StartsWith("NMSTR\t", StringComparison.Ordinal));
    Expect(yellowLines.Length == 4 && yellowHeadLines == 1,
        "Force Yellow was not emitted on exactly the requested Slide segments");
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
