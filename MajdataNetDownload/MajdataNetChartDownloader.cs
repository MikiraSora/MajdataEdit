using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MajdataEdit.MajdataNetDownload;

internal sealed record MajdataNetDownloadResult(
    string ChartDirectory,
    string SongId,
    string Title,
    IReadOnlyList<string> DownloadedFiles);

internal static class MajdataNetChartDownloader
{
    private const string MajdataNetHost = "majdata.net";
    private const string ApiRoot = "https://majdata.net/api3/api/maichart/";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static bool TryParseSongId(string link, out string songId)
    {
        songId = string.Empty;
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsMajdataNetHost(uri.Host) ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/song", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var queryPart in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = queryPart.Split('=', 2);
            if (!string.Equals(Uri.UnescapeDataString(pair[0]), "id", StringComparison.OrdinalIgnoreCase) ||
                pair.Length != 2)
            {
                continue;
            }

            if (Guid.TryParse(Uri.UnescapeDataString(pair[1]), out var parsedId))
            {
                songId = parsedId.ToString("D");
                return true;
            }
        }

        return false;
    }

    public static async Task<MajdataNetDownloadResult> DownloadAsync(
        string link,
        string targetDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseSongId(link, out var songId))
        {
            throw new ArgumentException("链接格式无效，必须是 majdata.net 的歌曲页面链接。", nameof(link));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("下载保存目录不能为空。", nameof(targetDirectory));
        }

        targetDirectory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(targetDirectory);

        progress?.Report("正在读取谱面信息...");
        var title = await GetSongTitleAsync(songId, cancellationToken);
        var temporaryDirectory = Path.Combine(
            targetDirectory,
            $".majdata-download-{songId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        var downloadedFiles = new List<string>();
        try
        {
            await DownloadRequiredFileAsync(songId, "chart", "maidata.txt", temporaryDirectory, progress,
                cancellationToken);
            await DownloadRequiredFileAsync(songId, "track", "track.mp3", temporaryDirectory, progress,
                cancellationToken);
            await DownloadRequiredFileAsync(songId, "image?fullImage=true", "bg.jpg", temporaryDirectory, progress,
                cancellationToken);

            var hasVideo = await TryDownloadOptionalFileAsync(
                songId,
                "video",
                "pv.mp4",
                temporaryDirectory,
                progress,
                cancellationToken);

            var fileNames = new List<string> { "maidata.txt", "track.mp3", "bg.jpg" };
            if (hasVideo)
            {
                fileNames.Add("pv.mp4");
            }

            progress?.Report("正在写入谱面文件...");
            foreach (var fileName in fileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = Path.Combine(temporaryDirectory, fileName);
                var targetPath = Path.Combine(targetDirectory, fileName);
                File.Move(sourcePath, targetPath, true);
                downloadedFiles.Add(targetPath);
            }

            return new MajdataNetDownloadResult(targetDirectory, songId, title, downloadedFiles);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task<string> GetSongTitleAsync(string songId, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            BuildApiUri(songId, "summary"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response.StatusCode, "谱面信息");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var summary = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            return summary.RootElement.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("majdata.net 返回了无法识别的谱面信息。", exception);
        }
    }

    private static async Task DownloadRequiredFileAsync(
        string songId,
        string endpoint,
        string fileName,
        string temporaryDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"正在下载 {fileName}...");
        var targetPath = Path.Combine(temporaryDirectory, fileName);
        using var response = await HttpClient.GetAsync(
            BuildApiUri(songId, endpoint),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response.StatusCode, fileName);
        }

        await WriteResponseToFileAsync(response, targetPath, cancellationToken);
        if (new FileInfo(targetPath).Length == 0)
        {
            throw new InvalidDataException($"服务器返回的 {fileName} 是空文件。");
        }
    }

    private static async Task<bool> TryDownloadOptionalFileAsync(
        string songId,
        string endpoint,
        string fileName,
        string temporaryDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"正在检查可选资源 {fileName}...");
        try
        {
            using var response = await HttpClient.GetAsync(
                BuildApiUri(songId, endpoint),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var targetPath = Path.Combine(temporaryDirectory, fileName);
            await WriteResponseToFileAsync(response, targetPath, cancellationToken);
            return new FileInfo(targetPath).Length > 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task WriteResponseToFileAsync(
        HttpResponseMessage response,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var targetStream = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await sourceStream.CopyToAsync(targetStream, cancellationToken);
    }

    private static Uri BuildApiUri(string songId, string endpoint)
    {
        return new Uri(ApiRoot + Uri.EscapeDataString(songId) + "/" + endpoint);
    }

    private static bool IsMajdataNetHost(string host)
    {
        return string.Equals(host, MajdataNetHost, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "www." + MajdataNetHost, StringComparison.OrdinalIgnoreCase);
    }

    private static Exception CreateHttpException(HttpStatusCode statusCode, string resourceName)
    {
        return statusCode switch
        {
            HttpStatusCode.NotFound => new InvalidOperationException(
                $"找不到 {resourceName}，链接可能无效、谱面已删除或该资源不存在。"),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new InvalidOperationException(
                $"majdata.net 拒绝访问 {resourceName}，请稍后重试。"),
            _ => new HttpRequestException(
                $"下载 {resourceName} 失败：服务器返回 {(int)statusCode} {statusCode}。")
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MajdataEdit", "4.4"));
        return client;
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
            // A failed cleanup must not hide the original download result or error.
        }
    }
}
