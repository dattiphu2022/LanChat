using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace LanChat.Infrastructure;

// One UDP receiver per port. Routing selects a mailbox; each network still verifies its own HMAC.
public sealed class DiscoverySockets
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Receiver> _receivers = new();

    public Subscription Subscribe(int port, Guid clusterId)
    {
        lock (_gate)
        {
            if (!_receivers.TryGetValue(port, out var receiver))
            {
                receiver = new Receiver(port);
                _receivers.Add(port, receiver);
            }
            var subscription = new Subscription(this, receiver, port, clusterId);
            receiver.Add(subscription);
            return subscription;
        }
    }

    private void Remove(Subscription subscription, Receiver receiver, int port)
    {
        lock (_gate)
        {
            if (!receiver.Remove(subscription)) return;
            _receivers.Remove(port);
            receiver.Close();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly DiscoverySockets _owner;
        private readonly Receiver _receiver;
        private readonly int _port;
        internal Guid ClusterId { get; }
        internal Channel<UdpReceiveResult> Inbox { get; } = Channel.CreateBounded<UdpReceiveResult>(
            new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

        internal Subscription(DiscoverySockets owner, Receiver receiver, int port, Guid clusterId)
        { _owner = owner; _receiver = receiver; _port = port; ClusterId = clusterId; }

        public Task<UdpReceiveResult> ReceiveAsync(CancellationToken ct) => Inbox.Reader.ReadAsync(ct).AsTask();
        public Task SendAsync(byte[] data, IPEndPoint target) => _receiver.SendAsync(data, target);
        public void Dispose() => _owner.Remove(this, _receiver, _port);
    }

    internal sealed class Receiver
    {
        private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
        private readonly object _gate = new();
        private readonly List<Subscription> _subscriptions = new();
        private bool _closed;

        public Receiver(int port)
        {
            try
            {
                _udp.ExclusiveAddressUse = false;
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.EnableBroadcast = true;
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                _ = ReceiveAsync();
            }
            catch { _udp.Close(); throw; }
        }

        public Task SendAsync(byte[] data, IPEndPoint target) => _udp.SendAsync(data, data.Length, target);

        public void Add(Subscription subscription)
        {
            lock (_gate) _subscriptions.Add(subscription);
        }

        public bool Remove(Subscription subscription)
        {
            lock (_gate)
            {
                if (!_subscriptions.Remove(subscription)) return false;
                subscription.Inbox.Writer.TryComplete();
                return _subscriptions.Count == 0;
            }
        }

        public void Close()
        {
            lock (_gate) { _closed = true; _udp.Close(); }
        }

        private async Task ReceiveAsync()
        {
            while (true)
            {
                try
                {
                    var packet = await _udp.ReceiveAsync().ConfigureAwait(false);
                    if (packet.Buffer.Length > 8192) continue;
                    using var json = JsonDocument.Parse(packet.Buffer);
                    if (json.RootElement.ValueKind != JsonValueKind.Object ||
                        !json.RootElement.TryGetProperty("ClusterId", out var cluster) ||
                        cluster.ValueKind != JsonValueKind.String || !cluster.TryGetGuid(out var id)) continue;
                    lock (_gate)
                        foreach (var subscription in _subscriptions)
                            if (subscription.ClusterId == id) subscription.Inbox.Writer.TryWrite(packet);
                }
                catch (JsonException) { }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    lock (_gate) if (_closed) return;
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        }
    }
}
