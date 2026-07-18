using System.Globalization;
using System.IO;
using System.Text;
using JacketGenerator;
using JacketGenerator.Base;

namespace MajdataEdit.JacketAbExport;

internal static class JacketAbExporter
{
    public static async Task ExportAsync(
        string sourceImagePath,
        string outputDirectory,
        string finalMusicId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceImagePath))
        {
            throw new FileNotFoundException("当前谱面目录中没有 bg.jpg。", sourceImagePath);
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("导出目录不存在。请选择有效的导出目录。");
        }

        if (finalMusicId.Length != 6 || finalMusicId.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException("FinaleMusicId 必须是六位数字。", nameof(finalMusicId));
        }

        var musicId = int.Parse(finalMusicId, NumberStyles.None, CultureInfo.InvariantCulture);
        var normalFileName = $"ui_jacket_{finalMusicId}.ab";
        var smallFileName = $"ui_jacket_{finalMusicId}_s.ab";
        var normalOutputDirectory = Path.Combine(outputDirectory, "jacket");
        var smallOutputDirectory = Path.Combine(outputDirectory, "jacket_s");
        Directory.CreateDirectory(normalOutputDirectory);
        Directory.CreateDirectory(smallOutputDirectory);

        var stagingDirectory = Path.Combine(
            outputDirectory,
            $".jacket-{finalMusicId}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);
        string? normalGeneratedPath = null;
        string? smallGeneratedPath = null;
        try
        {
            progress?.Report("正在生成 SDEZ 大封面 AssetBundle……");
            normalGeneratedPath = await Task.Run(
                async () => await Jacket.GenerateJacketFileAsync(
                    sourceImagePath,
                    musicId,
                    isSmall: false,
                    GenerateGameType.SDEZ,
                    BuildTarget.StandaloneWindows64),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report("正在生成 SDEZ 小封面 AssetBundle……");
            smallGeneratedPath = await Task.Run(
                async () => await Jacket.GenerateJacketFileAsync(
                    sourceImagePath,
                    musicId,
                    isSmall: true,
                    GenerateGameType.SDEZ,
                    BuildTarget.StandaloneWindows64),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAssetBundle(normalGeneratedPath);
            ValidateAssetBundle(smallGeneratedPath);

            var stagedNormalPath = Path.Combine(stagingDirectory, normalFileName);
            var stagedSmallPath = Path.Combine(stagingDirectory, smallFileName);
            File.Copy(normalGeneratedPath, stagedNormalPath);
            File.Copy(smallGeneratedPath, stagedSmallPath);

            progress?.Report("正在写入封面 AssetBundle……");
            ReplaceGeneratedPair(
                stagedNormalPath,
                stagedSmallPath,
                Path.Combine(normalOutputDirectory, normalFileName),
                Path.Combine(smallOutputDirectory, smallFileName));
            progress?.Report("生成完成。");
        }
        finally
        {
            TryDeleteFile(normalGeneratedPath);
            TryDeleteFile(smallGeneratedPath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void ValidateAssetBundle(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("JacketGenerator 没有产出 AssetBundle 文件。");
        }

        var signature = new byte[7];
        var expectedSignature = Encoding.ASCII.GetBytes("UnityFS");
        using var stream = File.OpenRead(path);
        if (stream.Read(signature, 0, signature.Length) != signature.Length ||
            !signature.SequenceEqual(expectedSignature))
        {
            throw new InvalidDataException("JacketGenerator 产出的文件不是有效的 UnityFS AssetBundle。");
        }
    }

    private static void ReplaceGeneratedPair(
        string generatedNormalPath,
        string generatedSmallPath,
        string targetNormalPath,
        string targetSmallPath)
    {
        var backupSuffix = $".{Guid.NewGuid():N}.bak";
        var backupNormalPath = targetNormalPath + backupSuffix;
        var backupSmallPath = targetSmallPath + backupSuffix;
        var normalBackedUp = false;
        var smallBackedUp = false;
        var normalMoved = false;
        var smallMoved = false;
        var replacementCompleted = false;
        try
        {
            if (File.Exists(targetNormalPath))
            {
                File.Move(targetNormalPath, backupNormalPath);
                normalBackedUp = true;
            }

            if (File.Exists(targetSmallPath))
            {
                File.Move(targetSmallPath, backupSmallPath);
                smallBackedUp = true;
            }

            File.Move(generatedNormalPath, targetNormalPath);
            normalMoved = true;
            File.Move(generatedSmallPath, targetSmallPath);
            smallMoved = true;
            replacementCompleted = true;
        }
        catch
        {
            if (normalMoved && File.Exists(targetNormalPath))
            {
                File.Delete(targetNormalPath);
            }

            if (smallMoved && File.Exists(targetSmallPath))
            {
                File.Delete(targetSmallPath);
            }

            if (normalBackedUp && File.Exists(backupNormalPath))
            {
                File.Move(backupNormalPath, targetNormalPath);
            }

            if (smallBackedUp && File.Exists(backupSmallPath))
            {
                File.Move(backupSmallPath, targetSmallPath);
            }

            throw;
        }
        finally
        {
            if (replacementCompleted)
            {
                TryDeleteFile(backupNormalPath);
                TryDeleteFile(backupSmallPath);
            }
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时或备份文件清理失败不应覆盖真正的生成结果或错误。
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
