using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LanChat.WinForms;

internal sealed record AppProfile(string Name, string? Department);
internal sealed record NetworkEntry(Guid Id, string Name, bool Primary = false);
internal sealed record DefaultNetworkProvision(string Invitation, bool Creator = false);
internal sealed record SavedNetworks(NetworkEntry[] Entries, NetworkEntry? DefaultNetwork, bool FirstPrivateCodeHandled);

internal sealed class NetworkCatalog(NodeOptions primary)
{
    private readonly DiscoverySockets _sockets = new();
    private readonly List<NetworkEntry> _entries = new();
    private readonly HashSet<int> _ports = new();
    private readonly string _directory = primary.DataDirectory;
    public IReadOnlyList<NetworkEntry> Entries => _entries;
    public AppProfile? Profile { get; private set; }
    public NetworkEntry? DefaultNetwork { get; private set; }
    public bool FirstPrivateCodeHandled { get; private set; }
    public bool NeedsDefaultChoice => !FirstPrivateCodeHandled && _entries.Any(IsDefault);
    public string? DefaultNetworkError { get; private set; }
    public bool IsDefault(NetworkEntry entry) => entry.Id == DefaultNetwork?.Id;

    public async Task LoadAsync()
    {
        var list = Path.Combine(_directory, "networks.json");
        if (File.Exists(list))
        {
            var text = await FrameworkIO.ReadTextAsync(list);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                _entries.AddRange(document.RootElement.Deserialize<NetworkEntry[]>() ?? []);
            else
            {
                var saved = document.RootElement.Deserialize<SavedNetworks>() ?? throw new InvalidDataException("Danh sách mạng chat không hợp lệ.");
                DefaultNetwork = saved.DefaultNetwork;
                FirstPrivateCodeHandled = saved.FirstPrivateCodeHandled;
                if (saved.Entries is null || (DefaultNetwork is not null && (DefaultNetwork.Id == Guid.Empty || string.IsNullOrWhiteSpace(DefaultNetwork.Name))))
                    throw new InvalidDataException("Thiếu cấu hình mạng mặc định.");
                _entries.AddRange(saved.Entries);
            }
        }
        else if (await ClusterConfigurationStore.LoadAsync(primary))
            _entries.Add(new NetworkEntry(primary.ClusterId, "Mạng hiện tại", true));

        if (_entries.Any(e => e.Id == Guid.Empty || string.IsNullOrWhiteSpace(e.Name)) ||
            _entries.Select(e => e.Id).Distinct().Count() != _entries.Count || _entries.Count(e => e.Primary) > 1)
            throw new InvalidDataException("Danh sách mạng chat không hợp lệ.");

        if (DefaultNetwork is null)
        {
            try { DefaultNetwork = await PrepareDefaultAsync(); }
            catch (Exception ex) when (_entries.Count > 0 && ex is IOException or JsonException or ArgumentException)
            {
                DefaultNetworkError = "Mạng mặc định: " + ex.Message;
            }
            FirstPrivateCodeHandled = _entries.Any(e => !IsDefault(e));
            if (DefaultNetwork is not null)
                await SaveNetworksAsync(_entries.Any(IsDefault) ? _entries : _entries.Prepend(DefaultNetwork));
        }

        var profile = Path.Combine(_directory, "profile.json");
        if (File.Exists(profile))
            Profile = JsonSerializer.Deserialize<AppProfile>(await FrameworkIO.ReadTextAsync(profile));
    }

    public async Task<NetworkEntry> RestoreDefaultAsync()
    {
        var entry = DefaultNetwork ??= await PrepareDefaultAsync();
        if (!Entries.Any(IsDefault)) await SaveNetworksAsync(_entries.Append(entry));
        return entry;
    }

    public Task ConfirmFirstPrivateCodeAsync(bool removeDefault) => SaveNetworksAsync(
        removeDefault ? _entries.Where(e => !IsDefault(e)) : _entries, firstPrivateCodeHandled: true);

    public async Task SaveProfileAsync(string name, string? department)
    {
        var profile = new AppProfile(name, department);
        await WriteAsync("profile.json", profile);
        Profile = profile;
    }

    public async Task<NetworkEntry[]> AddAsync(IEnumerable<string> codes, bool creator = false)
    {
        var entries = await Task.Run(() => codes.Select(code => (Code: code, Info: ClusterConfigurationStore.ParseInvitation(code))).ToArray());
        if (entries.Length == 0) throw new ArgumentException("Vui lòng nhập ít nhất một mã tham gia.");
        var legacy = new NodeOptions { DataDirectory = _directory };
        await ClusterConfigurationStore.LoadAsync(legacy);
        var next = _entries.ToList();
        var added = new List<NetworkEntry>();
        foreach (var item in entries)
        {
            if (next.Any(e => e.Id == item.Info.ClusterId)) continue;
            var name = "Mạng " + item.Info.ClusterId.ToString("N").Substring(0, 8).ToUpperInvariant();
            var entry = DefaultNetwork?.Id == item.Info.ClusterId ? DefaultNetwork :
                new NetworkEntry(item.Info.ClusterId, name, legacy.ClusterId == item.Info.ClusterId);
            var options = OptionsFor(entry);
            if (await ClusterConfigurationStore.LoadAsync(options))
            {
                if (options.ClusterId != entry.Id || options.BootstrapNodeId != item.Info.BootstrapNodeId ||
                    !FrameworkIO.FixedEquals(options.ClusterSecret, Convert.FromBase64String(item.Info.Secret)) ||
                    !SameCertificate(options.TlsCertificatePfx, Convert.FromBase64String(item.Info.TlsCertificatePfx!)))
                    throw new ArgumentException("Mã tham gia không khớp dữ liệu đã lưu của mạng " + entry.Name + ".");
            }
            else
                await ClusterConfigurationStore.SaveAsync(options, item.Code, creator: creator);
            next.Add(entry);
            added.Add(entry);
        }
        if (added.Count == 0) throw new ArgumentException("Các mã này đã có trong danh sách mạng chat.");
        await SaveNetworksAsync(next);
        return added.ToArray();
    }

    public async Task RemoveAsync(NetworkEntry entry)
    {
        await SaveNetworksAsync(_entries.Where(e => e.Id != entry.Id));
    }

    public async Task RenameAsync(NetworkEntry entry, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 60) throw new ArgumentException("Tên mạng cần từ 1 đến 60 ký tự.");
        var renamed = entry with { Name = name };
        await SaveNetworksAsync(_entries.Select(e => e.Id == entry.Id ? renamed : e));
    }

    public async Task<RunningNetwork> StartAsync(NetworkEntry entry, bool allowLegacyUpgrade, Form owner)
    {
        var options = OptionsFor(entry);
        ServiceRegistration.ValidateDataDirectory(options.DataDirectory);
        var dataLock = entry.Primary ? null : new FileStream(Path.Combine(options.DataDirectory, "instance.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (!await ClusterConfigurationStore.LoadAsync(options) || options.ClusterId != entry.Id)
                throw new InvalidDataException("Cấu hình mạng không khớp.");
            if (options.TlsCertificatePfx.Length == 0 || options.BootstrapNodeId == Guid.Empty)
            {
                if (!allowLegacyUpgrade) throw new InvalidDataException("Mạng cần nâng cấp mã tham gia.");
                using var upgrade = new Forms.ClusterUpgradeForm(options);
                if (upgrade.ShowDialog(owner) != DialogResult.OK) throw new OperationCanceledException();
            }

            var preferred = entry.Primary ? options.ServicePort : ClusterPorts.Alternate(entry.Id);
            options.ServicePort = AvailableTcpPort(preferred);
            var host = ServiceRegistration.BuildHost(options, services =>
            {
                services.AddSingleton(_sockets);
                services.AddSingleton<Controls.ChatImageService>();
            });
            _ports.Add(options.ServicePort);
            try
            {
                var store = host.Services.GetRequiredService<IChatStore>();
                await Task.Run(() => store.InitializeAsync());
                var saved = await store.GetProfileAsync();
                if (Profile is null && (entry.Primary || !_entries.Any(e => e.Primary)))
                    await SaveProfileAsync(saved.DisplayName, saved.Department);
                await ApplyProfileAsync(store);
                await host.StartAsync();
                return new RunningNetwork(host, dataLock, options.ServicePort);
            }
            catch
            {
                try { using var deadline = new CancellationTokenSource(5000); await host.StopAsync(deadline.Token); }
                finally { host.Dispose(); _ports.Remove(options.ServicePort); }
                throw;
            }
        }
        catch { dataLock?.Dispose(); throw; }
    }

    public async Task ApplyProfileAsync(IChatStore store)
    {
        if (Profile is not { } profile) return;
        var user = await store.GetProfileAsync();
        if (user.DisplayName == profile.Name && (user.Department ?? "") == (profile.Department ?? "")) return;
        user.DisplayName = profile.Name; user.Department = profile.Department;
        await store.SaveProfileAsync(user);
    }

    private NodeOptions OptionsFor(NetworkEntry entry)
    {
        if (entry.Primary) return primary;
        var options = new NodeOptions
        {
            DataDirectory = Path.Combine(_directory, "Networks", entry.Id.ToString("N")),
            ServicePort = ClusterPorts.Alternate(entry.Id),
            InitialDisplayName = Profile?.Name ?? primary.InitialDisplayName,
            InitialDepartment = Profile?.Department
        };
        return options;
    }

    public async Task StopAsync(RunningNetwork network)
    {
        try { await network.StopAsync(); }
        finally { _ports.Remove(network.Port); }
    }

    private int AvailableTcpPort(int preferred)
    {
        var listener = new TcpListener(IPAddress.Any, preferred) { ExclusiveAddressUse = true };
        try { if (!_ports.Contains(preferred)) { listener.Start(); return preferred; } }
        catch (SocketException) { }
        finally { listener.Stop(); }
        listener = new TcpListener(IPAddress.Any, 0) { ExclusiveAddressUse = true };
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static bool SameCertificate(byte[] left, byte[] right)
    {
        using var a = new System.Security.Cryptography.X509Certificates.X509Certificate2(left);
        using var b = new System.Security.Cryptography.X509Certificates.X509Certificate2(right);
        return FrameworkIO.FixedEquals(a.RawData, b.RawData);
    }

    private async Task<NetworkEntry> PrepareDefaultAsync()
    {
        // The private, deployment-provided code is optional. A fresh installation owns its own default network.
        var provisioned = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "default-network.json");
        var persisted = Path.Combine(_directory, "default-network.json");
        if (!string.Equals(provisioned, persisted, StringComparison.OrdinalIgnoreCase) && File.Exists(persisted))
            return JsonSerializer.Deserialize<NetworkEntry>(await FrameworkIO.ReadTextAsync(persisted))
                ?? throw new InvalidDataException("Cấu hình mạng mặc định không hợp lệ.");

        var provision = File.Exists(provisioned)
            ? JsonSerializer.Deserialize<DefaultNetworkProvision>(await FrameworkIO.ReadTextAsync(provisioned), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Tệp cấp mã mặc định không hợp lệ.")
            : new DefaultNetworkProvision(ClusterConfigurationStore.CreateInvitation(), Creator: true);
        var invite = ClusterConfigurationStore.ParseInvitation(provision.Invitation);
        var existing = _entries.FirstOrDefault(e => e.Id == invite.ClusterId);
        var entry = existing ?? new NetworkEntry(invite.ClusterId, "Mạng mặc định");
        var options = OptionsFor(entry);
        if (!await ClusterConfigurationStore.LoadAsync(options))
            await ClusterConfigurationStore.SaveAsync(options, provision.Invitation, creator: provision.Creator);
        else if (options.ClusterId != invite.ClusterId || options.BootstrapNodeId != invite.BootstrapNodeId ||
            !FrameworkIO.FixedEquals(options.ClusterSecret, Convert.FromBase64String(invite.Secret)) ||
            !SameCertificate(options.TlsCertificatePfx, Convert.FromBase64String(invite.TlsCertificatePfx!)))
            throw new InvalidDataException("Mã mặc định không khớp dữ liệu đã lưu.");

        if (!string.Equals(provisioned, persisted, StringComparison.OrdinalIgnoreCase))
            await WriteAsync("default-network.json", entry);
        return entry;
    }

    private async Task SaveNetworksAsync(IEnumerable<NetworkEntry> entries, bool? firstPrivateCodeHandled = null)
    {
        var next = new SavedNetworks(entries.ToArray(), DefaultNetwork, firstPrivateCodeHandled ?? FirstPrivateCodeHandled);
        await WriteAsync("networks.json", next);
        _entries.Clear();
        _entries.AddRange(next.Entries);
        FirstPrivateCodeHandled = next.FirstPrivateCodeHandled;
    }

    private async Task WriteAsync<T>(string fileName, T value)
    {
        var file = Path.Combine(_directory, fileName);
        var temp = file + ".tmp";
        await FrameworkIO.WriteTextAsync(temp, JsonSerializer.Serialize(value));
        FrameworkIO.PublishFile(temp, file);
    }
}

internal sealed class RunningNetwork(IHost host, FileStream? dataLock, int port)
{
    public IServiceProvider Services => host.Services;
    public int Port => port;

    public async Task StopAsync()
    {
        try { using var deadline = new CancellationTokenSource(8000); await host.StopAsync(deadline.Token).ConfigureAwait(false); }
        finally { try { host.Dispose(); } finally { dataLock?.Dispose(); } }
    }
}
