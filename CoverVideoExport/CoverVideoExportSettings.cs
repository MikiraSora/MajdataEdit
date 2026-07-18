using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace MajdataEdit.CoverVideoExport;

internal sealed class CoverVideoExportSettings
{
    public string BaseMusicId { get; set; } = string.Empty;

    public bool IsUtage { get; set; }

    public bool IsDx { get; set; }
}

internal static class CoverVideoExportSettingsStore
{
    public const string FileName = "export.json";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static CoverVideoExportSettings Load(string chartDirectory)
    {
        var path = GetPath(chartDirectory);
        if (!File.Exists(path))
        {
            return new CoverVideoExportSettings();
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        var settings = JsonConvert.DeserializeObject<CoverVideoExportSettings>(json)
                       ?? throw new InvalidDataException("export.json 内容为空。");
        if (settings.BaseMusicId == null)
        {
            throw new InvalidDataException("export.json 中缺少 BaseMusicId。");
        }

        if (settings.BaseMusicId.Length != 0 &&
            (settings.BaseMusicId.Length != 4 ||
             settings.BaseMusicId.Any(character => character is < '0' or > '9')))
        {
            throw new InvalidDataException("export.json 中的 BaseMusicId 必须是四位数字。");
        }

        return settings;
    }

    public static void Save(string chartDirectory, CoverVideoExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = CoverVideoExporter.BuildFinalMusicId(settings.BaseMusicId, settings.IsUtage, settings.IsDx);

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
}
