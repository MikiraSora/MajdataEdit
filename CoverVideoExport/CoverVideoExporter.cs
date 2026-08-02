using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace MajdataEdit.CoverVideoExport;

internal static class CoverVideoExporter
{
    public const ulong EncryptionKey = 0x7F4551499DF55E68;

    private const int VideoFrameRate = 60;
    private const int MaximumVideoWidth = 1920;
    private const int MaximumVideoHeight = 1080;
    private const int CoverVideoSize = 1080;

    public static string? FindSourcePath(string chartDirectory)
    {
        var videoPath = Path.Combine(chartDirectory, "pv.mp4");
        if (File.Exists(videoPath))
        {
            return videoPath;
        }

        var imagePath = Path.Combine(chartDirectory, "bg.jpg");
        return File.Exists(imagePath) ? imagePath : null;
    }

    public static string ResolveSourcePath(string chartDirectory)
    {
        return FindSourcePath(chartDirectory)
               ?? throw new FileNotFoundException(
                   "当前谱面目录中既没有 pv.mp4，也没有 bg.jpg。",
                   chartDirectory);
    }

    public static async Task ExportAsync(
        string sourceMediaPath,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceMediaPath))
        {
            throw new FileNotFoundException("USM 视频或封面来源不存在。", sourceMediaPath);
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("导出目录不存在。请选择有效的导出目录。");
        }

        var isVideoSource = string.Equals(
            Path.GetExtension(sourceMediaPath),
            ".mp4",
            StringComparison.OrdinalIgnoreCase);
        var ffmpegPath = FindFfmpegPath();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "MajdataEdit",
            "CoverVideo",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        var h264Path = Path.Combine(temporaryDirectory, "cover.h264");
        var previewFramePath = Path.Combine(temporaryDirectory, "preview.png");
        var temporaryOutputPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            int width;
            int height;
            int frameRate;
            if (isVideoSource)
            {
                progress?.Report("正在读取 pv.mp4 视频信息……");
                await ExtractFirstVideoFrameAsync(
                    ffmpegPath,
                    sourceMediaPath,
                    previewFramePath,
                    cancellationToken);
                (width, height) = FitVideoDimensions(ReadEvenImageDimensions(previewFramePath));
                frameRate = VideoFrameRate;

                progress?.Report("正在将 pv.mp4 编码为 H.264……");
                await EncodeVideoAsync(
                    ffmpegPath,
                    sourceMediaPath,
                    h264Path,
                    width,
                    height,
                    frameRate,
                    cancellationToken);
            }
            else
            {
                _ = ReadEvenImageDimensions(sourceMediaPath);
                width = CoverVideoSize;
                height = CoverVideoSize;
                frameRate = 1;

                progress?.Report("正在编码 1 秒静态封面……");
                await EncodeSingleFrameAsync(
                    ffmpegPath,
                    sourceMediaPath,
                    h264Path,
                    width,
                    height,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var frames = H264AnnexBParser.Parse(h264Path);
            progress?.Report($"正在封装并加密 USM（{frames.Count} 帧）……");
            await Task.Run(
                () => CriUsmWriter.WriteVideo(
                    h264Path,
                    temporaryOutputPath,
                    frames,
                    width,
                    height,
                    frameRate,
                    EncryptionKey,
                    cancellationToken),
                cancellationToken);

            ValidateUsmFile(temporaryOutputPath);
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

    private static async Task ExtractFirstVideoFrameAsync(
        string ffmpegPath,
        string sourceVideoPath,
        string previewFramePath,
        CancellationToken cancellationToken)
    {
        await RunFfmpegAsync(
            ffmpegPath,
            new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", sourceVideoPath,
                "-frames:v", "1",
                "-an",
                previewFramePath
            },
            previewFramePath,
            "FFmpeg 无法读取 pv.mp4 的视频画面。",
            cancellationToken);
    }

    private static async Task EncodeVideoAsync(
        string ffmpegPath,
        string sourceVideoPath,
        string h264Path,
        int width,
        int height,
        int frameRate,
        CancellationToken cancellationToken)
    {
        await RunFfmpegAsync(
            ffmpegPath,
            new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", sourceVideoPath,
                "-an",
                "-vf", $"fps={frameRate},scale={width}:{height}:flags=lanczos,setsar=1",
                "-c:v", "libx264",
                "-preset", "medium",
                "-profile:v", "high",
                "-level:v", "4.1",
                "-pix_fmt", "yuv420p",
                "-r", frameRate.ToString(),
                "-x264-params", $"aud=1:repeat-headers=1:keyint={frameRate * 2}:min-keyint={frameRate * 2}:scenecut=0",
                "-f", "h264",
                h264Path
            },
            h264Path,
            "FFmpeg 无法将 pv.mp4 编码为 H.264。",
            cancellationToken);
    }

    private static async Task EncodeSingleFrameAsync(
        string ffmpegPath,
        string sourceImagePath,
        string h264Path,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        await RunFfmpegAsync(
            ffmpegPath,
            new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", sourceImagePath,
                "-frames:v", "1",
                "-an",
                "-vf", $"scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,crop={width}:{height},setsar=1",
                "-c:v", "libx264",
                "-preset", "medium",
                "-tune", "stillimage",
                "-profile:v", "high",
                "-level:v", "4.1",
                "-pix_fmt", "yuv420p",
                "-r", "1",
                "-x264-params", "aud=1:repeat-headers=1:keyint=1:min-keyint=1:scenecut=0",
                "-f", "h264",
                h264Path
            },
            h264Path,
            "FFmpeg 无法将 bg.jpg 编码为 H.264。",
            cancellationToken);
    }

    private static async Task RunFfmpegAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        string expectedOutputPath,
        string errorMessage,
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 FFmpeg。");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0 || !File.Exists(expectedOutputPath) || new FileInfo(expectedOutputPath).Length == 0)
        {
            var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException(
                errorMessage +
                (string.IsNullOrWhiteSpace(details) ? string.Empty : "\n" + details.Trim()));
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
                    ?? throw new InvalidDataException("媒体来源不包含可读取的图像帧。");
        var width = Math.Max(2, frame.PixelWidth - frame.PixelWidth % 2);
        var height = Math.Max(2, frame.PixelHeight - frame.PixelHeight % 2);
        return (width, height);
    }

    private static (int Width, int Height) FitVideoDimensions((int Width, int Height) source)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                MaximumVideoWidth / (double)source.Width,
                MaximumVideoHeight / (double)source.Height));
        var width = Math.Max(2, (int)Math.Floor(source.Width * scale));
        var height = Math.Max(2, (int)Math.Floor(source.Height * scale));
        return (width - width % 2, height - height % 2);
    }

    private static void ValidateUsmFile(string path)
    {
        Span<byte> signature = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(signature) != signature.Length ||
            signature[0] != (byte)'C' || signature[1] != (byte)'R' ||
            signature[2] != (byte)'I' || signature[3] != (byte)'D')
        {
            throw new InvalidDataException("生成的文件不是有效的 CRID/USM 容器。");
        }
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

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Cancellation should preserve the original cancellation exception.
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
            // Temporary cleanup failures should not hide the real export result or error.
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
            // Temporary cleanup failures should not hide the real export result or error.
        }
    }
}
