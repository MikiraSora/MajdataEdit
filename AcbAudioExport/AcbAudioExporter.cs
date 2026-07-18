using System.IO;
using AcbGeneratorFuck;
using VGAudio.Cli;

namespace MajdataEdit.AcbAudioExport;

internal static class AcbAudioExporter
{
    public const int HcaBitrate = 192 * 1024;
    public const ulong SdezKeyCode = 9_170_825_592_834_449_000;

    public static async Task ExportAsync(
        string sourceAudioPath,
        string outputDirectory,
        string finalMusicId,
        int previewStartMilliseconds,
        int previewEndMilliseconds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceAudioPath))
        {
            throw new FileNotFoundException("当前谱面目录中没有 track.mp3。", sourceAudioPath);
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("导出目录不存在。请选择有效的导出目录。");
        }

        if (finalMusicId.Length != 6 || finalMusicId.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException("FinaleMusicId 必须是六位数字。", nameof(finalMusicId));
        }

        if (previewStartMilliseconds < 0 || previewEndMilliseconds <= previewStartMilliseconds)
        {
            throw new ArgumentException("音频预览中止位置必须大于起始位置，且二者不能小于 0。");
        }

        var outputPrefix = "music" + finalMusicId;
        var temporaryDirectory = Path.Combine(
            outputDirectory,
            $".{outputPrefix}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            progress?.Report("正在转换 MP3 并生成 SDEZ ACB/AWB……");
            var options = new Options
            {
                Bitrate = HcaBitrate,
                KeyCode = SdezKeyCode
            };
            var generated = await Task.Run(
                () => Generator.Generate(
                    sourceAudioPath,
                    outputPrefix,
                    temporaryDirectory,
                    audioNormalization: true,
                    option: options,
                    appendOffset: 0,
                    thread: 0,
                    isSDEZ: true,
                    specifyACBFileBytes: default,
                    previewBeginTime: TimeSpan.FromMilliseconds(previewStartMilliseconds),
                    previewEndTime: TimeSpan.FromMilliseconds(previewEndMilliseconds)),
                cancellationToken);
            if (!generated)
            {
                throw new InvalidOperationException("AcbGeneratorFuck 未能生成 SDEZ ACB/AWB 文件。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var temporaryAcbPath = Path.Combine(temporaryDirectory, outputPrefix + ".acb");
            var temporaryAwbPath = Path.Combine(temporaryDirectory, outputPrefix + ".awb");
            if (!File.Exists(temporaryAcbPath) || !File.Exists(temporaryAwbPath))
            {
                throw new InvalidDataException("生成器没有产出完整的 ACB/AWB 文件对。");
            }

            progress?.Report("正在写入 ACB/AWB 文件……");
            ReplaceGeneratedPair(
                temporaryAcbPath,
                temporaryAwbPath,
                Path.Combine(outputDirectory, outputPrefix + ".acb"),
                Path.Combine(outputDirectory, outputPrefix + ".awb"));
            progress?.Report("生成完成。");
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static void ReplaceGeneratedPair(
        string generatedAcbPath,
        string generatedAwbPath,
        string targetAcbPath,
        string targetAwbPath)
    {
        var backupSuffix = $".{Guid.NewGuid():N}.bak";
        var backupAcbPath = targetAcbPath + backupSuffix;
        var backupAwbPath = targetAwbPath + backupSuffix;
        var acbBackedUp = false;
        var awbBackedUp = false;
        var acbMoved = false;
        var awbMoved = false;
        var replacementCompleted = false;
        try
        {
            if (File.Exists(targetAcbPath))
            {
                File.Move(targetAcbPath, backupAcbPath);
                acbBackedUp = true;
            }

            if (File.Exists(targetAwbPath))
            {
                File.Move(targetAwbPath, backupAwbPath);
                awbBackedUp = true;
            }

            File.Move(generatedAcbPath, targetAcbPath);
            acbMoved = true;
            File.Move(generatedAwbPath, targetAwbPath);
            awbMoved = true;
            replacementCompleted = true;
        }
        catch
        {
            if (acbMoved && File.Exists(targetAcbPath))
            {
                File.Delete(targetAcbPath);
            }

            if (awbMoved && File.Exists(targetAwbPath))
            {
                File.Delete(targetAwbPath);
            }

            if (acbBackedUp && File.Exists(backupAcbPath))
            {
                File.Move(backupAcbPath, targetAcbPath);
            }

            if (awbBackedUp && File.Exists(backupAwbPath))
            {
                File.Move(backupAwbPath, targetAwbPath);
            }

            throw;
        }
        finally
        {
            if (replacementCompleted)
            {
                TryDeleteFile(backupAcbPath);
                TryDeleteFile(backupAwbPath);
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
            // 备份文件清理失败不应覆盖真正的生成结果或错误。
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // 临时目录清理失败不应覆盖真正的生成结果或错误。
        }
    }
}
