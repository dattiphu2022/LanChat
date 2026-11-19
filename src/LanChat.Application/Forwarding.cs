using LanChat.Domain;

namespace LanChat.Application;

public sealed record ForwardDestination(Guid Id, bool Group, string Name);
public sealed record ForwardRequest(Guid MessageId, ForwardDestination Destination);
public enum ForwardOutcome { Queued, Failed, Cancelled }
public sealed record ForwardResult(ForwardRequest Request, ForwardOutcome Outcome, Guid? CreatedMessageId = null, string? Error = null)
{ public Exception? Cause { get; init; } }
public sealed record ForwardProgress(int Completed, int Total, string Destination, FileProgress? File = null);

public sealed partial class ChatService
{
    public async Task<ForwardResult[]> ForwardBatchAsync(ForwardRequest[] requests, IProgress<ForwardProgress>? progress = null, CancellationToken ct = default)
    {
        var pending = requests.GroupBy(r => (r.MessageId, r.Destination.Id, r.Destination.Group)).Select(g => g.First()).ToArray();
        if (pending.Length == 0) return [];
        var results = new List<ForwardResult>();
        var details = await distributed.GetMessageDetailsBatchAsync(pending.Select(r => r.MessageId).Distinct().ToArray(), ct).ConfigureAwait(false);
        var byId = details.ToDictionary(d => d.Message.Id);
        var ordered = pending.GroupBy(r => r.MessageId).OrderBy(g => byId.TryGetValue(g.Key, out var d) ? d.Message.PhysicalTicks : long.MaxValue)
            .ThenBy(g => byId.TryGetValue(g.Key, out var d) ? d.Message.LogicalCounter : 0)
            .ThenBy(g => byId.TryGetValue(g.Key, out var d) ? d.Message.OriginNodeId : Guid.Empty).ThenBy(g => g.Key).ToArray();
        foreach (var source in ordered)
        {
            var targets = source.ToArray();
            AttachmentDto[][] copies;
            try
            {
                ct.ThrowIfCancellationRequested();
                var original = await ReadForwardableAsync(source.Key, ct).ConfigureAwait(false);
                copies = Enumerable.Range(0, targets.Length).Select(_ => Array.Empty<AttachmentDto>()).ToArray();
                var perTarget = Enumerable.Range(0, targets.Length).Select(_ => new List<AttachmentDto>()).ToArray();
                foreach (var file in original.Attachments)
                {
                    var report = new ForwardFileProgress(p => progress?.Report(new(results.Count, pending.Length, targets[0].Destination.Name, p)));
                    var prepared = await files.PrepareForwardCopiesAsync(file, targets.Length, report, ct).ConfigureAwait(false);
                    for (var index = 0; index < targets.Length; index++) perTarget[index].Add(prepared[index]);
                }
                copies = perTarget.Select(items => items.ToArray()).ToArray();
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && !ct.IsCancellationRequested) throw;
                foreach (var target in targets) results.Add(new(target, ct.IsCancellationRequested ? ForwardOutcome.Cancelled : ForwardOutcome.Failed, Error: ex.Message) { Cause = ex });
                progress?.Report(new(results.Count, pending.Length, targets[0].Destination.Name));
                continue;
            }
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                try
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(results.Count, pending.Length, target.Destination.Name));
                    // Revalidate source and membership after all potentially long disk/network work.
                    var original = await ReadForwardableAsync(source.Key, ct).ConfigureAwait(false);
                    var destination = target.Destination.Group ? target.Destination.Id : await store.OpenDirectAsync(target.Destination.Id, ct).ConfigureAwait(false);
                    var sent = await distributed.SendToConversationAsync(destination, original.Message.Text,
                        forwardedFrom: source.Key, attachments: copies[index], ct: ct).ConfigureAwait(false);
                    // A local commit is queued, never proof that the recipient has received it.
                    results.Add(new(target, ForwardOutcome.Queued, sent.Id));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { results.Add(new(target, ForwardOutcome.Cancelled)); }
                catch (Exception ex) { results.Add(new(target, ForwardOutcome.Failed, Error: ex.Message) { Cause = ex }); }
                progress?.Report(new(results.Count, pending.Length, target.Destination.Name));
            }
        }
        return results.ToArray();
    }
    private async Task<MessageDetails> ReadForwardableAsync(Guid id, CancellationToken ct)
    {
        var details = await distributed.GetMessageDetailsAsync(id, ct).ConfigureAwait(false);
        var conversations = await store.GetConversationsAsync(ct: ct).ConfigureAwait(false);
        if (details.Message.IsRecalled || details.Message.IsDeletedLocal || details.Message.Kind == MessageKind.System ||
            !conversations.Any(c => c.Id == details.Message.ConversationId))
            throw new InvalidOperationException("Tin gốc đã thu hồi, bị xóa hoặc bạn không còn quyền chuyển tiếp.");
        return details;
    }
    private sealed class ForwardFileProgress(Action<FileProgress> report) : IProgress<FileProgress>
    { public void Report(FileProgress value) => report(value); }
}
