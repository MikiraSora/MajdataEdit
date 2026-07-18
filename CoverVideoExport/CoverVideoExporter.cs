using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace MajdataEdit.CoverVideoExport;

internal static class CoverVideoExporter
{
    public const ulong EncryptionKey = 0x7F4551499DF55E68;

    public static async Task ExportAsync(
        string sourceImagePath,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceImagePath))
        {
            throw new FileNotFoundException("当前谱面目录中没有 bg.jpg。", sourceImagePath);
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("导出目录不存在。请选择有效的导出目录。");
        }

        var ffmpegPath = FindFfmpegPath();
        var (width, height) = ReadEvenImageDimensions(sourceImagePath);
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "MajdataEdit",
            "CoverVideo",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        var h264Path = Path.Combine(temporaryDirectory, "cover.h264");
        var temporaryOutputPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            progress?.Report("正在编码静态封面……");
            await EncodeSingleFrameAsync(
                ffmpegPath,
                sourceImagePath,
                h264Path,
                width,
                height,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在封装并加密 USM……");
            var h264Frame = await File.ReadAllBytesAsync(h264Path, cancellationToken);
            var usm = await Task.Run(
                () => CriUsmWriter.CreateSingleFrame(h264Frame, width, height, EncryptionKey),
                cancellationToken);
            if (usm.Length < 4 || usm[0] != (byte)'C' || usm[1] != (byte)'R' ||
                usm[2] != (byte)'I' || usm[3] != (byte)'D')
            {
                throw new InvalidDataException("生成的文件不是有效的 CRID/USM 容器。");
            }

            await File.WriteAllBytesAsync(temporaryOutputPath, usm, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutputPath, outputPath, true);
            progress?.Report("生成完成。");
        }
        finally
        {
            TryDeleteFile(temporaryOutputPath);
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task EncodeSingleFrameAsync(
        string ffmpegPath,
        string sourceImagePath,
        string h264Path,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory
        };

        AddArguments(
            startInfo,
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", sourceImagePath,
            "-frames:v", "1",
            "-an",
            "-vf", $"scale={width}:{height}:flags=lanczos,setsar=1",
            "-c:v", "libx264",
            "-preset", "medium",
            "-tune", "stillimage",
            "-profile:v", "high",
            "-level:v", "4.1",
            "-pix_fmt", "yuv420p",
            "-r", "1",
            "-x264-params", "keyint=1:min-keyint=1:scenecut=0",
            "-f", "h264",
            h264Path);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 FFmpeg。");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0 || !File.Exists(h264Path))
        {
            var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException(
                "FFmpeg 无法将 bg.jpg 编码为 H.264。" +
                (string.IsNullOrWhiteSpace(details) ? string.Empty : "\n" + details.Trim()));
        }
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static (int Width, int Height) ReadEvenImageDimensions(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
                    ?? throw new InvalidDataException("bg.jpg 不包含可读取的图像帧。");
        var width = Math.Max(2, frame.PixelWidth - frame.PixelWidth % 2);
        var height = Math.Max(2, frame.PixelHeight - frame.PixelHeight % 2);
        return (width, height);
    }

    private static string FindFfmpegPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "MajdataView_Data", "StreamingAssets", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        };

        var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnvironment))
        {
            candidates.AddRange(pathEnvironment
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.Combine(path.Trim().Trim('"'), "ffmpeg.exe")));
        }

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException(
                   "找不到 FFmpeg。请确认 MajdataView_Data\\StreamingAssets\\ffmpeg.exe 随编辑器一起发布。");
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
            // 临时目录清理失败不应覆盖真正的导出结果或错误。
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
            // 临时文件清理失败不应覆盖真正的导出结果或错误。
        }
    }
}
