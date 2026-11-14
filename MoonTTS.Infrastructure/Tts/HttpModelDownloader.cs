using System.Buffers;
using System.Diagnostics;
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
        - ProbeRemoteLengthAsync(string url, CancellationToken cancellationToken) : Task<long?>
        - CleanUpTempFile(string? tempPath) : void 
*/

public class HttpModelDownloader : IModelDownloader
{
    private const int DownloadBufferSize = 65536;
    private const long ProgressReportIntervalMs = 50;

    private readonly HttpClient _httpClient;
    
    /* Injects HttpClient to not open too many network sockets */
    public HttpModelDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /* Downloads missing model files atomically with HEAD size probing, shared buffer pooling, and throttled progress */
    public async Task<Result> DownloadModelAsync(
        ModelMetadata model,
        string destinationFolder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string? currentTempFilePath = null;

        try
        {
            Directory.CreateDirectory(destinationFolder);

            var filesToDownload = new List<(string FileName, string Url, long? RemoteLength)>();
            long totalExpectedBytes = 0;
            long totalDownloaded = 0;

            /* Probes expected file sizes via HEAD and skips shared/complete files already on disk */
            foreach (var file in model.DownloadFiles)
            {
                string finalFilePath = Path.Combine(destinationFolder, file.Key);
                long? remoteLength = await ProbeRemoteLengthAsync(file.Value, cancellationToken);

                if (File.Exists(finalFilePath))
                {
                    long existingSize = new FileInfo(finalFilePath).Length;
                    if (existingSize > 0 && (!remoteLength.HasValue || existingSize == remoteLength.Value))
                    {
                        totalExpectedBytes += existingSize;
                        totalDownloaded += existingSize;
                        continue;
                    }
                }

                if (remoteLength.HasValue)
                    totalExpectedBytes += remoteLength.Value;

                filesToDownload.Add((file.Key, file.Value, remoteLength));
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
                foreach (var (expectedFileName, remoteUrl, _) in filesToDownload)
                {
                    string finalFilePath = Path.Combine(destinationFolder, expectedFileName);
                    currentTempFilePath = finalFilePath + ".part";

                    using var response = await _httpClient.GetAsync(
                        remoteUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        CleanUpTempFile(currentTempFilePath);
                        return Result.Failure(
                            $"Server returned error {(int)response.StatusCode}: {response.ReasonPhrase} for {expectedFileName}.");
                    }

                    using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fileStream = new FileStream(
                        currentTempFilePath,
                        FileMode.Create,
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
                            totalDownloaded += bytesRead;

                            if (progress != null && totalExpectedBytes > 0)
                            {
                                long now = Stopwatch.GetTimestamp();
                                if (now - lastReportTimestamp >= minReportTicks)
                                {
                                    lastReportTimestamp = now;
                                    double percentage = Math.Min(100.0, Math.Round((double)totalDownloaded / totalExpectedBytes * 100, 2));
                                    progress.Report(percentage);
                                }
                            }
                        }

                        await fileStream.FlushAsync(cancellationToken);
                    }

                    File.Move(currentTempFilePath, finalFilePath, overwrite: true);
                    currentTempFilePath = null;
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
            CleanUpTempFile(currentTempFilePath);
            return Result.Failure("Model download was canceled.");
        }
        catch (HttpRequestException ex)
        {
            CleanUpTempFile(currentTempFilePath);
            return Result.Failure($"Network error while downloading model: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            CleanUpTempFile(currentTempFilePath);
            return Result.Failure($"Failed to download model files: {ex.Message}", ex);
        }
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

    /* Removes partial temporary download files after failure or cancellation */
    private static void CleanUpTempFile(string? tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* Ignores cleanup errors */ }
        }
    }
}
