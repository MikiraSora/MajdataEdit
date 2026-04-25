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
    public Ma2ExportResult(int diffId, string fileName, string content)
    {
        DiffId = diffId;
        FileName = fileName;
        Content = content;
    }

    public int DiffId { get; }
    public string FileName { get; }
    public string Content { get; }
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
