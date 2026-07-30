namespace MajdataEdit.Ma2Export;

public sealed class Ma2ExportChart
{
    public Ma2ExportChart(int diffId, string chartContent)
    {
        DiffId = diffId;
        ChartContent = chartContent;
    }

    public int DiffId { get; }
    public string ChartContent { get; }
}

public sealed class Ma2ExportResult
{
    public Ma2ExportResult(int diffId, string fileName, string content, Ma2ConversionReport? report = null)
    {
        DiffId = diffId;
        FileName = fileName;
        Content = content;
        Report = report;
    }

    public int DiffId { get; }
    public string FileName { get; }
    public string Content { get; }
    public Ma2ConversionReport? Report { get; }
}

public sealed class Ma2AdaptiveResolutionOptions
{
    public const int DefaultResolution = 384;
    public const int DefaultMaximumResolution = 192000;

    public int MinimumResolution { get; init; } = DefaultResolution;
    public int MaximumResolution { get; init; } = DefaultMaximumResolution;
    public bool EnableAdaptiveResolution { get; init; } = true;
    public bool EnableMinimumGridRepair { get; init; } = true;
}

public sealed class Ma2ConversionResult
{
    public Ma2ConversionResult(string content, Ma2ConversionReport report)
    {
        Content = content;
        Report = report;
    }

    public string Content { get; }
    public Ma2ConversionReport Report { get; }
}

public sealed class Ma2ConversionReport
{
    internal Ma2ConversionReport(
        int initialResolution,
        int finalResolution,
        int candidateAttempts,
        bool usedMinimumGridRepair,
        IReadOnlyList<Ma2TimingAdjustment> adjustments)
    {
        InitialResolution = initialResolution;
        FinalResolution = finalResolution;
        CandidateAttempts = candidateAttempts;
        UsedMinimumGridRepair = usedMinimumGridRepair;
        Adjustments = adjustments;
    }

    public int InitialResolution { get; }
    public int FinalResolution { get; }
    public int CandidateAttempts { get; }
    public bool UsedMinimumGridRepair { get; }
    public IReadOnlyList<Ma2TimingAdjustment> Adjustments { get; }
    public int AdjustedObjectCount => Adjustments.Select(x => (x.BranchId, x.SegmentIndex)).Distinct().Count();
    public long MaximumGridAdjustment => Adjustments.Count == 0 ? 0 : Adjustments.Max(x => Math.Abs(x.GridDelta));
    public double MaximumMillisecondAdjustment => Adjustments.Count == 0 ? 0 : Adjustments.Max(x => Math.Abs(x.MillisecondDelta));
}

public sealed class Ma2TimingAdjustment
{
    internal Ma2TimingAdjustment(
        string kind,
        int sourceLine,
        string sourceText,
        int branchId,
        int segmentIndex,
        long gridDelta,
        double millisecondDelta)
    {
        Kind = kind;
        SourceLine = sourceLine;
        SourceText = sourceText;
        BranchId = branchId;
        SegmentIndex = segmentIndex;
        GridDelta = gridDelta;
        MillisecondDelta = millisecondDelta;
    }

    public string Kind { get; }
    public int SourceLine { get; }
    public string SourceText { get; }
    public int BranchId { get; }
    public int SegmentIndex { get; }
    public long GridDelta { get; }
    public double MillisecondDelta { get; }
}

public sealed class Ma2DifficultyOption
{
    public Ma2DifficultyOption(int diffId, string difficultyName, string level, string chartContent)
    {
        DiffId = diffId;
        DifficultyName = difficultyName;
        Level = level;
        ChartContent = chartContent;
    }

    public int DiffId { get; }
    public string DifficultyName { get; }
    public string Level { get; }
    public string ChartContent { get; }
    public bool IsSelected { get; set; }
}
