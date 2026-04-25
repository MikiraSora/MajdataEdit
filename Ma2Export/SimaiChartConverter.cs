using System.Globalization;
using System.Text;
using MajSimai;

namespace MajdataEdit.Ma2Export;

public sealed class SimaiChartConverter
{
    private const long Resolution = 384;

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

    public string ConvertChartToMa2Content(string chartContent, double shiftTime, float? fallbackWholeBpm = null)
    {
        if (string.IsNullOrWhiteSpace(chartContent))
        {
            throw new InvalidOperationException("谱面内容为空，无法生成 MA2。");
        }

        var preparedContent = PrepareChartContent(chartContent, fallbackWholeBpm);
        var chart = SimaiParser.ParseChart(preparedContent.AsSpan(), 0, out _);
        var timingPoints = chart.NoteTimings.ToArray()
            .Where(x => x.Notes.Length != 0)
            .OrderBy(x => x.Timing)
            .ToArray();

        if (timingPoints.Length == 0)
        {
            throw new InvalidOperationException("谱面没有可导出的音符。");
        }

        var headerOutput = new StringBuilder();
        var compositeOutput = new StringBuilder();
        var notesOutput = new StringBuilder();
        var totalOutput = new StringBuilder();

        var currentBpm = timingPoints[0].Bpm;
        if (currentBpm <= 0)
        {
            throw new InvalidOperationException("谱面缺少有效 BPM，且没有可用的 &wholebpm=。");
        }

        var currentBpmBaseGrid = 0L;
        var currentBpmBaseAudioTime = 0d;
        var shiftTimeGrid = 0L;

        long CalculateGrid(double audioTimeInSecond)
        {
            var d = audioTimeInSecond - currentBpmBaseAudioTime;
            if (d == 0)
            {
                return currentBpmBaseGrid + shiftTimeGrid;
            }

            var totalGrid = Math.Round(d * (Resolution * currentBpm) / 240d);
            return currentBpmBaseGrid + (long)totalGrid + shiftTimeGrid;
        }

        shiftTimeGrid = CalculateGrid(shiftTime);

        var maxBpm = timingPoints.Max(x => x.Bpm);
        var minBpm = timingPoints.Min(x => x.Bpm);

        headerOutput.AppendLine("VERSION\t0.00.00\t1.04.00");
        headerOutput.AppendLine("FES_MODE\t0");
        headerOutput.AppendLine(
            $"BPM_DEF\t{FormatBpmFixed(currentBpm)}\t{FormatBpmFixed(currentBpm)}\t{FormatBpmFixed(maxBpm)}\t{FormatBpmFixed(minBpm)}");
        headerOutput.AppendLine("MET_DEF\t4\t4");
        headerOutput.AppendLine($"RESOLUTION\t{Resolution}");
        headerOutput.AppendLine($"CLK_DEF\t{Resolution}");
        headerOutput.AppendLine("COMPATIBLE_CODE\tMA2");
        headerOutput.AppendLine();

        compositeOutput.AppendLine($"BPM\t0\t0\t{FormatBpm(currentBpm)}");
        compositeOutput.AppendLine("MET\t0\t0\t4\t4");
        compositeOutput.AppendLine();

        static void FormatGrid(long time, out long unit, out long grid)
        {
            unit = time / Resolution;
            grid = time % Resolution;
        }

        var noteStatMap = new Dictionary<string, int>();
        void AddStat(string key) => noteStatMap[key] = (noteStatMap.TryGetValue(key, out var v) ? v : 0) + 1;

        foreach (var timingPoint in timingPoints)
        {
            var curTime = timingPoint.Timing;
            var currentTotalGrid = CalculateGrid(curTime);
            FormatGrid(currentTotalGrid, out var curUnit, out var curGrid);

            if (Math.Abs(timingPoint.Bpm - currentBpm) > float.Epsilon)
            {
                compositeOutput.AppendLine($"BPM\t{curUnit}\t{curGrid}\t{FormatBpm(timingPoint.Bpm)}");

                currentBpm = timingPoint.Bpm;
                currentBpmBaseAudioTime = curTime;
                currentBpmBaseGrid = currentTotalGrid;
                shiftTimeGrid = 0;
            }

            foreach (var note in timingPoint.Notes)
            {
                if (!note.IsSlideNoHead)
                {
                    var id = GetMa2NoteId(note);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    notesOutput.Append($"{id}\t{curUnit}\t{curGrid}\t{(note.TouchArea == 'C' ? 0 : note.StartPosition - 1)}");

                    void AppendAsHold()
                    {
                        var endTime = note.HoldTime + curTime;
                        var durationGrids = CalculateGrid(endTime) - currentTotalGrid;
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

                    if (note.SoflanGroup != 0)
                        notesOutput.Append($"\t#{note.SoflanGroup}");

                    notesOutput.AppendLine();
                    AddStat(id);
                }

                if (note.Type != SimaiNoteType.Slide)
                {
                    continue;
                }

                var subSlides = InstantiateStarGroup(timingPoint, note);
                var prevTotalGrid = currentTotalGrid;
                var tmpSubListOutput = new List<(string SlideId, long Grid, long StartPos, long WaitGrid, long DurationGrid, long EndPos)>();

                for (var i = 0; i < subSlides.Count; i++)
                {
                    var subSlide = subSlides[i];
                    if (subSlides.Count == 1)
                    {
                        AddStat(subSlide.IsSlideBreak ? "BSL" : "SLD");
                    }

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

                    var slideId = prefix + Ma2SlideMap[simaiId];
                    var slideTotalGrid = CalculateGrid(subSlide.SlideStartTime);
                    var waitGrid = slideTotalGrid - prevTotalGrid;
                    var durationGrid = CalculateGrid(subSlide.SlideStartTime + subSlide.SlideTime) - slideTotalGrid;

                    if (subSlides.Count > 1)
                    {
                        waitGrid = prefix == "CN" ? 0 : waitGrid - durationGrid;
                    }

                    tmpSubListOutput.Add((slideId, prevTotalGrid, subSlide.StartPosition - 1, waitGrid, durationGrid,
                        endPosition - 1));

                    prevTotalGrid = slideTotalGrid;
                }

                var tmpFixedSubListOutput = new List<(string SlideId, long Grid, long StartPos, long WaitGrid, long DurationGrid, long EndPos)>();
                for (var i = 1; i < tmpSubListOutput.Count; i++)
                {
                    var previous = tmpSubListOutput[i - 1];
                    var current = tmpSubListOutput[i];
                    var duration = current.Grid - (previous.Grid + previous.WaitGrid);
                    tmpFixedSubListOutput.Add((previous.SlideId, previous.Grid, previous.StartPos, previous.WaitGrid,
                        duration, previous.EndPos));
                }

                tmpFixedSubListOutput.Add(tmpSubListOutput.Last());

                foreach (var (slideId, grid, startPos, waitGrid, durationGrid, endPos) in tmpFixedSubListOutput)
                {
                    FormatGrid(grid, out var slideUnit, out var slideGrid);
                    notesOutput.AppendLine($"{slideId}\t{slideUnit}\t{slideGrid}\t{startPos}\t{waitGrid}\t{durationGrid}\t{endPos}");
                }
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

        var numEachPairs = timingPoints.Count(x => x.Notes.Count(note =>
        {
            return note.Type switch
            {
                SimaiNoteType.Tap or SimaiNoteType.Hold or SimaiNoteType.Touch or SimaiNoteType.TouchHold => true,
                _ => false
            };
        }) > 1);
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

        return headerOutput.ToString() + compositeOutput + notesOutput + totalOutput;
    }

    public IReadOnlyList<Ma2ExportResult> ConvertSelectedCharts(
        IEnumerable<Ma2ExportChart> charts,
        string musicId6,
        double shiftTime,
        float? fallbackWholeBpm = null)
    {
        var results = new List<Ma2ExportResult>();
        foreach (var chart in charts)
        {
            var content = ConvertChartToMa2Content(chart.ChartContent, shiftTime, fallbackWholeBpm);
            results.Add(new Ma2ExportResult(chart.DiffId, $"music{musicId6}_{chart.DiffId}.ma2", content));
        }

        return results;
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

    private static string GetMa2NoteId(SimaiNote note)
    {
        return note.Type switch
        {
            SimaiNoteType.Tap => GetNormalBreakExId(note, "TAP"),
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
            if (endPos == 5 || endPos == 1)
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

    private static string FormatBpm(float bpm) => bpm.ToString("G9", CultureInfo.InvariantCulture);
    private static string FormatBpmFixed(float bpm) => bpm.ToString("F3", CultureInfo.InvariantCulture);

    private sealed class SlidePart
    {
        public bool IsBreak { get; set; }
        public bool IsSlideBreak { get; set; }
        public bool IsEx { get; set; }
        public bool IsSlideNoHead { get; set; }
        public int StartPosition { get; set; }
        public double SlideStartTime { get; set; }
        public double SlideTime { get; set; }
        public string RawContent { get; set; } = string.Empty;
    }
}
