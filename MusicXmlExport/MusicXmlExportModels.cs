using MajdataEdit.Ma2Export;

namespace MajdataEdit.MusicXmlExport;

internal sealed class MusicXmlDifficultyExport
{
    public int SlotIndex { get; init; }

    public bool IsEnabled { get; init; }

    public string Level { get; init; } = string.Empty;

    public string Designer { get; init; } = string.Empty;

    public string ChartContent { get; init; } = string.Empty;
}

internal sealed class MusicXmlExportRequest
{
    public string OutputDirectory { get; init; } = string.Empty;

    public string TemplatePath { get; init; } = string.Empty;

    public string BaseMusicId { get; init; } = string.Empty;

    public string FinalMusicId { get; init; } = string.Empty;

    public string MusicName { get; init; } = string.Empty;

    public string ArtistName { get; init; } = string.Empty;

    public int GenreId { get; init; }

    public string GenreName { get; init; } = string.Empty;

    public int Bpm { get; init; }

    public bool LongMusic { get; init; }

    public string UtageKanjiName { get; init; } = string.Empty;

    public string Comment { get; init; } = string.Empty;

    public int HSpeedInterpolationGrid { get; init; } = 32;

    public IReadOnlyList<MusicXmlDifficultyExport> Difficulties { get; init; } =
        Array.Empty<MusicXmlDifficultyExport>();
}

internal sealed class MusicXmlExportResult
{
    public MusicXmlExportResult(
        string targetDirectory,
        string musicXmlPath,
        IReadOnlyList<string> ma2Paths,
        IReadOnlyList<Ma2ConversionReport> conversionReports)
    {
        TargetDirectory = targetDirectory;
        MusicXmlPath = musicXmlPath;
        Ma2Paths = ma2Paths;
        ConversionReports = conversionReports;
    }

    public string TargetDirectory { get; }

    public string MusicXmlPath { get; }

    public IReadOnlyList<string> Ma2Paths { get; }

    public IReadOnlyList<Ma2ConversionReport> ConversionReports { get; }
}
