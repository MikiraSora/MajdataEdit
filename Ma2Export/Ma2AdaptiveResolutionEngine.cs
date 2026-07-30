using System.Globalization;

namespace MajdataEdit.Ma2Export;

internal sealed class Ma2CandidateBuild
{
    public Ma2CandidateBuild(
        string content,
        int resolution,
        IReadOnlyList<Ma2SlideSource> slideSources,
        IReadOnlyList<Ma2HoldSource> holdSources)
    {
        Content = content;
        Resolution = resolution;
        SlideSources = slideSources;
        HoldSources = holdSources;
    }

    public string Content { get; }
    public int Resolution { get; }
    public IReadOnlyList<Ma2SlideSource> SlideSources { get; }
    public IReadOnlyList<Ma2HoldSource> HoldSources { get; }
}

internal sealed class Ma2SlideSource
{
    public int BranchId { get; init; }
    public int SegmentIndex { get; init; }
    public int SourceLine { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public bool HasHead { get; init; }
    public double OriginalWaitSeconds { get; init; }
    public double OriginalDurationSeconds { get; init; }
}

internal sealed class Ma2HoldSource
{
    public int SourceIndex { get; init; }
    public int SourceLine { get; init; }
    public string SourceText { get; init; } = string.Empty;
    public double OriginalDurationSeconds { get; init; }
}

internal sealed class Ma2CandidateValidation
{
    public int RootCaptureCount { get; init; }
    public int SelfCaptureCount { get; init; }
    public int CrossBranchCaptureCount { get; init; }
    public int OrphanConnectedSlideCount { get; init; }
    public int MissingHeadCount { get; init; }
    public int PositiveGridCollapseCount { get; init; }
    public int PositiveRuntimeCollapseCount { get; init; }
    public int IntegerRangeErrorCount { get; init; }

    public bool IsValid =>
        RootCaptureCount == 0 &&
        SelfCaptureCount == 0 &&
        CrossBranchCaptureCount == 0 &&
        OrphanConnectedSlideCount == 0 &&
        MissingHeadCount == 0 &&
        PositiveGridCollapseCount == 0 &&
        PositiveRuntimeCollapseCount == 0 &&
        IntegerRangeErrorCount == 0;

    public override string ToString()
    {
        return string.Join(
            ", ",
            $"root captures={RootCaptureCount}",
            $"self captures={SelfCaptureCount}",
            $"cross-branch captures={CrossBranchCaptureCount}",
            $"orphan CN={OrphanConnectedSlideCount}",
            $"missing heads={MissingHeadCount}",
            $"positive grid collapses={PositiveGridCollapseCount}",
            $"positive runtime collapses={PositiveRuntimeCollapseCount}",
            $"integer range errors={IntegerRangeErrorCount}");
    }
}

internal sealed class Ma2AdaptiveResolutionEngine
{
    private const double PositiveTimeEpsilon = 1e-12;
    private const int RepairAttemptLimit = 4096;

    private static readonly HashSet<string> SlideSuffixes = new(StringComparer.Ordinal)
    {
        "SI_", "SCL", "SCR", "SUL", "SUR", "SSL", "SSR",
        "SV_", "SXL", "SXR", "SLL", "SLR", "SF_"
    };

    public Ma2CandidateValidation Validate(Ma2CandidateBuild candidate)
    {
        var parsed = ParsedMa2.Parse(candidate);
        return Analyze(parsed).Validation;
    }

    public (string Content, IReadOnlyList<Ma2TimingAdjustment> Adjustments) Repair(Ma2CandidateBuild candidate)
    {
        var parsed = ParsedMa2.Parse(candidate);
        var adjustments = new List<Ma2TimingAdjustment>();

        foreach (var hold in parsed.Holds)
        {
            if (hold.Source.OriginalDurationSeconds <= PositiveTimeEpsilon)
            {
                continue;
            }

            var repairedLength = EnsurePositiveRuntimeLength(parsed.Timeline, hold.Grid, hold.LengthGrid);
            if (repairedLength != hold.LengthGrid)
            {
                var oldEnd = checked(hold.Grid + hold.LengthGrid);
                var newEnd = checked(hold.Grid + repairedLength);
                adjustments.Add(new Ma2TimingAdjustment(
                    "Positive Hold duration",
                    hold.Source.SourceLine,
                    hold.Source.SourceText,
                    -hold.Source.SourceIndex - 1,
                    0,
                    repairedLength - hold.LengthGrid,
                    parsed.Timeline.CalculateMsec(newEnd) - parsed.Timeline.CalculateMsec(oldEnd)));
                hold.LengthGrid = repairedLength;
            }
        }

        foreach (var branch in parsed.Slides.GroupBy(x => x.Source.BranchId).OrderBy(x => x.Key))
        {
            var segments = branch.OrderBy(x => x.Source.SegmentIndex).ToArray();
            long nextGrid = segments[0].Grid;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (i > 0 && segment.Grid != nextGrid)
                {
                    var oldGrid = segment.Grid;
                    segment.Grid = nextGrid;
                    AddAdjustment(adjustments, "Slide branch propagation", segment, segment.Grid - oldGrid,
                        parsed.Timeline.CalculateMsec(segment.Grid) - parsed.Timeline.CalculateMsec(oldGrid));
                }

                if (i == 0 && segment.Source.OriginalWaitSeconds > PositiveTimeEpsilon)
                {
                    var repairedWait = EnsurePositiveRuntimeLength(parsed.Timeline, segment.Grid, segment.WaitGrid);
                    if (repairedWait != segment.WaitGrid)
                    {
                        var oldEnd = checked(segment.Grid + segment.WaitGrid);
                        var newEnd = checked(segment.Grid + repairedWait);
                        AddAdjustment(adjustments, "Positive Slide wait", segment, repairedWait - segment.WaitGrid,
                            parsed.Timeline.CalculateMsec(newEnd) - parsed.Timeline.CalculateMsec(oldEnd));
                        segment.WaitGrid = repairedWait;
                    }
                }

                if (segment.Source.OriginalDurationSeconds > PositiveTimeEpsilon)
                {
                    var shootGrid = checked(segment.Grid + segment.WaitGrid);
                    var repairedDuration = EnsurePositiveRuntimeLength(parsed.Timeline, shootGrid, segment.DurationGrid);
                    if (repairedDuration != segment.DurationGrid)
                    {
                        var oldEnd = checked(shootGrid + segment.DurationGrid);
                        var newEnd = checked(shootGrid + repairedDuration);
                        AddAdjustment(adjustments, "Positive Slide duration", segment,
                            repairedDuration - segment.DurationGrid,
                            parsed.Timeline.CalculateMsec(newEnd) - parsed.Timeline.CalculateMsec(oldEnd));
                        segment.DurationGrid = repairedDuration;
                    }
                }

                nextGrid = segment.EndGrid;
            }
        }

        for (var attempt = 0; attempt < RepairAttemptLimit; attempt++)
        {
            var analysis = Analyze(parsed);
            var capture = analysis.WrongCaptures.FirstOrDefault();
            if (capture is null)
            {
                var validation = analysis.Validation;
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(
                        $"MA2 minimum-grid repair did not produce a valid chart: {validation}");
                }

                parsed.UpdateLines();
                return (parsed.Render(), adjustments);
            }

            var owner = capture.Owner;
            var hasExpectedNext = parsed.Slides.Any(x =>
                x.Source.BranchId == owner.Source.BranchId &&
                x.Source.SegmentIndex == owner.Source.SegmentIndex + 1);
            var minimumDuration = EnsurePositiveRuntimeLength(
                parsed.Timeline,
                checked(owner.Grid + owner.WaitGrid),
                1);

            if (!hasExpectedNext && owner.DurationGrid > minimumDuration)
            {
                var oldEnd = owner.EndGrid;
                owner.DurationGrid--;
                AddAdjustment(adjustments, "Slide root-contact separation", owner, -1,
                    parsed.Timeline.CalculateMsec(owner.EndGrid) - parsed.Timeline.CalculateMsec(oldEnd));
                continue;
            }

            if (capture.Candidate.IsRoot && !ReferenceEquals(owner, capture.Candidate))
            {
                var victim = capture.Candidate;
                var oldGrid = victim.Grid;
                parsed.ShiftHeadAndEachGroup(victim, 1);
                AddAdjustment(adjustments, "Slide Head/EACH group shift", victim, 1,
                    parsed.Timeline.CalculateMsec(victim.Grid) - parsed.Timeline.CalculateMsec(oldGrid));
                continue;
            }

            throw new InvalidOperationException(
                $"Cannot safely separate Slide branch {owner.Source.BranchId} segment " +
                $"{owner.Source.SegmentIndex + 1} (Simai line {owner.Source.SourceLine}) from " +
                $"branch {capture.Candidate.Source.BranchId} without breaking a connected segment.");
        }

        throw new InvalidOperationException($"MA2 minimum-grid repair exceeded {RepairAttemptLimit} attempts.");
    }

    private static long EnsurePositiveRuntimeLength(Ma2BpmTimeline timeline, long startGrid, long currentLength)
    {
        var length = Math.Max(1, currentLength);
        var startMsec = timeline.CalculateMsec(startGrid);
        for (var i = 0; i < RepairAttemptLimit; i++, length++)
        {
            if (timeline.CalculateMsec(checked(startGrid + length)) > startMsec)
            {
                return length;
            }
        }

        throw new InvalidOperationException(
            $"No runtime-distinct Grid interval was found after grid {startGrid}.");
    }

    private static void AddAdjustment(
        ICollection<Ma2TimingAdjustment> adjustments,
        string kind,
        ParsedSlide segment,
        long gridDelta,
        double millisecondDelta)
    {
        adjustments.Add(new Ma2TimingAdjustment(
            kind,
            segment.Source.SourceLine,
            segment.Source.SourceText,
            segment.Source.BranchId,
            segment.Source.SegmentIndex,
            gridDelta,
            millisecondDelta));
    }

    private static SlideAnalysis Analyze(ParsedMa2 parsed)
    {
        foreach (var slide in parsed.Slides)
        {
            slide.HasParent = false;
            slide.AssignedBySlide = false;
        }

        foreach (var star in parsed.Stars)
        {
            foreach (var slide in parsed.Slides)
            {
                if (slide.LineIndex > star.LineIndex && slide.IsRoot &&
                    slide.Grid == star.Grid && slide.StartPosition == star.Position)
                {
                    slide.HasParent = true;
                }
            }
        }

        var captures = new List<SlideCapture>();
        foreach (var owner in parsed.Slides)
        {
            foreach (var candidate in parsed.Slides)
            {
                if (candidate.LineIndex < owner.LineIndex ||
                    candidate.Grid != owner.EndGrid ||
                    candidate.StartPosition != owner.EndPosition ||
                    candidate.HasParent)
                {
                    continue;
                }

                candidate.HasParent = owner.HasParent;
                candidate.AssignedBySlide = true;
                captures.Add(new SlideCapture(owner, candidate));
                break;
            }
        }

        var wrongCaptures = captures.Where(x => !x.IsExpectedConnection).ToArray();
        var rootCaptures = wrongCaptures.Count(x => x.Candidate.IsRoot);
        var selfCaptures = wrongCaptures.Count(x => ReferenceEquals(x.Owner, x.Candidate));
        var crossBranchCaptures = wrongCaptures.Count(x =>
            x.Owner.Source.BranchId != x.Candidate.Source.BranchId);
        var orphanConnected = parsed.Slides.Count(x => !x.IsRoot && !x.AssignedBySlide);
        var missingHeads = parsed.Slides.Count(x =>
            x.IsRoot && x.Source.HasHead && !x.HasParent && !x.AssignedBySlide);

        var gridCollapses = 0;
        var runtimeCollapses = 0;
        var integerErrors = 0;
        integerErrors += parsed.Stars.Count(x => !IsNonNegativeInt32(x.Grid));
        integerErrors += parsed.OtherTimedNotes.Count(x => !IsNonNegativeInt32(x.Grid));
        foreach (var hold in parsed.Holds)
        {
            if (!IsNonNegativeInt32(hold.Grid) ||
                !IsNonNegativeInt32(hold.LengthGrid) ||
                !IsNonNegativeInt32(checked(hold.Grid + hold.LengthGrid)))
            {
                integerErrors++;
            }

            if (hold.Source.OriginalDurationSeconds > PositiveTimeEpsilon)
            {
                if (hold.LengthGrid <= 0)
                {
                    gridCollapses++;
                }
                else if (parsed.Timeline.CalculateMsec(checked(hold.Grid + hold.LengthGrid)) <=
                         parsed.Timeline.CalculateMsec(hold.Grid))
                {
                    runtimeCollapses++;
                }
            }
        }

        foreach (var slide in parsed.Slides)
        {
            if (!IsNonNegativeInt32(slide.Grid) ||
                !IsNonNegativeInt32(slide.WaitGrid) ||
                !IsNonNegativeInt32(slide.DurationGrid) ||
                !IsNonNegativeInt32(slide.EndGrid))
            {
                integerErrors++;
            }

            if (slide.Source.OriginalWaitSeconds > PositiveTimeEpsilon)
            {
                if (slide.WaitGrid <= 0)
                {
                    gridCollapses++;
                }
                else if (parsed.Timeline.CalculateMsec(checked(slide.Grid + slide.WaitGrid)) <=
                         parsed.Timeline.CalculateMsec(slide.Grid))
                {
                    runtimeCollapses++;
                }
            }

            if (slide.Source.OriginalDurationSeconds > PositiveTimeEpsilon)
            {
                var shootGrid = checked(slide.Grid + slide.WaitGrid);
                if (slide.DurationGrid <= 0)
                {
                    gridCollapses++;
                }
                else if (parsed.Timeline.CalculateMsec(checked(shootGrid + slide.DurationGrid)) <=
                         parsed.Timeline.CalculateMsec(shootGrid))
                {
                    runtimeCollapses++;
                }
            }
        }

        var validation = new Ma2CandidateValidation
        {
            RootCaptureCount = rootCaptures,
            SelfCaptureCount = selfCaptures,
            CrossBranchCaptureCount = crossBranchCaptures,
            OrphanConnectedSlideCount = orphanConnected,
            MissingHeadCount = missingHeads,
            PositiveGridCollapseCount = gridCollapses,
            PositiveRuntimeCollapseCount = runtimeCollapses,
            IntegerRangeErrorCount = integerErrors
        };
        return new SlideAnalysis(validation, wrongCaptures);
    }

    private static bool IsNonNegativeInt32(long value) => value is >= 0 and <= int.MaxValue;

    private sealed class ParsedMa2
    {
        private ParsedMa2(
            string[] lines,
            IReadOnlyList<ParsedSlide> slides,
            IReadOnlyList<ParsedHold> holds,
            IReadOnlyList<ParsedStar> stars,
            IReadOnlyList<ParsedTimedNote> otherTimedNotes,
            Ma2BpmTimeline timeline)
        {
            Lines = lines;
            Slides = slides;
            Holds = holds;
            Stars = stars;
            OtherTimedNotes = otherTimedNotes;
            Timeline = timeline;
        }

        public string[] Lines { get; }
        public IReadOnlyList<ParsedSlide> Slides { get; }
        public IReadOnlyList<ParsedHold> Holds { get; }
        public IReadOnlyList<ParsedStar> Stars { get; }
        public IReadOnlyList<ParsedTimedNote> OtherTimedNotes { get; }
        public Ma2BpmTimeline Timeline { get; }

        public static ParsedMa2 Parse(Ma2CandidateBuild candidate)
        {
            var lines = candidate.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var bpmEvents = new List<(long Grid, float Bpm)>();
            var slides = new List<ParsedSlide>();
            var holds = new List<ParsedHold>();
            var stars = new List<ParsedStar>();
            var otherTimedNotes = new List<ParsedTimedNote>();
            var sourceIndex = 0;
            var holdSourceIndex = 0;

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex]))
                {
                    continue;
                }

                var fields = lines[lineIndex].Split('\t');
                var type = fields[0];
                if (type == "BPM" && fields.Length >= 4)
                {
                    bpmEvents.Add((
                        ToTotalGrid(fields[1], fields[2], candidate.Resolution),
                        float.Parse(fields[3], CultureInfo.InvariantCulture)));
                    continue;
                }

                if (IsStar(type) && fields.Length >= 4)
                {
                    stars.Add(new ParsedStar(lineIndex, fields, candidate.Resolution));
                    continue;
                }

                if (!IsSlide(type) || fields.Length < 7)
                {
                    if (IsHold(type) && fields.Length >= 5)
                    {
                        if (holdSourceIndex >= candidate.HoldSources.Count)
                        {
                            throw new InvalidOperationException("MA2 Hold output exceeded its source metadata.");
                        }

                        holds.Add(new ParsedHold(
                            lineIndex,
                            fields,
                            candidate.HoldSources[holdSourceIndex++],
                            candidate.Resolution));
                    }
                    else if (IsOtherTimedNote(type) && fields.Length >= 4)
                    {
                        otherTimedNotes.Add(new ParsedTimedNote(
                            lineIndex,
                            fields,
                            candidate.Resolution));
                    }
                    continue;
                }

                if (sourceIndex >= candidate.SlideSources.Count)
                {
                    throw new InvalidOperationException("MA2 Slide output exceeded its source metadata.");
                }

                slides.Add(new ParsedSlide(
                    lineIndex,
                    fields,
                    candidate.SlideSources[sourceIndex++],
                    candidate.Resolution));
            }

            if (sourceIndex != candidate.SlideSources.Count)
            {
                throw new InvalidOperationException(
                    $"MA2 Slide/source count mismatch: output={sourceIndex}, source={candidate.SlideSources.Count}.");
            }

            if (holdSourceIndex != candidate.HoldSources.Count)
            {
                throw new InvalidOperationException(
                    $"MA2 Hold/source count mismatch: output={holdSourceIndex}, source={candidate.HoldSources.Count}.");
            }

            return new ParsedMa2(
                lines,
                slides,
                holds,
                stars,
                otherTimedNotes,
                new Ma2BpmTimeline(candidate.Resolution, bpmEvents));
        }

        public void UpdateLines()
        {
            foreach (var slide in Slides)
            {
                slide.UpdateFields();
                Lines[slide.LineIndex] = string.Join("\t", slide.Fields);
            }

            foreach (var hold in Holds)
            {
                hold.UpdateFields();
                Lines[hold.LineIndex] = string.Join("\t", hold.Fields);
            }
            foreach (var star in Stars)
            {
                star.UpdateFields();
                Lines[star.LineIndex] = string.Join("\t", star.Fields);
            }
            foreach (var note in OtherTimedNotes)
            {
                note.UpdateFields();
                Lines[note.LineIndex] = string.Join("\t", note.Fields);
            }
        }

        public string Render()
        {
            return string.Join(Environment.NewLine, Lines);
        }

        private static bool IsStar(string type)
        {
            return type.EndsWith("STR", StringComparison.Ordinal);
        }

        private static bool IsSlide(string type)
        {
            if (type == "SHL")
            {
                return true;
            }

            if (type.Length < 5)
            {
                return false;
            }

            var prefix = type[..2];
            return (prefix is "NM" or "BR" or "CN") && SlideSuffixes.Contains(type[2..]);
        }

        private static bool IsHold(string type)
        {
            return type.EndsWith("HLD", StringComparison.Ordinal) || type == "NMTHO";
        }

        private static bool IsOtherTimedNote(string type)
        {
            if (type.Length < 3)
            {
                return false;
            }

            var prefix = type[..2];
            return prefix is "NM" or "BR" or "EX" or "BX";
        }

        public void ShiftHeadAndEachGroup(ParsedSlide victim, long delta)
        {
            var sourceGrid = victim.Grid;
            var branchIds = Slides
                .Where(x => x.IsRoot && x.Grid == sourceGrid)
                .Select(x => x.Source.BranchId)
                .ToHashSet();
            if (branchIds.Count == 0)
            {
                throw new InvalidOperationException("Cannot locate the root Slide group to shift.");
            }

            foreach (var slide in Slides.Where(x => branchIds.Contains(x.Source.BranchId)))
            {
                slide.Grid = checked(slide.Grid + delta);
            }
            foreach (var star in Stars.Where(x => x.Grid == sourceGrid))
            {
                star.Grid = checked(star.Grid + delta);
            }
            foreach (var hold in Holds.Where(x => x.Grid == sourceGrid))
            {
                hold.Grid = checked(hold.Grid + delta);
            }
            foreach (var note in OtherTimedNotes.Where(x => x.Grid == sourceGrid))
            {
                note.Grid = checked(note.Grid + delta);
            }
        }

        private static long ToTotalGrid(string barText, string gridText, int resolution)
        {
            var bar = long.Parse(barText, CultureInfo.InvariantCulture);
            var grid = long.Parse(gridText, CultureInfo.InvariantCulture);
            return checked(bar * resolution + grid);
        }
    }

    private sealed class ParsedSlide
    {
        private readonly int _resolution;

        public ParsedSlide(int lineIndex, string[] fields, Ma2SlideSource source, int resolution)
        {
            LineIndex = lineIndex;
            Fields = fields;
            Source = source;
            _resolution = resolution;
            Grid = checked(
                long.Parse(fields[1], CultureInfo.InvariantCulture) * resolution +
                long.Parse(fields[2], CultureInfo.InvariantCulture));
            StartPosition = int.Parse(fields[3], CultureInfo.InvariantCulture);
            WaitGrid = long.Parse(fields[4], CultureInfo.InvariantCulture);
            DurationGrid = long.Parse(fields[5], CultureInfo.InvariantCulture);
            EndPosition = int.Parse(fields[6], CultureInfo.InvariantCulture);
            IsRoot = !fields[0].StartsWith("CN", StringComparison.Ordinal);
        }

        public int LineIndex { get; }
        public string[] Fields { get; }
        public Ma2SlideSource Source { get; }
        public int StartPosition { get; }
        public int EndPosition { get; }
        public bool IsRoot { get; }
        public bool HasParent { get; set; }
        public bool AssignedBySlide { get; set; }
        public long Grid { get; set; }
        public long WaitGrid { get; set; }
        public long DurationGrid { get; set; }
        public long EndGrid => checked(Grid + WaitGrid + DurationGrid);

        public void UpdateFields()
        {
            Fields[1] = (Grid / _resolution).ToString(CultureInfo.InvariantCulture);
            Fields[2] = (Grid % _resolution).ToString(CultureInfo.InvariantCulture);
            Fields[4] = WaitGrid.ToString(CultureInfo.InvariantCulture);
            Fields[5] = DurationGrid.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class ParsedStar
    {
        private readonly int _resolution;

        public ParsedStar(int lineIndex, string[] fields, int resolution)
        {
            LineIndex = lineIndex;
            Fields = fields;
            _resolution = resolution;
            Grid = checked(
                long.Parse(fields[1], CultureInfo.InvariantCulture) * resolution +
                long.Parse(fields[2], CultureInfo.InvariantCulture));
            Position = int.Parse(fields[3], CultureInfo.InvariantCulture);
        }

        public int LineIndex { get; }
        public string[] Fields { get; }
        public long Grid { get; set; }
        public int Position { get; }

        public void UpdateFields()
        {
            Fields[1] = (Grid / _resolution).ToString(CultureInfo.InvariantCulture);
            Fields[2] = (Grid % _resolution).ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class ParsedHold
    {
        public ParsedHold(int lineIndex, string[] fields, Ma2HoldSource source, int resolution)
        {
            LineIndex = lineIndex;
            Fields = fields;
            Source = source;
            _resolution = resolution;
            Grid = checked(
                long.Parse(fields[1], CultureInfo.InvariantCulture) * resolution +
                long.Parse(fields[2], CultureInfo.InvariantCulture));
            LengthGrid = long.Parse(fields[4], CultureInfo.InvariantCulture);
        }

        private readonly int _resolution;
        public int LineIndex { get; }
        public string[] Fields { get; }
        public Ma2HoldSource Source { get; }
        public long Grid { get; set; }
        public long LengthGrid { get; set; }

        public void UpdateFields()
        {
            Fields[1] = (Grid / _resolution).ToString(CultureInfo.InvariantCulture);
            Fields[2] = (Grid % _resolution).ToString(CultureInfo.InvariantCulture);
            Fields[4] = LengthGrid.ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class ParsedTimedNote
    {
        private readonly int _resolution;

        public ParsedTimedNote(int lineIndex, string[] fields, int resolution)
        {
            LineIndex = lineIndex;
            Fields = fields;
            _resolution = resolution;
            Grid = checked(
                long.Parse(fields[1], CultureInfo.InvariantCulture) * resolution +
                long.Parse(fields[2], CultureInfo.InvariantCulture));
        }

        public int LineIndex { get; }
        public string[] Fields { get; }
        public long Grid { get; set; }

        public void UpdateFields()
        {
            Fields[1] = (Grid / _resolution).ToString(CultureInfo.InvariantCulture);
            Fields[2] = (Grid % _resolution).ToString(CultureInfo.InvariantCulture);
        }
    }

    private sealed class SlideCapture
    {
        public SlideCapture(ParsedSlide owner, ParsedSlide candidate)
        {
            Owner = owner;
            Candidate = candidate;
        }

        public ParsedSlide Owner { get; }
        public ParsedSlide Candidate { get; }
        public bool IsExpectedConnection =>
            Owner.Source.BranchId == Candidate.Source.BranchId &&
            Candidate.Source.SegmentIndex == Owner.Source.SegmentIndex + 1;
    }

    private sealed class SlideAnalysis
    {
        public SlideAnalysis(Ma2CandidateValidation validation, IReadOnlyList<SlideCapture> wrongCaptures)
        {
            Validation = validation;
            WrongCaptures = wrongCaptures;
        }

        public Ma2CandidateValidation Validation { get; }
        public IReadOnlyList<SlideCapture> WrongCaptures { get; }
    }
}

internal sealed class Ma2BpmTimeline
{
    private readonly int _resolution;
    private readonly Segment[] _segments;

    public Ma2BpmTimeline(int resolution, IEnumerable<(long Grid, float Bpm)> events)
    {
        _resolution = resolution;
        var ordered = events
            .Where(x => x.Bpm > 0)
            .OrderBy(x => x.Grid)
            .GroupBy(x => x.Grid)
            .Select(x => x.Last())
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new InvalidOperationException("MA2 has no valid BPM event.");
        }

        var segments = new List<Segment>(ordered.Length);
        var previousGrid = ordered[0].Grid;
        var previousBpm = ordered[0].Bpm;
        var previousMsec = 0f;
        segments.Add(new Segment(previousGrid, previousBpm, previousMsec));
        for (var i = 1; i < ordered.Length; i++)
        {
            var current = ordered[i];
            previousMsec += FourBeat(current.Grid - previousGrid) * 60000f / previousBpm;
            segments.Add(new Segment(current.Grid, current.Bpm, previousMsec));
            previousGrid = current.Grid;
            previousBpm = current.Bpm;
        }

        _segments = segments.ToArray();
    }

    public float CalculateMsec(long grid)
    {
        var segment = _segments[0];
        for (var i = _segments.Length - 1; i >= 0; i--)
        {
            if (_segments[i].Grid <= grid)
            {
                segment = _segments[i];
                break;
            }
        }

        return segment.Msec + FourBeat(grid - segment.Grid) * 60000f / segment.Bpm;
    }

    private float FourBeat(long grid)
    {
        return (float)grid / _resolution * 4f;
    }

    private readonly record struct Segment(long Grid, float Bpm, float Msec);
}
