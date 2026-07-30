using System.Globalization;
using System.Text;
using MajSimai;

namespace MajdataEdit.Ma2Export;

public sealed class SimaiChartConverter
{
    private static readonly Dictionary<int, string> Ma2SlideMap = new Dictionary<string, int>
    {
        { "SI_", 1 },
        { "SCL", 2 },
        { "SCR", 3 },
        { "SUL", 4 },
        { "SUR", 5 },
        { "SSL", 6 },
        { "SSR", 7 },
        { "SV_", 8 },
        { "SXL", 9 },
        { "SXR", 10 },
        { "SLL", 11 },
        { "SLR", 12 },
        { "SF_", 13 }
    }.ToDictionary(x => x.Value, x => x.Key);

    private static readonly Dictionary<string, int> SimaiSlideMap = new()
    {
        { "-", 1 },
        { "p", 4 },
        { "q", 5 },
        { "s", 6 },
        { "z", 7 },
        { "v", 8 },
        { "pp", 9 },
        { "qq", 10 },
        { "w", 13 }
    };

    public string ConvertChartToMa2Content(string chartContent, float? fallbackWholeBpm = null, int hSpeedInterpolationGrid = 32)
    {
        return ConvertChartToMa2(
            chartContent,
            fallbackWholeBpm,
            hSpeedInterpolationGrid).Content;
    }

    public Ma2ConversionResult ConvertChartToMa2(
        string chartContent,
        float? fallbackWholeBpm = null,
        int hSpeedInterpolationGrid = 32,
        Ma2AdaptiveResolutionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(chartContent))
        {
            throw new InvalidOperationException("谱面内容为空，无法生成 MA2。");
        }

        options ??= new Ma2AdaptiveResolutionOptions();
        ValidateOptions(options);

        var preparedContent = PrepareChartContent(chartContent, fallbackWholeBpm);
        var chart = SimaiParser.ParseChart(preparedContent.AsSpan(), 0, hSpeedInterpolationGrid, out _);
        var allTimingPoints = chart.NoteTimings.ToArray();
        var commaTimingPoints = chart.CommaTimings.ToArray();
        var timingPoints = allTimingPoints
            .Where(x => x.Notes.Length != 0)
            .ToArray();

        if (timingPoints.Length == 0)
        {
            throw new InvalidOperationException("谱面没有可导出的音符。");
        }

        var legalStep = CalculateLegalResolutionStep(commaTimingPoints);
        var firstResolution = RoundUpToMultiple(options.MinimumResolution, legalStep);
        if (firstResolution > options.MaximumResolution)
        {
            throw new InvalidOperationException(
                $"拍号要求分辨率为 {legalStep} 的倍数，但允许范围 " +
                $"{options.MinimumResolution}..{options.MaximumResolution} 内没有合法值。");
        }

        var engine = new Ma2AdaptiveResolutionEngine();
        Ma2CandidateBuild? lastCandidate = null;
        Ma2CandidateValidation? lastValidation = null;
        var attempts = 0;
        for (var resolution = firstResolution;
             resolution <= options.MaximumResolution;
             resolution = checked(resolution + legalStep))
        {
            attempts++;
            var candidate = BuildMa2Content(allTimingPoints, timingPoints, commaTimingPoints, resolution);
            var validation = engine.Validate(candidate);
            if (validation.IsValid)
            {
                return new Ma2ConversionResult(
                    candidate.Content,
                    new Ma2ConversionReport(
                        Ma2AdaptiveResolutionOptions.DefaultResolution,
                        resolution,
                        attempts,
                        false,
                        Array.Empty<Ma2TimingAdjustment>()));
            }

            lastCandidate = candidate;
            lastValidation = validation;
            if (!options.EnableAdaptiveResolution)
            {
                break;
            }

            if (resolution > options.MaximumResolution - legalStep)
            {
                break;
            }
        }

        if (lastCandidate is null || lastValidation is null)
        {
            throw new InvalidOperationException("没有生成任何 MA2 分辨率候选。");
        }

        if (!options.EnableMinimumGridRepair)
        {
            throw new InvalidOperationException(
                $"MA2 在分辨率 {lastCandidate.Resolution} 下验证失败，且最小 Grid 修复已禁用：{lastValidation}");
        }

        var repaired = engine.Repair(lastCandidate);
        var repairedCandidate = new Ma2CandidateBuild(
            repaired.Content,
            lastCandidate.Resolution,
            lastCandidate.SlideSources,
            lastCandidate.HoldSources);
        var repairedValidation = engine.Validate(repairedCandidate);
        if (!repairedValidation.IsValid)
        {
            throw new InvalidOperationException($"MA2 最终验证失败：{repairedValidation}");
        }

        return new Ma2ConversionResult(
            repaired.Content,
            new Ma2ConversionReport(
                Ma2AdaptiveResolutionOptions.DefaultResolution,
                lastCandidate.Resolution,
                attempts,
                true,
                repaired.Adjustments));
    }

    private static Ma2CandidateBuild BuildMa2Content(
        SimaiTimingPoint[] allTimingPoints,
        SimaiTimingPoint[] timingPoints,
        SimaiTimingPoint[] commaTimingPoints,
        int resolution)
    {

        var headerOutput = new StringBuilder();
        var compositeOutput = new StringBuilder();
        var notesOutput = new StringBuilder();
        var totalOutput = new StringBuilder();

        var currentBpm = timingPoints[0].Bpm;
        if (currentBpm <= 0)
        {
            throw new InvalidOperationException("谱面缺少有效 BPM，且没有可用的 &wholebpm=。");
        }

        var gridTimeline = new SimaiGridTimeline(allTimingPoints, resolution, currentBpm);
        long CalculateGrid(double audioTimeInSecond) => gridTimeline.CalculateGrid(audioTimeInSecond);

        var maxBpm = timingPoints.Max(x => x.Bpm);
        var minBpm = timingPoints.Min(x => x.Bpm);
        var meterEvents = commaTimingPoints
            .Where(x => x.SignatureNumerator > 0 && x.SignatureDenominator > 0)
            .OrderBy(x => x.Timing)
            .ToArray();
        var initialMeterNumerator = meterEvents.Length == 0 ? 4 : meterEvents[0].SignatureNumerator;
        var initialMeterDenominator = meterEvents.Length == 0 ? 4 : meterEvents[0].SignatureDenominator;

        headerOutput.AppendLine("VERSION\t0.00.00\t1.04.00");
        headerOutput.AppendLine("FES_MODE\t0");
        headerOutput.AppendLine(
            $"BPM_DEF\t{FormatBpmFixed(currentBpm)}\t{FormatBpmFixed(currentBpm)}\t{FormatBpmFixed(maxBpm)}\t{FormatBpmFixed(minBpm)}");
        headerOutput.AppendLine($"MET_DEF\t{initialMeterNumerator}\t{initialMeterDenominator}");
        headerOutput.AppendLine($"RESOLUTION\t{resolution}");
        headerOutput.AppendLine($"CLK_DEF\t{resolution}");
        headerOutput.AppendLine("COMPATIBLE_CODE\tMA2");
        headerOutput.AppendLine();

        compositeOutput.AppendLine($"BPM\t0\t0\t{FormatBpm(currentBpm)}");
        compositeOutput.AppendLine($"MET\t0\t0\t{initialMeterNumerator}\t{initialMeterDenominator}");
        var currentMeterNumerator = initialMeterNumerator;
        var currentMeterDenominator = initialMeterDenominator;
        foreach (var meterEvent in meterEvents)
        {
            if (meterEvent.SignatureNumerator == currentMeterNumerator &&
                meterEvent.SignatureDenominator == currentMeterDenominator)
            {
                continue;
            }

            FormatGrid(CalculateGrid(meterEvent.Timing), out var meterUnit, out var meterGrid);
            compositeOutput.AppendLine(
                $"MET\t{meterUnit}\t{meterGrid}\t{meterEvent.SignatureNumerator}\t{meterEvent.SignatureDenominator}");
            currentMeterNumerator = meterEvent.SignatureNumerator;
            currentMeterDenominator = meterEvent.SignatureDenominator;
        }
        compositeOutput.AppendLine();

        // soflanGroup -> totalGrid -> speed
        var hSpeedListMap = new Dictionary<int, Dictionary<long, float>>();

        var lastHSpeedMap = new Dictionary<int, float>();
        float getLastHSpeed(int soflanGroup) => lastHSpeedMap.GetValueOrDefault(soflanGroup, 1);
        void setLastHSpeed(int soflanGroup, float lastHSpeed) => lastHSpeedMap[soflanGroup] = lastHSpeed;

        void FormatGrid(long totalGrid, out long unit, out long grid)
        {
            if (totalGrid is < 0 or > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"MA2 Grid {totalGrid} is outside the non-negative Int32 range at resolution {resolution}.");
            }
            unit = totalGrid / resolution;
            grid = totalGrid % resolution;
        }

        static void ValidateLength(long length, string kind)
        {
            if (length is < 0 or > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"MA2 {kind} length {length} is outside the non-negative Int32 range.");
            }
        }

        var noteStatMap = new Dictionary<string, int>();
        void AddStat(string key) => noteStatMap[key] = (noteStatMap.TryGetValue(key, out var v) ? v : 0) + 1;

        var soflanGroupMap = BuildAutoSoflanGroupMap(allTimingPoints);
        int MapSoflanGroup(int soflanGroup) => soflanGroup < 0 && soflanGroupMap.TryGetValue(soflanGroup, out var mappedGroup)
            ? mappedGroup
            : soflanGroup;
        var lastSoflanTotalGrid = 0L;
        var slideSources = new List<Ma2SlideSource>();
        var holdSources = new List<Ma2HoldSource>();
        var nextSlideBranchId = 0;

        foreach (var timingPoint in allTimingPoints)
        {
            var curTime = timingPoint.Timing;
            var currentTotalGrid = CalculateGrid(curTime);
            lastSoflanTotalGrid = Math.Max(lastSoflanTotalGrid, currentTotalGrid);
            FormatGrid(currentTotalGrid, out var curUnit, out var curGrid);

            if (timingPoint.Bpm > 0 && Math.Abs(timingPoint.Bpm - currentBpm) > float.Epsilon)
            {
                compositeOutput.AppendLine($"BPM\t{curUnit}\t{curGrid}\t{FormatBpm(timingPoint.Bpm)}");

                currentBpm = timingPoint.Bpm;
            }

            var soflanGroup = timingPoint.SoflanGroup;
            var prevHSpeed = getLastHSpeed(soflanGroup);
            if (Math.Abs(timingPoint.HSpeed - prevHSpeed) > float.Epsilon)
            {
                if (!hSpeedListMap.TryGetValue(soflanGroup, out var speedMap))
                    hSpeedListMap[soflanGroup] = speedMap = new Dictionary<long, float>();
                speedMap[currentTotalGrid] = timingPoint.HSpeed;

                setLastHSpeed(soflanGroup, timingPoint.HSpeed);
            }

            foreach (var note in timingPoint.Notes)
            {
                ValidateForceYellowCompatibility(note);
                if (!note.IsSlideNoHead)
                {
                    var id = GetMa2NoteId(note);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        if (HasForceYellow(note))
                        {
                            throw new InvalidOperationException("Force Yellow cannot be exported for this note type");
                        }
                        continue;
                    }

                    notesOutput.Append($"{id}\t{curUnit}\t{curGrid}\t{(note.TouchArea == 'C' ? 0 : note.StartPosition - 1)}");

                    void AppendAsHold()
                    {
                        var endTime = note.HoldTime + curTime;
                        var durationGrids = CalculateGrid(endTime) - currentTotalGrid;
                        ValidateLength(durationGrids, "Hold");
                        notesOutput.Append($"\t{durationGrids}");
                    }

                    void AppendAsTouch()
                    {
                        notesOutput.Append($"\t{note.TouchArea}\t{(note.IsHanabi ? 1 : 0)}\tM1");
                    }

                    switch (note.Type)
                    {
                        case SimaiNoteType.Hold:
                            AppendAsHold();
                            break;
                        case SimaiNoteType.Touch:
                            AppendAsTouch();
                            break;
                        case SimaiNoteType.TouchHold:
                            AppendAsHold();
                            AppendAsTouch();
                            break;
                    }

                    if (note.Type is SimaiNoteType.Hold or SimaiNoteType.TouchHold)
                    {
                        holdSources.Add(new Ma2HoldSource
                        {
                            SourceIndex = holdSources.Count,
                            SourceLine = timingPoint.RawTextPositionY + 1,
                            SourceText = timingPoint.RawContent,
                            OriginalDurationSeconds = note.HoldTime
                        });
                    }

                    AppendNoteTail(
                        notesOutput,
                        note.IsMine,
                        MapSoflanGroup(note.SoflanGroup),
                        note.IsFixedSoflan,
                        note.HasFixedSoflanSpeed,
                        note.FixedSoflanSpeed,
                        note.IsForceYellow);

                    notesOutput.AppendLine();
                    AddStat(id);
                }

                if (note.Type != SimaiNoteType.Slide)
                {
                    continue;
                }

                var subSlides = InstantiateStarGroup(timingPoint, note);
                var segmentTimings = SlideSegmentTimingAnalysis.Analyze(timingPoint, note);
                if (segmentTimings.Length != subSlides.Count)
                {
                    throw new InvalidOperationException(
                        $"Slide 分段数量不一致：轨迹={subSlides.Count}，时长={segmentTimings.Length}。");
                }

                var branchId = nextSlideBranchId++;
                AddStat(note.IsSlideBreak ? "BSL" : "SLD");
                var slideOutput = new List<(string SlideId, long Grid, long StartPos, long WaitGrid, long DurationGrid, long EndPos, bool IsForceYellow)>();

                for (var i = 0; i < subSlides.Count; i++)
                {
                    var subSlide = subSlides[i];
                    var prefix = subSlide.IsSlideBreak ? "BR" : "NM";
                    if (i > 0)
                    {
                        prefix = "CN";
                    }

                    var curPosition = int.Parse(subSlide.RawContent[0].ToString(), CultureInfo.InvariantCulture);
                    var pattern = subSlide.RawContent[1].ToString();
                    if ((pattern == "p" || pattern == "q") && pattern[0] == subSlide.RawContent[2])
                    {
                        pattern += pattern;
                    }

                    var endPosition = int.Parse((pattern switch
                    {
                        "pp" or "qq" or "V" => subSlide.RawContent[3],
                        _ => subSlide.RawContent[2]
                    }).ToString(), CultureInfo.InvariantCulture);

                    var simaiId = pattern switch
                    {
                        "^" => IsClockwise(curPosition, endPosition) ? 3 : 2,
                        ">" => IsTop(curPosition) ? 3 : 2,
                        "<" => IsTop(curPosition) ? 2 : 3,
                        "V" => IsClockwise(curPosition,
                            int.Parse(subSlide.RawContent[2].ToString(), CultureInfo.InvariantCulture)) ? 12 : 11,
                        _ => SimaiSlideMap[pattern]
                    };

                    var isSelfReturningV = pattern == "v" && curPosition == endPosition;
                    var slideId = isSelfReturningV ? "SHL" : prefix + Ma2SlideMap[simaiId];
                    var segmentTiming = segmentTimings[i];
                    var segmentStartGrid = CalculateGrid(segmentTiming.StartTime);
                    var segmentEndGrid = CalculateGrid(segmentTiming.StartTime + segmentTiming.Duration);
                    var grid = i == 0 ? currentTotalGrid : segmentStartGrid;
                    var waitGrid = i == 0 ? segmentStartGrid - currentTotalGrid : 0;
                    var durationGrid = segmentEndGrid - segmentStartGrid;
                    ValidateLength(waitGrid, "Slide wait");
                    ValidateLength(durationGrid, "Slide duration");

                    slideOutput.Add((slideId, grid, subSlide.StartPosition - 1, waitGrid, durationGrid,
                        endPosition - 1, subSlide.IsForceYellow));
                }

                for (var segmentIndex = 0; segmentIndex < slideOutput.Count; segmentIndex++)
                {
                    var (slideId, grid, startPos, waitGrid, durationGrid, endPos, isForceYellow) =
                        slideOutput[segmentIndex];
                    FormatGrid(grid, out var slideUnit, out var slideGrid);
                    notesOutput.Append($"{slideId}\t{slideUnit}\t{slideGrid}\t{startPos}\t{waitGrid}\t{durationGrid}\t{endPos}");
                    AppendNoteTail(
                        notesOutput,
                        note.IsMineSlide,
                        MapSoflanGroup(note.SlideSoflanGroup),
                        isForceYellow: isForceYellow,
                        isForceYellowMovingStar: segmentIndex == 0 && note.IsForceYellow);
                    notesOutput.AppendLine();
                    slideSources.Add(new Ma2SlideSource
                    {
                        BranchId = branchId,
                        SegmentIndex = segmentIndex,
                        SourceLine = timingPoint.RawTextPositionY + 1,
                        SourceText = timingPoint.RawContent,
                        HasHead = !note.IsSlideNoHead,
                        OriginalWaitSeconds = segmentIndex == 0
                            ? Math.Max(0, note.SlideStartTime - timingPoint.Timing)
                            : 0,
                        OriginalDurationSeconds = segmentTimings[segmentIndex].Duration
                    });
                    lastSoflanTotalGrid = Math.Max(lastSoflanTotalGrid, grid + waitGrid + durationGrid);
                }
            }
        }

        //generate Soflan
        compositeOutput.AppendLine();
        foreach (var pair1 in hSpeedListMap)
        {
            var soflanGroup = pair1.Key;
            var outputSoflanGroup = MapSoflanGroup(soflanGroup);
            var speedList = pair1.Value.OrderBy(x => x.Key).ToList();

            for (int i = 0; i < speedList.Count - 1; i++)
            {
                var curSpeedPoint = speedList[i];
                var nextSpeedPoint = speedList[i + 1];

                var totalGrid = curSpeedPoint.Key;
                var speed = curSpeedPoint.Value;
                FormatGrid(totalGrid, out var unit, out var grid);

                var duration = nextSpeedPoint.Key - curSpeedPoint.Key;
                ValidateLength(duration, "Soflan");

                compositeOutput.AppendLine($"SFL\t{unit}\t{grid}\t{duration}\t{speed:F6}\t{outputSoflanGroup}");
            }
            {
                var lastSpeedPoint = speedList[^1];

                var totalGrid = lastSpeedPoint.Key;
                var speed = lastSpeedPoint.Value;
                FormatGrid(totalGrid, out var unit, out var grid);

                var duration = lastSoflanTotalGrid + 1 - lastSpeedPoint.Key;
                ValidateLength(duration, "Soflan");

                compositeOutput.AppendLine($"SFL\t{unit}\t{grid}\t{duration}\t{speed:F6}\t{outputSoflanGroup}");
            }
        }

        notesOutput.AppendLine();

        var totalNotes = 0;
        foreach (var stat in noteStatMap)
        {
            totalNotes += stat.Value;
            totalOutput.AppendLine($"T_REC_{stat.Key}\t{stat.Value}");
        }

        totalOutput.AppendLine($"T_REC_ALL\t{totalNotes}");

        var numTaps = new[] { "NMTAP", "EXTAP", "NMSTR", "EXSTR", "NMTTP" }
            .Select(x => noteStatMap.TryGetValue(x, out var r) ? r : 0)
            .Sum();
        var numBreaks = new[] { "BRTAP", "BRSTR", "BXTAP", "BXSTR" }
            .Select(x => noteStatMap.TryGetValue(x, out var r) ? r : 0)
            .Sum();
        var numHolds = new[] { "NMHLD", "EXHLD", "BXHLD", "NMTHO" }
            .Select(x => noteStatMap.TryGetValue(x, out var r) ? r : 0)
            .Sum();
        var numSlides = new[] { "SLD" }
            .Select(x => noteStatMap.TryGetValue(x, out var r) ? r : 0)
            .Sum();

        totalOutput.AppendLine($"T_NUM_TAP\t{numTaps}");
        totalOutput.AppendLine($"T_NUM_BRK\t{numBreaks}");
        totalOutput.AppendLine($"T_NUM_HLD\t{numHolds}");
        totalOutput.AppendLine($"T_NUM_SLD\t{numSlides}");
        totalOutput.AppendLine($"T_NUM_ALL\t{totalNotes}");

        var judgeTaps = numTaps + numBreaks;
        var judgeHolds = Math.Round(numHolds * 1.75);
        var judgeAll = judgeTaps + judgeHolds + numSlides;

        totalOutput.AppendLine($"T_JUDGE_TAP\t{judgeTaps}");
        totalOutput.AppendLine($"T_JUDGE_HLD\t{judgeHolds}");
        totalOutput.AppendLine($"T_JUDGE_SLD\t{numSlides}");
        totalOutput.AppendLine($"T_JUDGE_ALL\t{judgeAll}");

        var numEachPairs = EachNoteAnalysis.Analyze(timingPoints).GroupCount;
        totalOutput.AppendLine($"TTM_EACHPAIRS\t{numEachPairs}");

        var totalMaxScoreTap = 500 * numTaps;
        var totalMaxScoreBreak = 2600 * numBreaks;
        var totalMaxScoreHold = 1000 * numHolds;
        var totalMaxScoreSlide = 1500 * numSlides;
        var totalMaxScore = totalMaxScoreTap + totalMaxScoreBreak + totalMaxScoreHold + totalMaxScoreSlide;

        totalOutput.AppendLine($"TTM_SCR_TAP\t{totalMaxScoreTap}");
        totalOutput.AppendLine($"TTM_SCR_BRK\t{totalMaxScoreBreak}");
        totalOutput.AppendLine($"TTM_SCR_HLD\t{totalMaxScoreHold}");
        totalOutput.AppendLine($"TTM_SCR_SLD\t{totalMaxScoreSlide}");
        totalOutput.AppendLine($"TTM_SCR_ALL\t{totalMaxScore}");

        var totalBaseScore = totalMaxScoreTap + totalMaxScoreHold + totalMaxScoreSlide + 2500 * numBreaks;
        var maxFinaleAchievement = totalBaseScore == 0 ? 0 : (int)(10000.0f * totalMaxScore / totalBaseScore);
        var totalMaxScoreS = (int)Math.Round(0.97 * totalBaseScore / 100) * 100;
        var totalMaxScoreSs = totalBaseScore;

        totalOutput.AppendLine($"TTM_SCR_S\t{totalMaxScoreS}");
        totalOutput.AppendLine($"TTM_SCR_SS\t{totalMaxScoreSs}");
        totalOutput.AppendLine($"TTM_RAT_ACV\t{maxFinaleAchievement}");

        var content = headerOutput.ToString() + compositeOutput + notesOutput + totalOutput;
        return new Ma2CandidateBuild(content, resolution, slideSources, holdSources);
    }

    public IReadOnlyList<Ma2ExportResult> ConvertSelectedCharts(
        IEnumerable<Ma2ExportChart> charts,
        string musicId6,
        float? fallbackWholeBpm = null,
        int hSpeedInterpolationGrid = 32)
    {
        var results = new List<Ma2ExportResult>();
        foreach (var chart in charts)
        {
            var conversion = ConvertChartToMa2(chart.ChartContent, fallbackWholeBpm, hSpeedInterpolationGrid);
            results.Add(new Ma2ExportResult(
                chart.DiffId,
                $"music{musicId6}_{chart.DiffId}.ma2",
                conversion.Content,
                conversion.Report));
        }

        return results;
    }

    private static void ValidateOptions(Ma2AdaptiveResolutionOptions options)
    {
        if (options.MinimumResolution < Ma2AdaptiveResolutionOptions.DefaultResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Minimum MA2 resolution cannot be lower than {Ma2AdaptiveResolutionOptions.DefaultResolution}.");
        }

        if (options.MaximumResolution > Ma2AdaptiveResolutionOptions.DefaultMaximumResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Maximum MA2 resolution cannot exceed {Ma2AdaptiveResolutionOptions.DefaultMaximumResolution}.");
        }

        if (options.MinimumResolution > options.MaximumResolution)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum MA2 resolution exceeds the maximum.");
        }
    }

    private static int CalculateLegalResolutionStep(IEnumerable<SimaiTimingPoint> commaTimings)
    {
        long result = Ma2AdaptiveResolutionOptions.DefaultResolution;
        foreach (var denominator in commaTimings
                     .Select(x => x.SignatureDenominator)
                     .Where(x => x > 0)
                     .Distinct())
        {
            result = LeastCommonMultiple(result, denominator);
            if (result > Ma2AdaptiveResolutionOptions.DefaultMaximumResolution)
            {
                throw new InvalidOperationException(
                    $"拍号分母 {denominator} 使合法分辨率步长超过 " +
                    $"{Ma2AdaptiveResolutionOptions.DefaultMaximumResolution}。");
            }
        }

        return checked((int)result);
    }

    private static long LeastCommonMultiple(long left, long right)
    {
        static long GreatestCommonDivisor(long a, long b)
        {
            while (b != 0)
            {
                (a, b) = (b, a % b);
            }

            return Math.Abs(a);
        }

        return checked(left / GreatestCommonDivisor(left, right) * right);
    }

    private static int RoundUpToMultiple(int value, int step)
    {
        return checked((int)(((long)value + step - 1) / step * step));
    }

    private static string PrepareChartContent(string chartContent, float? fallbackWholeBpm)
    {
        if (!fallbackWholeBpm.HasValue || fallbackWholeBpm.Value <= 0 || HasInitialBpmDeclaration(chartContent))
        {
            return chartContent;
        }

        return $"({FormatBpm(fallbackWholeBpm.Value)})\n{chartContent}";
    }

    private static bool HasInitialBpmDeclaration(string chartContent)
    {
        for (var i = 0; i < chartContent.Length; i++)
        {
            var current = chartContent[i];
            if (char.IsWhiteSpace(current))
            {
                continue;
            }

            if (current == '|' && i + 1 < chartContent.Length && chartContent[i + 1] == '|')
            {
                while (i < chartContent.Length && chartContent[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            return current == '(';
        }

        return false;
    }

    private static Dictionary<int, int> BuildAutoSoflanGroupMap(IEnumerable<SimaiTimingPoint> timingPoints)
    {
        var explicitMaxGroup = 0;
        var autoGroupFirstSeen = new Dictionary<int, (double Timing, int Order)>();
        var autoGroupOrder = 0;

        foreach (var timingPoint in timingPoints)
        {
            TrackGroup(timingPoint.SoflanGroup, timingPoint.Timing);
            foreach (var note in timingPoint.Notes)
            {
                TrackGroup(note.SoflanGroup, timingPoint.Timing);
                if (note.Type == SimaiNoteType.Slide)
                {
                    TrackGroup(note.SlideSoflanGroup, timingPoint.Timing);
                }
            }
        }

        if (autoGroupFirstSeen.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var nextGroup = GetAutoSoflanGroupBase(explicitMaxGroup);
        var groupMap = new Dictionary<int, int>();
        foreach (var group in autoGroupFirstSeen.OrderBy(x => x.Value.Timing).ThenBy(x => x.Value.Order))
        {
            groupMap[group.Key] = nextGroup++;
        }

        return groupMap;

        void TrackGroup(int soflanGroup, double timing)
        {
            if (soflanGroup > explicitMaxGroup)
            {
                explicitMaxGroup = soflanGroup;
            }
            else if (soflanGroup < 0 && !autoGroupFirstSeen.ContainsKey(soflanGroup))
            {
                autoGroupFirstSeen[soflanGroup] = (timing, autoGroupOrder++);
            }
        }
    }

    private static int GetAutoSoflanGroupBase(int maxExplicitGroup)
    {
        var digits = Math.Max(1, maxExplicitGroup.ToString(CultureInfo.InvariantCulture).Length);
        var result = 1;
        for (var i = 0; i < digits; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static string GetMa2NoteId(SimaiNote note)
    {
        return note.Type switch
        {
            // Force star (simai `$` / `$$`) renders a tap as a star head, so emit STR instead of TAP.
            // ma2 has no rotation field for a standalone star, so fake rotation (`$$`) degrades to a static STR.
            SimaiNoteType.Tap => GetNormalBreakExId(note, note.IsForceStar ? "STR" : "TAP"),
            SimaiNoteType.Slide => GetNormalBreakExId(note, "STR"),
            SimaiNoteType.Hold => GetNormalBreakExId(note, "HLD"),
            SimaiNoteType.Touch => "NMTTP",
            SimaiNoteType.TouchHold => "NMTHO",
            _ => string.Empty
        };
    }

    private static string GetNormalBreakExId(SimaiNote note, string suffix)
    {
        if (note.IsBreak && note.IsEx)
        {
            return "BX" + suffix;
        }

        if (note.IsBreak)
        {
            return "BR" + suffix;
        }

        return note.IsEx ? "EX" + suffix : "NM" + suffix;
    }

    private static void AppendNoteTail(
        StringBuilder output,
        bool isMine,
        int soflanGroup = 0,
        bool isFixedSoflan = false,
        bool hasFixedSoflanSpeed = false,
        float fixedSoflanSpeed = 0,
        bool isForceYellow = false,
        bool isForceYellowMovingStar = false)
    {
        if (isMine && (isForceYellow || isForceYellowMovingStar))
        {
            throw new InvalidOperationException("Force Yellow cannot coexist with Mine");
        }

        if (!isMine && !isForceYellow && !isForceYellowMovingStar && soflanGroup == 0 && !isFixedSoflan)
        {
            return;
        }

        output.Append('\t');
        if (isMine)
        {
            output.Append("!m");
        }
        if (isForceYellowMovingStar)
        {
            output.Append("!yh");
        }
        if (isForceYellow)
        {
            output.Append("!y");
        }

        if (soflanGroup == 0 && !isFixedSoflan)
        {
            return;
        }

        output.Append('#');
        if (soflanGroup != 0)
            output.Append(soflanGroup.ToString(CultureInfo.InvariantCulture));
        if (isFixedSoflan)
        {
            output.Append('F');
            if (hasFixedSoflanSpeed)
                output.Append(fixedSoflanSpeed.ToString("G9", CultureInfo.InvariantCulture));
        }
    }

    private static List<SlidePart> InstantiateStarGroup(SimaiTimingPoint timing, SimaiNote note)
    {
        var subSlide = new List<SlidePart>();
        var subBarCount = new List<int>();
        var sumBarCount = 0;
        var noteContent = note.RawContent;

        if (string.IsNullOrWhiteSpace(noteContent))
        {
            throw new InvalidOperationException("Slide 缺少原始内容。");
        }

        var latestStartIndex = CharIntParse(noteContent[0]);
        var ptr = 1;
        var specTimeFlag = 0;

        while (ptr < noteContent.Length)
        {
            if (char.IsNumber(noteContent[ptr]))
            {
                throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
            }

            var slideTypeChar = noteContent[ptr++].ToString();
            var slidePart = new SlidePart
            {
                StartPosition = latestStartIndex
            };

            if (slideTypeChar == "V")
            {
                if (ptr + 1 >= noteContent.Length)
                {
                    throw new InvalidOperationException("V 星星参数不完整。");
                }

                var middlePos = noteContent[ptr++];
                var endPos = noteContent[ptr++];
                slidePart.RawContent = latestStartIndex + slideTypeChar + middlePos + endPos;
                latestStartIndex = CharIntParse(endPos);
            }
            else
            {
                if (ptr >= noteContent.Length)
                {
                    throw new InvalidOperationException("星星参数不完整。");
                }

                if (noteContent[ptr] == slideTypeChar[0])
                {
                    slideTypeChar += noteContent[ptr++];
                }

                if (ptr >= noteContent.Length)
                {
                    throw new InvalidOperationException("星星终点不完整。");
                }

                var endPos = noteContent[ptr++];
                slidePart.RawContent = latestStartIndex + slideTypeChar + endPos;
                latestStartIndex = CharIntParse(endPos);
            }

            if (ptr < noteContent.Length && noteContent[ptr] == '[')
            {
                if (specTimeFlag == 0)
                {
                    specTimeFlag = 2;
                }
                else if (specTimeFlag == 1)
                {
                    specTimeFlag = 3;
                }
                else if (specTimeFlag == 3)
                {
                    throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
                }

                while (ptr < noteContent.Length && noteContent[ptr] != ']')
                {
                    slidePart.RawContent += noteContent[ptr++];
                }

                if (ptr >= noteContent.Length)
                {
                    throw new InvalidOperationException("星星时长参数缺少 ]。");
                }

                slidePart.RawContent += noteContent[ptr++];
            }
            else
            {
                if (specTimeFlag == 0)
                {
                    specTimeFlag = 1;
                }
                else if (specTimeFlag == 2 || specTimeFlag == 3)
                {
                    throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
                }
            }

            var slideIndex = DetectShapeFromText(slidePart.RawContent);
            if (slideIndex < 0)
            {
                slideIndex = -slideIndex;
            }

            const int barCount = 12;
            subBarCount.Add(barCount);
            sumBarCount += barCount;
            subSlide.Add(slidePart);
        }

        foreach (var slide in subSlide)
        {
            slide.IsBreak = note.IsBreak;
            slide.IsEx = note.IsEx;
            slide.IsSlideBreak = note.IsSlideBreak;
            slide.IsSlideNoHead = true;
        }

        var forceYellowFlags = ForceYellowSlideSegmentHelper.ResolveFlags(note, subSlide.Count);
        for (var i = 0; i < subSlide.Count; i++)
        {
            subSlide[i].IsForceYellow = forceYellowFlags[i];
        }

        if (subSlide.Count == 0)
        {
            throw new InvalidOperationException("Slide 拆分结果为空。");
        }

        subSlide[0].IsSlideNoHead = note.IsSlideNoHead;

        if (specTimeFlag == 1 || specTimeFlag == 0)
        {
            throw new InvalidOperationException("组合星星有错误\nSLIDE CHAIN ERROR");
        }

        if (specTimeFlag == 3)
        {
            var tempBarCount = 0;
            for (var i = 0; i < subSlide.Count; i++)
            {
                subSlide[i].SlideStartTime = note.SlideStartTime + ((i + 1.0f) / subSlide.Count) * note.SlideTime;
                subSlide[i].SlideTime = note.SlideTime / subSlide.Count;
                tempBarCount += subBarCount[i];
            }
        }
        else
        {
            double tempSlideTime = 0;
            for (var i = 0; i < subSlide.Count; i++)
            {
                subSlide[i].SlideStartTime = note.SlideStartTime + tempSlideTime;
                subSlide[i].SlideTime = GetTimeFromBeats(subSlide[i].RawContent, timing.Bpm);
                tempSlideTime += subSlide[i].SlideTime;
            }
        }

        return subSlide;
    }

    private static int DetectShapeFromText(string content)
    {
        static bool IsUpperHalf(int key) => key is 7 or 8 or 1 or 2;

        static int MirrorKeys(int key)
        {
            return key switch
            {
                1 => 1,
                2 => 8,
                3 => 7,
                4 => 6,
                5 => 5,
                6 => 4,
                7 => 3,
                8 => 2,
                _ => throw new InvalidOperationException("Keys out of range: " + key)
            };
        }

        if (content.Contains('-'))
        {
            var digits = content[..3].Split('-');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            if (endPos < 3 || endPos > 7)
            {
                throw new InvalidOperationException("-星星至少隔开一键\n-スライドエラー");
            }

            return endPos - 3;
        }

        if (content.Contains('>'))
        {
            var digits = content[..3].Split('>');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);

            if (IsUpperHalf(startPos))
            {
                return endPos + 4;
            }

            if (endPos != 1)
            {
                endPos = MirrorKeys(endPos);
            }

            return -(endPos + 4);
        }

        if (content.Contains('<'))
        {
            var digits = content[..3].Split('<');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);

            if (!IsUpperHalf(startPos))
            {
                return endPos + 4;
            }

            if (endPos != 1)
            {
                endPos = MirrorKeys(endPos);
            }

            return -(endPos + 4);
        }

        if (content.Contains('^'))
        {
            var digits = content[..3].Split('^');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos -= startPos;
            endPos = endPos < 0 ? endPos + 8 : endPos;
            endPos = endPos > 8 ? endPos - 8 : endPos;

            if (endPos < 4)
            {
                return endPos + 1 + 4;
            }

            if (endPos > 4)
            {
                return -(MirrorKeys(endPos + 1) + 4);
            }

            throw new InvalidOperationException("^星星不合法\n^スライドエラー");
        }

        if (content.Contains('v'))
        {
            var digits = content[..3].Split('v');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            if (endPos == 5)
            {
                throw new InvalidOperationException("v星星不合法\nvスライドエラー");
            }

            if (endPos > 4)
            {
                return endPos + 10;
            }

            if (endPos < 6)
            {
                return endPos + 11;
            }
        }

        if (content.Contains("pp"))
        {
            var digits = content[..4].Split('p');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[2], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            return endPos + 18;
        }

        if (content.Contains("qq"))
        {
            var digits = content[..4].Split('q');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[2], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            if (endPos != 1)
            {
                endPos = MirrorKeys(endPos);
            }

            return -(endPos + 18);
        }

        if (content.Contains('p'))
        {
            var digits = content[..3].Split('p');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            return endPos + 26;
        }

        if (content.Contains('q'))
        {
            var digits = content[..3].Split('q');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            if (endPos != 1)
            {
                endPos = MirrorKeys(endPos);
            }

            return -(endPos + 26);
        }

        if (content.Contains('s'))
        {
            var digits = content[..3].Split('s');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            if (endPos != 5)
            {
                throw new InvalidOperationException("s星星尾部错误\nsスライドエラー");
            }

            return 35;
        }

        if (content.Contains('z'))
        {
            var digits = content[..3].Split('z');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1], CultureInfo.InvariantCulture);
            endPos = ToRelativePosition(startPos, endPos);
            if (endPos != 5)
            {
                throw new InvalidOperationException("z星星尾部错误\nzスライドエラー");
            }

            return -35;
        }

        if (content.Contains('V'))
        {
            var digits = content[..4].Split('V');
            var startPos = int.Parse(digits[0], CultureInfo.InvariantCulture);
            var turnPos = int.Parse(digits[1][0].ToString(), CultureInfo.InvariantCulture);
            var endPos = int.Parse(digits[1][1].ToString(), CultureInfo.InvariantCulture);

            turnPos = ToRelativePosition(startPos, turnPos);
            endPos = ToRelativePosition(startPos, endPos);
            if (turnPos == 7)
            {
                if (endPos < 2 || endPos > 5)
                {
                    throw new InvalidOperationException("V星星终点不合法\nVスライドエラー");
                }

                return endPos + 35;
            }

            if (turnPos == 3)
            {
                if (endPos < 5)
                {
                    throw new InvalidOperationException("V星星终点不合法\nVスライドエラー");
                }

                return -(MirrorKeys(endPos) + 35);
            }

            throw new InvalidOperationException("V星星拐点只能隔开一键\nVスライドエラー");
        }

        return 0;
    }

    private static int ToRelativePosition(int startPos, int endPos)
    {
        endPos -= startPos;
        endPos = endPos < 0 ? endPos + 8 : endPos;
        endPos = endPos > 8 ? endPos - 8 : endPos;
        return endPos + 1;
    }

    private static bool IsClockwise(int startPosition, int endPosition)
    {
        startPosition -= 1;
        endPosition -= 1;

        var diff = Math.Abs(endPosition - startPosition);
        var otherDiff = Math.Abs(8 - diff);
        return !((endPosition > startPosition && diff > otherDiff) ||
                 (endPosition < startPosition && diff < otherDiff));
    }

    private static bool IsTop(int position)
    {
        return (position - 1) switch
        {
            0 or 1 or 6 or 7 => true,
            _ => false
        };
    }

    private static double GetTimeFromBeats(string noteText, float currentBpm)
    {
        var startIndex = noteText.IndexOf('[');
        var overIndex = noteText.IndexOf(']');
        if (startIndex < 0 || overIndex < 0 || overIndex <= startIndex)
        {
            throw new InvalidOperationException("星星时长参数不完整。");
        }

        var innerString = noteText[(startIndex + 1)..overIndex];
        var timeOneBeat = 1d / (currentBpm / 60d);
        if (innerString.Count(o => o == '#') == 1)
        {
            var times = innerString.Split('#');
            if (times[1].Contains(':'))
            {
                innerString = times[1];
                timeOneBeat = 1d / (double.Parse(times[0], CultureInfo.InvariantCulture) / 60d);
            }
            else
            {
                return double.Parse(times[1], CultureInfo.InvariantCulture);
            }
        }

        if (innerString.Count(o => o == '#') == 2)
        {
            var times = innerString.Split('#');
            return double.Parse(times[2], CultureInfo.InvariantCulture);
        }

        var numbers = innerString.Split(':');
        var divide = int.Parse(numbers[0], CultureInfo.InvariantCulture);
        var count = int.Parse(numbers[1], CultureInfo.InvariantCulture);

        return timeOneBeat * 4d / divide * count;
    }

    private static int CharIntParse(char c) => c - '0';

    private static void ValidateForceYellowCompatibility(SimaiNote note)
    {
        var segmentIndices = note.ForceYellowSlideSegmentIndices ?? Array.Empty<int>();
        if (note.Type != SimaiNoteType.Slide && segmentIndices.Length != 0)
        {
            throw new InvalidOperationException("Force Yellow slide segment index is invalid");
        }

        var hasForceYellow = HasForceYellow(note);
        if (!hasForceYellow)
        {
            return;
        }

        if (note.IsBreak || note.IsSlideBreak)
        {
            throw new InvalidOperationException("Force Yellow cannot coexist with Break");
        }
        if (note.IsMine || note.IsMineSlide)
        {
            throw new InvalidOperationException("Force Yellow cannot coexist with Mine");
        }
    }

    private static bool HasForceYellow(SimaiNote note)
    {
        return note.IsForceYellow || (note.ForceYellowSlideSegmentIndices?.Length ?? 0) != 0;
    }

    private static string FormatBpm(float bpm) => bpm.ToString("G9", CultureInfo.InvariantCulture);
    private static string FormatBpmFixed(float bpm) => bpm.ToString("F3", CultureInfo.InvariantCulture);

    private sealed class SlidePart
    {
        public bool IsBreak { get; set; }
        public bool IsSlideBreak { get; set; }
        public bool IsEx { get; set; }
        public bool IsSlideNoHead { get; set; }
        public bool IsForceYellow { get; set; }
        public int StartPosition { get; set; }
        public double SlideStartTime { get; set; }
        public double SlideTime { get; set; }
        public string RawContent { get; set; } = string.Empty;
    }

    private sealed class SimaiGridTimeline
    {
        private readonly int _resolution;
        private readonly Segment[] _segments;

        public SimaiGridTimeline(
            IEnumerable<SimaiTimingPoint> timingPoints,
            int resolution,
            float initialBpm)
        {
            _resolution = resolution;
            var segments = new List<Segment> { new(0, 0, initialBpm) };
            var currentTime = 0d;
            var currentGrid = 0L;
            var currentBpm = initialBpm;

            foreach (var timingPoint in timingPoints.OrderBy(x => x.Timing))
            {
                if (timingPoint.Bpm <= 0 ||
                    Math.Abs(timingPoint.Bpm - currentBpm) <= float.Epsilon)
                {
                    continue;
                }

                currentGrid = checked(currentGrid + Quantize(timingPoint.Timing - currentTime, currentBpm));
                currentTime = timingPoint.Timing;
                currentBpm = timingPoint.Bpm;
                if (segments[^1].Time == currentTime)
                {
                    segments[^1] = new Segment(currentTime, currentGrid, currentBpm);
                }
                else
                {
                    segments.Add(new Segment(currentTime, currentGrid, currentBpm));
                }
            }

            _segments = segments.ToArray();
        }

        public long CalculateGrid(double time)
        {
            var segment = _segments[0];
            for (var i = _segments.Length - 1; i >= 0; i--)
            {
                if (_segments[i].Time <= time)
                {
                    segment = _segments[i];
                    break;
                }
            }

            return checked(segment.Grid + Quantize(time - segment.Time, segment.Bpm));
        }

        private long Quantize(double seconds, float bpm)
        {
            return checked((long)Math.Round(
                seconds * _resolution * bpm / 240d,
                MidpointRounding.ToEven));
        }

        private readonly record struct Segment(double Time, long Grid, float Bpm);
    }
}
