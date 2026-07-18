using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace MajdataEdit.Export;

internal sealed class ExportSettings
{
    public string BaseMusicId { get; set; } = string.Empty;

    public bool IsUtage { get; set; }

    public bool IsDx { get; set; }

    public int AudioPreviewStartMilliseconds { get; set; } = 60_000;

    public int AudioPreviewEndMilliseconds { get; set; } = 80_000;

    public MusicXmlExportSettings? MusicXml { get; set; }
}

internal sealed class MusicXmlExportSettings
{
    public string OutputDirectory { get; set; } = string.Empty;

    public string TemplatePath { get; set; } = string.Empty;

    public string? MusicName { get; set; }

    public string? ArtistName { get; set; }

    public int GenreId { get; set; } = 105;

    public string GenreName { get; set; } = "maimai";

    public string? Bpm { get; set; }

    public bool LongMusic { get; set; }

    public string UtageKanjiName { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    public bool GenerateCoverVideo { get; set; }

    public bool GenerateAcbAwb { get; set; }

    public bool GenerateJacketAb { get; set; }

    public List<MusicXmlDifficultySettings> Difficulties { get; set; } = new();
}

internal sealed class MusicXmlDifficultySettings
{
    public int SlotIndex { get; set; }

    public bool IsEnabled { get; set; }

    public int InoteIndex { get; set; }

    public string Level { get; set; } = string.Empty;

    public string Designer { get; set; } = string.Empty;
}

internal static class ExportMusicId
{
    public static string BuildFinalMusicId(string baseMusicId, bool isUtage, bool isDx)
    {
        if (baseMusicId.Length != 4 || baseMusicId.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException("BaseMusicId 必须是四位数字。", nameof(baseMusicId));
        }

        return $"{(isUtage ? '1' : '0')}{(isDx ? '1' : '0')}{baseMusicId}";
    }
}

internal static class ExportSettingsStore
{
    public const string FileName = "export.json";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static ExportSettings Load(string chartDirectory)
    {
        var path = GetPath(chartDirectory);
        if (!File.Exists(path))
        {
            return new ExportSettings();
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var settings = JsonConvert.DeserializeObject<ExportSettings>(json)
                       ?? throw new InvalidDataException("export.json 内容为空。");
        Validate(settings, allowEmptyBaseMusicId: true);
        return settings;
    }

    public static void Save(string chartDirectory, ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings, allowEmptyBaseMusicId: false);

        var path = GetPath(chartDirectory);
        var temporaryPath = Path.Combine(
            chartDirectory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented) + Environment.NewLine;
        try
        {
            File.WriteAllText(temporaryPath, json, Utf8NoBom);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 临时文件清理失败不应覆盖真正的保存结果或错误。
            }
        }
    }

    public static string GetPath(string chartDirectory)
    {
        if (string.IsNullOrWhiteSpace(chartDirectory) || !Directory.Exists(chartDirectory))
        {
            throw new DirectoryNotFoundException("当前谱面目录不存在。");
        }

        return Path.Combine(chartDirectory, FileName);
    }

    private static void Validate(ExportSettings settings, bool allowEmptyBaseMusicId)
    {
        if (settings.BaseMusicId == null)
        {
            throw new InvalidDataException("export.json 中缺少 BaseMusicId。");
        }

        if (!allowEmptyBaseMusicId || settings.BaseMusicId.Length != 0)
        {
            _ = ExportMusicId.BuildFinalMusicId(settings.BaseMusicId, settings.IsUtage, settings.IsDx);
        }

        if (settings.AudioPreviewStartMilliseconds < 0)
        {
            throw new InvalidDataException("音频预览起始位置不能小于 0 毫秒。");
        }

        if (settings.AudioPreviewEndMilliseconds <= settings.AudioPreviewStartMilliseconds)
        {
            throw new InvalidDataException("音频预览中止位置必须大于起始位置。");
        }
    }
}
