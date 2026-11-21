using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MoonTTS.Core.Utilities;
using MoonTTS.Domain.Common;
using MoonTTS.Domain.Interfaces;
using MoonTTS.Domain.ValueObjects;

namespace MoonTTS.Infrastructure.Tts;

/* File Structure:
        - HttpModelDownloader(HttpClient httpClient)
        - DownloadModelAsync(
            ModelMetadata model,
            string destinationFolder,
            IProgress<double>? progress,
            CancellationToken cancellationToken) : Task<Result>
        - DownloadFileWithRetryAsync(...) : Task<Result>
        - ProbeRemoteLengthAsync(string url, CancellationToken cancellationToken) : Task<long?>
        - VerifyChecksumAsync(string filePath, string expectedSha256, CancellationToken cancellationToken) : Task<bool>
        - TryResolveSafeDestinationPath(string destinationFolder, string fileName) : string?
        - IsTransientStatus(HttpStatusCode statusCode) : bool
        - CleanUpTempFile(string? tempPath) : void
*/

/* Resumable, retrying HTTP model downloader with path-traversal protection and optional SHA-256 verification */
public class HttpModelDownloader : IModelDownloader
{
    private const int DownloadBufferSize = 65536;
    private const long ProgressReportIntervalMs = 50;
    private const int MaxRetryAttempts = 3;

    private readonly HttpClient _httpClient;

    /* Injects HttpClient to share socket pools across downloads */
    public HttpModelDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /* Downloads missing model files atomically with HTTP Range resumption, retries, and optional SHA-256 verification */
    public async Task<Result> DownloadModelAsync(
        ModelMetadata model,
        string destinationFolder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(destinationFolder);

            var filesToDownload = new List<(string FileName, string FinalPath, string Url, long? RemoteLength)>();
            long totalExpectedBytes = 0;
            long totalDownloaded = 0;

            /* Probes expected file sizes via HEAD and skips valid complete files already on disk */
            foreach (var file in model.DownloadFiles)
            {
                string? finalFilePath = TryResolveSafeDestinationPath(destinationFolder, file.Key);
                if (finalFilePath == null)
                    return Result.Failure($"Invalid model file path '{file.Key}' in manifest.");

                long? remoteLength = await ProbeRemoteLengthAsync(file.Value, cancellationToken);

                if (File.Exists(finalFilePath))
                {
                    long existingSize = new FileInfo(finalFilePath).Length;
                    bool sizeMatches = existingSize > 0 && (!remoteLength.HasValue || existingSize == remoteLength.Value);
                    bool checksumMatches = true;
                    if (sizeMatches && model.FileChecksums != null && model.FileChecksums.TryGetValue(file.Key, out string? expectedHash))
                        checksumMatches = await VerifyChecksumAsync(finalFilePath, expectedHash, cancellationToken);

                    if (sizeMatches && checksumMatches)
                    {
                        totalExpectedBytes += existingSize;
                        totalDownloaded += existingSize;
                        continue;
                    }
                }

                if (remoteLength.HasValue)
                    totalExpectedBytes += remoteLength.Value;

                filesToDownload.Add((file.Key, finalFilePath, file.Value, remoteLength));
            }

            if (filesToDownload.Count == 0)
            {
                progress?.Report(100.0);
                return Result.Success();
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(DownloadBufferSize);
            long minReportTicks = Stopwatch.Frequency * ProgressReportIntervalMs / 1000;
            long lastReportTimestamp = 0;

            try
            {
                foreach (var (expectedFileName, finalFilePath, remoteUrl, _) in filesToDownload)
                {
                    string tempFilePath = finalFilePath + ".part";
                    string? expectedHash = model.FileChecksums != null
                        && model.FileChecksums.TryGetValue(expectedFileName, out string? hash)
                        ? hash
                        : null;

                    var fileResult = await DownloadFileWithRetryAsync(
                        expectedFileName,
                        remoteUrl,
                        tempFilePath,
                        finalFilePath,
                        expectedHash,
                        buffer,
                        totalExpectedBytes,
                        minReportTicks,
                        progress,
                        () => totalDownloaded,
                        delta => totalDownloaded += delta,
                        () => lastReportTimestamp,
                        ts => lastReportTimestamp = ts,
                        cancellationToken);

                    if (fileResult.IsFailure)
                        return fileResult;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            progress?.Report(100.0);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            /* Keep .part file on disk so a subsequent download resumes via HTTP Range */
            return Result.Failure("Model download was canceled.");
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure($"Network error while downloading model: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AppLogger.Error("HttpModelDownloader", $"Failed to download model '{model.Id}'", ex);
            return Result.Failure($"Failed to download model files: {ex.Message}", ex);
        }
    }

    /* Downloads a single file with HTTP Range resume and exponential backoff retry on transient network/server errors */
    private async Task<Result> DownloadFileWithRetryAsync(
        string expectedFileName,
        string remoteUrl,
        string tempFilePath,
        string finalFilePath,
        string? expectedSha256,
        byte[] buffer,
        long totalExpectedBytes,
        long minReportTicks,
        IProgress<double>? progress,
        Func<long> getTotalDownloaded,
        Action<long> addDownloaded,
        Func<long> getLastReportTs,
        Action<long> setLastReportTs,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                long existingPartLength = File.Exists(tempFilePath) ? new FileInfo(tempFilePath).Length : 0;

                using var request = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
                if (existingPartLength > 0)
                    request.Headers.Range = new RangeHeaderValue(existingPartLength, null);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                /* If the server rejects our Range offset (e.g. file grew/changed), restart cleanly from byte 0 */
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingPartLength > 0)
                {
                    CleanUpTempFile(tempFilePath);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransientStatus(response.StatusCode) && attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt * attempt), cancellationToken);
                        continue;
                    }

                    if ((int)response.StatusCode is >= 400 and < 500)
                        CleanUpTempFile(tempFilePath);

                    return Result.Failure(
                        $"Server returned error {(int)response.StatusCode}: {response.ReasonPhrase} for {expectedFileName}.");
                }

                bool isResuming = existingPartLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (isResuming && attempt == 1)
                    addDownloaded(existingPartLength);

                FileMode fileMode = isResuming ? FileMode.Append : FileMode.Create;

                using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(
                    tempFilePath,
                    fileMode,
                    FileAccess.Write,
                    FileShare.None,
                    DownloadBufferSize,
                    FileOptions.Asynchronous))
                {
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(
                               buffer.AsMemory(0, DownloadBufferSize),
                               cancellationToken)) != 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        addDownloaded(bytesRead);

                        if (progress != null && totalExpectedBytes > 0)
                        {
                            long now = Stopwatch.GetTimestamp();
                            if (now - getLastReportTs() >= minReportTicks)
                            {
                                setLastReportTs(now);
                                double percentage = Math.Min(100.0, Math.Round((double)getTotalDownloaded() / totalExpectedBytes * 100, 2));
                                progress.Report(percentage);
                            }
                        }
                    }

                    await fileStream.FlushAsync(cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(expectedSha256)
                    && !await VerifyChecksumAsync(tempFilePath, expectedSha256, cancellationToken))
                {
                    CleanUpTempFile(tempFilePath);
                    return Result.Failure($"Checksum verification failed for '{expectedFileName}'.");
                }

                File.Move(tempFilePath, finalFilePath, overwrite: true);
                return Result.Success();
            }
            catch (HttpRequestException) when (attempt < MaxRetryAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt * attempt), cancellationToken);
            }
            catch (IOException) when (attempt < MaxRetryAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt * attempt), cancellationToken);
            }
        }

        return Result.Failure($"Failed to download '{expectedFileName}' after {MaxRetryAttempts} attempts.");
    }

    /* Queries remote Content-Length using HEAD first and falls back to header-only GET only if needed */
    private async Task<long?> ProbeRemoteLengthAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await _httpClient.SendAsync(
                headRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength.HasValue)
                return headResponse.Content.Headers.ContentLength.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            /* Fall back to header-only GET below */
        }

        try
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var getResponse = await _httpClient.SendAsync(
                getRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (getResponse.IsSuccessStatusCode && getResponse.Content.Headers.ContentLength.HasValue)
                return getResponse.Content.Headers.ContentLength.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            /* Unknown content length; actual download handles HTTP errors directly */
        }

        return null;
    }

    /* Computes SHA-256 of a file on disk and compares it against the expected hex digest */
    private static async Task<bool> VerifyChecksumAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, FileOptions.Asynchronous);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        string actualHex = Convert.ToHexString(hash);
        return string.Equals(actualHex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /* Resolves a manifest filename inside destinationFolder while rejecting directory traversal attempts */
    private static string? TryResolveSafeDestinationPath(string destinationFolder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        string normalizedRoot = Path.GetFullPath(destinationFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(destinationFolder, fileName));

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(normalizedRoot, cmp) ? fullPath : null;
    }

    /* Identifies transient HTTP status codes eligible for retry */
    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    /* Removes corrupted or rejected temporary download files */
    private static void CleanUpTempFile(string? tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* Ignores cleanup errors */ }
        }
    }
}
