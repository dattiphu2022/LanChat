
using LanChat.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class AttachmentDownloadBackgroundService(IDistributedStore store, ChatStore chat,
    PeerFileTransferService files, NodeOptions options, ILogger<AttachmentDownloadBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PreparedFileRecheck = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new Dictionary<Guid, AttachmentDto>();
        var active = new Dictionary<Guid, Task<bool>>();
        var retryAfter = new Dictionary<Guid, DateTimeOffset>();
        long revision = -1;
        long presenceRevision = -1;
        var nextScan = DateTimeOffset.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var download in active.Where(item => item.Value.IsCompleted).ToArray())
                {
                    if (await download.Value.ConfigureAwait(false))
                    {
                        pending.Remove(download.Key);
                        retryAfter.Remove(download.Key);
                    }
                    else retryAfter[download.Key] = DateTimeOffset.UtcNow + RetryDelay;
                    active.Remove(download.Key);
                }

                var currentRevision = chat.Revision;
                var currentPresence = chat.PresenceRevision;
                if (presenceRevision != currentPresence)
                {
                    retryAfter.Clear();
                    presenceRevision = currentPresence;
                }

                if (revision != currentRevision || DateTimeOffset.UtcNow >= nextScan)
                {
                    try
                    {
                        // Only re-query when content changes or the scheduled scan is due.
                        var attachments = await store.GetAttachmentsAsync(ct: stoppingToken).ConfigureAwait(false);
                        pending = attachments.Where(file => !files.IsPrepared(file)).ToDictionary(file => file.Id);

                        // Remove retry markers for deleted/recalled or already prepared files.
                        foreach (var id in retryAfter.Keys.Where(id => !pending.ContainsKey(id)).ToArray())
                            retryAfter.Remove(id);

                        revision = currentRevision;
                        nextScan = DateTimeOffset.UtcNow + PreparedFileRecheck;
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not refresh automatic attachment downloads");
                        await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                foreach (var file in pending.Values)
                {
                    if (active.Count >= (options.ResourceSaving.Enabled ? 1 : 2)) break;
                    if (active.ContainsKey(file.Id) || (retryAfter.TryGetValue(file.Id, out var retry) && DateTimeOffset.UtcNow < retry)) continue;
                    active.Add(file.Id, PrepareAsync(file, stoppingToken));
                }

                // This loop only checks in-memory state between scheduled scans.
                await Task.Delay(options.ResourceSaving.Enabled ? 1000 : 250, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(active.Values).ConfigureAwait(false); }
    }

    private async Task<bool> PrepareAsync(AttachmentDto file, CancellationToken ct)
    {
        try
        {
            // PrepareOpenAsync cũng download mọi folder entry và công bố bản sao đặt tên đầy đủ nguyên tử.
            await files.PrepareOpenAsync(file, ct: ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            logger.LogDebug("Automatic attachment download deferred for {Attachment}: {Type}", file.Id, ex.GetType().Name);
            return false;
        }
    }
}
