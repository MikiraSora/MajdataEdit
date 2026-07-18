using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MajdataEdit.Ma2Export;

namespace MajdataEdit.MusicXmlExport;

internal static class MusicXmlExporter
{
    private const int DifficultyCount = 6;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static Task<MusicXmlExportResult> ExportAsync(
        MusicXmlExportRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Export(request, progress, cancellationToken), cancellationToken);
    }

    public static void ValidateLevelConstant(string value)
    {
        _ = ParseLevel(value);
    }

    private static MusicXmlExportResult Export(
        MusicXmlExportRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var targetDirectory = Path.Combine(
            request.OutputDirectory,
            "music",
            "music" + request.FinalMusicId);
        var stagingDirectory = Path.Combine(
            request.OutputDirectory,
            $".musicxml-{request.FinalMusicId}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            progress?.Report("正在转换启用的 simai 谱面……");
            var converter = new SimaiChartConverter();
            var generatedCharts = new Dictionary<int, (string FileName, string Content, int MaxNotes)>();
            foreach (var difficulty in request.Difficulties.Where(x => x.IsEnabled).OrderBy(x => x.SlotIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = converter.ConvertChartToMa2Content(
                    difficulty.ChartContent,
                    request.Bpm,
                    request.HSpeedInterpolationGrid);
                var fileName = $"{request.FinalMusicId}_{difficulty.SlotIndex:00}.ma2";
                generatedCharts.Add(difficulty.SlotIndex, (fileName, content, GetMaxNotes(content)));
                File.WriteAllText(Path.Combine(stagingDirectory, fileName), content, Utf8NoBom);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在基于模板生成 Music.xml……");
            var document = XDocument.Load(request.TemplatePath, LoadOptions.SetLineInfo);
            UpdateDocument(document, request, generatedCharts);
            var stagedMusicXmlPath = Path.Combine(stagingDirectory, "Music.xml");
            SaveDocument(document, stagedMusicXmlPath);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在写入 Music.xml 与 MA2 文件……");
            Directory.CreateDirectory(targetDirectory);
            var stagedFiles = new List<(string StagedPath, string TargetPath)>
            {
                (stagedMusicXmlPath, Path.Combine(targetDirectory, "Music.xml"))
            };
            stagedFiles.AddRange(generatedCharts.Values.Select(chart =>
                (Path.Combine(stagingDirectory, chart.FileName), Path.Combine(targetDirectory, chart.FileName))));
            ReplaceFiles(stagedFiles);

            var ma2Paths = generatedCharts.Values
                .Select(chart => Path.Combine(targetDirectory, chart.FileName))
                .ToArray();
            progress?.Report("生成完成。");
            return new MusicXmlExportResult(
                targetDirectory,
                Path.Combine(targetDirectory, "Music.xml"),
                ma2Paths);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void ValidateRequest(MusicXmlExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Directory.Exists(request.OutputDirectory))
        {
            throw new DirectoryNotFoundException("导出目录不存在。请选择有效的导出目录。");
        }

        if (!File.Exists(request.TemplatePath))
        {
            throw new FileNotFoundException("基础 Music.xml 模板不存在。", request.TemplatePath);
        }

        if (!string.Equals(Path.GetFileName(request.TemplatePath), "Music.xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("基础模板必须是名为 Music.xml 的文件。", nameof(request.TemplatePath));
        }

        if (request.BaseMusicId.Length != 4 || request.BaseMusicId.Any(character => !char.IsDigit(character)))
        {
            throw new ArgumentException("BaseMusicId 必须是四位数字。", nameof(request.BaseMusicId));
        }

        if (request.FinalMusicId.Length != 6 || request.FinalMusicId.Any(character => !char.IsDigit(character)) ||
            !request.FinalMusicId.EndsWith(request.BaseMusicId, StringComparison.Ordinal))
        {
            throw new ArgumentException("FinaleMusicId 必须是以 BaseMusicId 结尾的六位数字。", nameof(request.FinalMusicId));
        }

        if (string.IsNullOrWhiteSpace(request.MusicName))
        {
            throw new ArgumentException("歌曲名不能为空。", nameof(request.MusicName));
        }

        if (string.IsNullOrWhiteSpace(request.ArtistName))
        {
            throw new ArgumentException("艺术家名不能为空。", nameof(request.ArtistName));
        }

        if (request.GenreId <= 0 || string.IsNullOrWhiteSpace(request.GenreName))
        {
            throw new ArgumentException("请选择有效的歌曲分类。", nameof(request.GenreName));
        }

        if (request.Bpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Bpm), "BPM 必须是正整数。");
        }

        if (request.Difficulties.Any(x => x.SlotIndex is < 0 or >= DifficultyCount) ||
            request.Difficulties.GroupBy(x => x.SlotIndex).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("难度槽位必须是 00 到 05，且不能重复。", nameof(request.Difficulties));
        }

        foreach (var difficulty in request.Difficulties.Where(x => x.IsEnabled))
        {
            _ = ParseLevel(difficulty.Level);
            if (string.IsNullOrWhiteSpace(difficulty.ChartContent))
            {
                throw new ArgumentException($"难度 {difficulty.SlotIndex:00} 选择的 inote 谱面为空。");
            }
        }
    }

    private static void UpdateDocument(
        XDocument document,
        MusicXmlExportRequest request,
        IReadOnlyDictionary<int, (string FileName, string Content, int MaxNotes)> generatedCharts)
    {
        var root = document.Root ?? throw new InvalidDataException("Music.xml 模板没有根元素。");
        if (!string.Equals(root.Name.LocalName, "MusicData", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Music.xml 模板根元素必须是 MusicData。");
        }

        SetRequiredValue(root, "dataName", "music" + request.FinalMusicId);
        SetRequiredValue(root, "longMusic", request.LongMusic ? "1" : "0");
        SetRequiredValue(RequiredChild(root, "name"), "id", request.FinalMusicId);
        SetRequiredValue(RequiredChild(root, "name"), "str", request.MusicName);
        SetRequiredValue(RequiredChild(root, "artistName"), "str", request.ArtistName);
        SetRequiredValue(RequiredChild(root, "genreName"), "id",
            request.GenreId.ToString(CultureInfo.InvariantCulture));
        SetRequiredValue(RequiredChild(root, "genreName"), "str", request.GenreName);
        SetRequiredValue(root, "bpm", request.Bpm.ToString(CultureInfo.InvariantCulture));
        SetRequiredValue(RequiredChild(root, "movieName"), "id", request.BaseMusicId);
        SetRequiredValue(RequiredChild(root, "cueName"), "id", request.BaseMusicId);
        SetRequiredValue(root, "utageKanjiName", request.UtageKanjiName);
        SetRequiredValue(root, "comment", request.Comment);

        var notesData = RequiredChild(root, "notesData");
        var notes = notesData.Elements().Where(element => element.Name.LocalName == "Notes").ToList();
        while (notes.Count < DifficultyCount)
        {
            var note = CreateNotesElement(notesData.Name.Namespace);
            notesData.Add(note);
            notes.Add(note);
        }

        var difficultyMap = request.Difficulties.ToDictionary(x => x.SlotIndex);
        for (var slotIndex = 0; slotIndex < DifficultyCount; slotIndex++)
        {
            difficultyMap.TryGetValue(slotIndex, out var difficulty);
            var isEnabled = difficulty?.IsEnabled == true;
            var note = notes[slotIndex];
            SetRequiredValue(RequiredChild(RequiredChild(note, "file"), "path"),
                request.FinalMusicId + $"_{slotIndex:00}.ma2");

            var parsedLevel = isEnabled ? ParseLevel(difficulty!.Level) : (Level: 0, Decimal: 0, MusicLevelId: 0);
            SetRequiredValue(note, "level", parsedLevel.Level.ToString(CultureInfo.InvariantCulture));
            SetRequiredValue(note, "levelDecimal", parsedLevel.Decimal.ToString(CultureInfo.InvariantCulture));

            var notesDesigner = RequiredChild(note, "notesDesigner");
            SetRequiredValue(notesDesigner, "id", "0");
            SetRequiredValue(notesDesigner, "str", isEnabled ? difficulty!.Designer : string.Empty);
            SetRequiredValue(note, "notesType", "0");
            SetRequiredValue(note, "musicLevelID", parsedLevel.MusicLevelId.ToString(CultureInfo.InvariantCulture));
            SetRequiredValue(note, "maxNotes",
                isEnabled
                    ? generatedCharts[slotIndex].MaxNotes.ToString(CultureInfo.InvariantCulture)
                    : "0");
            SetRequiredValue(note, "isEnable", isEnabled ? "true" : "false");
        }
    }

    private static (int Level, int Decimal, int MusicLevelId) ParseLevel(string value)
    {
        if (!decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var constant) || constant < 1m || constant >= 16m)
        {
            throw new ArgumentException("难度定数必须是 1.00 到 15.90 之间的数字。");
        }

        var tenths = constant * 10m;
        if (tenths != decimal.Truncate(tenths))
        {
            throw new ArgumentException("SDEZ 难度定数只支持一位有效小数；可写成 14.60，但不能写 14.65。");
        }

        var level = decimal.ToInt32(decimal.Truncate(constant));
        var levelDecimal = decimal.ToInt32(tenths) - level * 10;
        var musicLevelId = level <= 6
            ? level
            : level * 2 - 7 + (levelDecimal >= 6 ? 1 : 0);
        return (level, levelDecimal, musicLevelId);
    }

    private static int GetMaxNotes(string ma2Content)
    {
        foreach (var line in ma2Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "T_REC_ALL\t";
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(line[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return count;
            }
        }

        throw new InvalidDataException("生成的 MA2 中缺少 T_REC_ALL 汇总字段。");
    }

    private static XElement RequiredChild(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)
               ?? throw new InvalidDataException($"Music.xml 模板缺少 {parent.Name.LocalName}/{localName} 节点。");
    }

    private static void SetRequiredValue(XElement parent, string localName, string value)
    {
        RequiredChild(parent, localName).Value = value;
    }

    private static void SetRequiredValue(XElement element, string value)
    {
        element.Value = value;
    }

    private static XElement CreateNotesElement(XNamespace xmlNamespace)
    {
        return new XElement(xmlNamespace + "Notes",
            new XElement(xmlNamespace + "file", new XElement(xmlNamespace + "path", string.Empty)),
            new XElement(xmlNamespace + "level", 0),
            new XElement(xmlNamespace + "levelDecimal", 0),
            new XElement(xmlNamespace + "notesDesigner",
                new XElement(xmlNamespace + "id", 0),
                new XElement(xmlNamespace + "str", string.Empty)),
            new XElement(xmlNamespace + "notesType", 0),
            new XElement(xmlNamespace + "musicLevelID", 0),
            new XElement(xmlNamespace + "maxNotes", 0),
            new XElement(xmlNamespace + "isEnable", false));
    }

    private static void SaveDocument(XDocument document, string path)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false
        };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static void ReplaceFiles(IReadOnlyList<(string StagedPath, string TargetPath)> files)
    {
        var backupSuffix = $".{Guid.NewGuid():N}.bak";
        var backups = new List<(string BackupPath, string TargetPath)>();
        var movedTargets = new List<string>();
        var completed = false;
        try
        {
            foreach (var (_, targetPath) in files)
            {
                if (!File.Exists(targetPath))
                {
                    continue;
                }

                var backupPath = targetPath + backupSuffix;
                File.Move(targetPath, backupPath);
                backups.Add((backupPath, targetPath));
            }

            foreach (var (stagedPath, targetPath) in files)
            {
                File.Move(stagedPath, targetPath);
                movedTargets.Add(targetPath);
            }

            completed = true;
        }
        catch
        {
            foreach (var targetPath in movedTargets)
            {
                TryDeleteFile(targetPath);
            }

            foreach (var (backupPath, targetPath) in backups)
            {
                if (File.Exists(backupPath))
                {
                    File.Move(backupPath, targetPath);
                }
            }

            throw;
        }
        finally
        {
            if (completed)
            {
                foreach (var (backupPath, _) in backups)
                {
                    TryDeleteFile(backupPath);
                }
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时或备份文件清理失败不应覆盖真正的生成结果或错误。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 临时目录清理失败不应覆盖真正的生成结果或错误。
        }
    }
}
