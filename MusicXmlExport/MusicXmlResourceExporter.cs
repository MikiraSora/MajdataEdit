using System.IO;
using MajdataEdit.AcbAudioExport;
using MajdataEdit.CoverVideoExport;
using MajdataEdit.Export;
using MajdataEdit.JacketAbExport;

namespace MajdataEdit.MusicXmlExport;

internal static class MusicXmlResourceExporter
{
    public static IReadOnlyList<string> GetOutputPaths(
        string outputDirectory,
        string baseMusicId,
        bool generateCoverVideo,
        bool generateAcbAwb,
        bool generateJacketAb)
    {
        var musicId = ExportMusicId.BuildFinalMusicId(baseMusicId, isUtage: false, isDx: false);
        var paths = new List<string>();
        if (generateCoverVideo)
        {
            paths.Add(Path.Combine(outputDirectory, "MovieData", musicId + ".dat"));
        }

        if (generateAcbAwb)
        {
            paths.Add(Path.Combine(outputDirectory, "SoundData", "music" + musicId + ".acb"));
            paths.Add(Path.Combine(outputDirectory, "SoundData", "music" + musicId + ".awb"));
        }

        if (generateJacketAb)
        {
            paths.Add(Path.Combine(outputDirectory, "AssetBundleImages", "jacket", $"ui_jacket_{musicId}.ab"));
            paths.Add(Path.Combine(outputDirectory, "AssetBundleImages", "jacket_s", $"ui_jacket_{musicId}_s.ab"));
        }

        return paths;
    }

    public static async Task<IReadOnlyList<string>> ExportAsync(
        string chartDirectory,
        string outputDirectory,
        string baseMusicId,
        int previewStartMilliseconds,
        int previewEndMilliseconds,
        bool generateCoverVideo,
        bool generateAcbAwb,
        bool generateJacketAb,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outputPaths = GetOutputPaths(
            outputDirectory,
            baseMusicId,
            generateCoverVideo,
            generateAcbAwb,
            generateJacketAb);
        if (outputPaths.Count == 0)
        {
            return Array.Empty<string>();
        }

        var musicId = ExportMusicId.BuildFinalMusicId(baseMusicId, isUtage: false, isDx: false);
        var sourceImagePath = Path.Combine(chartDirectory, "bg.jpg");
        var sourceAudioPath = Path.Combine(chartDirectory, "track.mp3");
        if ((generateCoverVideo || generateJacketAb) && !File.Exists(sourceImagePath))
        {
            throw new FileNotFoundException("当前谱面目录中没有 bg.jpg。", sourceImagePath);
        }

        if (generateAcbAwb && !File.Exists(sourceAudioPath))
        {
            throw new FileNotFoundException("当前谱面目录中没有 track.mp3。", sourceAudioPath);
        }

        if (generateCoverVideo)
        {
            var movieDataDirectory = Path.Combine(outputDirectory, "MovieData");
            Directory.CreateDirectory(movieDataDirectory);
            progress?.Report("正在生成封面 USM 视频……");
            await CoverVideoExporter.ExportAsync(
                sourceImagePath,
                Path.Combine(movieDataDirectory, musicId + ".dat"),
                progress,
                cancellationToken);
        }

        if (generateAcbAwb)
        {
            var soundDataDirectory = Path.Combine(outputDirectory, "SoundData");
            Directory.CreateDirectory(soundDataDirectory);
            progress?.Report("正在生成 ACB/AWB 音频……");
            await AcbAudioExporter.ExportAsync(
                sourceAudioPath,
                soundDataDirectory,
                musicId,
                previewStartMilliseconds,
                previewEndMilliseconds,
                progress,
                cancellationToken);
        }

        if (generateJacketAb)
        {
            var assetBundleImagesDirectory = Path.Combine(outputDirectory, "AssetBundleImages");
            Directory.CreateDirectory(assetBundleImagesDirectory);
            progress?.Report("正在生成封面 AssetBundle……");
            await JacketAbExporter.ExportAsync(
                sourceImagePath,
                assetBundleImagesDirectory,
                musicId,
                progress,
                cancellationToken);
        }

        return outputPaths;
    }
}
