using LanChat.Application;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed partial class PeerFileTransferService
{
    private readonly object _preparationGate = new();
    private readonly Dictionary<Guid, Preparation> _preparations = [];
    private sealed class Preparation : IProgress<FileProgress>
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly Dictionary<long, IProgress<FileProgress>?> Waiters = [];
        internal Task<string> Task = null!;
        internal long NextWaiter;
        internal bool Abandoned;
        internal Action<FileProgress>? StageChanged;
        internal volatile FileProgress Last = new(0, 0, AttachmentPhase.Waiting);
        public void Report(FileProgress value)
        {
            IProgress<FileProgress>?[] listeners;
            bool changed;
            lock (Waiters) { changed = Last.Phase != value.Phase; Last = value; listeners = Waiters.Values.ToArray(); }
            if (changed) StageChanged?.Invoke(value);
            foreach (var listener in listeners) SafeReport(listener, value);
        }
        internal static void SafeReport(IProgress<FileProgress>? listener, FileProgress value)
        {
            try { listener?.Report(value); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { } // A closing UI must not fail another subscriber's transfer.
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        }
    }
    public FileProgress? GetPreparationState(Guid attachmentId)
    {
        lock (_preparationGate) return _preparations.TryGetValue(attachmentId, out var operation) && !operation.Task.IsCompleted ? operation.Last : null;
    }
    private async Task<string> JoinPreparationAsync(AttachmentDto file, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Preparation operation;
        long waiter;
        Task<string>? draining = null;
        lock (_preparationGate)
        {
            if (!_preparations.TryGetValue(file.Id, out operation!) || operation.Task.IsCompleted)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                operation = new Preparation { StageChanged = value => logger.LogDebug("Attachment {Attachment}: {Phase} at {Milliseconds} ms", file.Id, value.Phase, clock.ElapsedMilliseconds) };
                _preparations[file.Id] = operation;
                var snapshot = operation;
                operation.Task = Task.Run(() => PrepareExclusiveAsync(file, snapshot, snapshot.Cancellation.Token));
                _ = operation.Task.ContinueWith(completed =>
                {
                    _ = completed.Exception; // Observe faults even if all subscribers have canceled.
                    lock (_preparationGate)
                        if (_preparations.TryGetValue(file.Id, out var current) && ReferenceEquals(current, snapshot)) _preparations.Remove(file.Id);
                    snapshot.Cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (operation.Abandoned && !operation.Task.IsCompleted) { draining = operation.Task; waiter = 0; }
            else lock (operation.Waiters) { waiter = ++operation.NextWaiter; operation.Waiters.Add(waiter, progress); }
        }
        if (draining is not null)
        {
            var canceledDrain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelDrain = ct.Register(() => canceledDrain.TrySetResult(true));
            if (await Task.WhenAny(draining, canceledDrain.Task).ConfigureAwait(false) != draining) throw new OperationCanceledException(ct);
            try { await draining.ConfigureAwait(false); } catch (Exception) { }
            return await JoinPreparationAsync(file, progress, ct).ConfigureAwait(false);
        }
        Preparation.SafeReport(progress, operation.Last);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => canceled.TrySetResult(true));
        try
        {
            if (await Task.WhenAny(operation.Task, canceled.Task).ConfigureAwait(false) != operation.Task) throw new OperationCanceledException(ct);
            ct.ThrowIfCancellationRequested();
            return await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            var drain = false;
            lock (_preparationGate)
            lock (operation.Waiters)
            {
                operation.Waiters.Remove(waiter);
                if (operation.Waiters.Count == 0 && !operation.Task.IsCompleted) { operation.Abandoned = true; drain = true; }
            }
            if (drain)
            {
                // Cancellation can run arbitrary callbacks. Never invoke it while holding the coordinator locks.
                try { operation.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                try { await operation.Task.ConfigureAwait(false); } catch (Exception) { /* The writer has stopped. */ }
            }
        }
    }
}
