using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using LanChat.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LanChat.Infrastructure;

public sealed record ClusterConfiguration(Guid ClusterId, string ProtectedSecret, int ServicePort, int DiscoveryPort,
    Guid BootstrapNodeId = default, string? ProtectedTlsPfx = null, Guid? InitialNodeId = null);
public sealed record JoinInvitation(Guid ClusterId, string Secret, int Version = 2, Guid BootstrapNodeId = default, string? TlsCertificatePfx = null);

public static class ClusterConfigurationStore
{
    public static string CreateInvitation()
    {
        var cluster = Guid.CreateVersion7();
        return EncodeInvitation(new(cluster, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 2, Guid.CreateVersion7(), Convert.ToBase64String(ClusterTls.CreateCertificate(cluster))));
    }
    public static string CreateUpgradeInvitation(NodeOptions options, Guid existingNodeId) => EncodeInvitation(new(options.ClusterId,
        Convert.ToBase64String(options.ClusterSecret), 2, existingNodeId, Convert.ToBase64String(ClusterTls.CreateCertificate(options.ClusterId))));
    private static string EncodeInvitation(JoinInvitation value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));
    public static JoinInvitation ParseInvitation(string text)
    {
        try
        {
            var value = JsonSerializer.Deserialize<JoinInvitation>(Convert.FromBase64String(text.Trim())) ?? throw new FormatException();
            if (value.ClusterId == Guid.Empty || value.Version != 2 || value.BootstrapNodeId == Guid.Empty || string.IsNullOrWhiteSpace(value.TlsCertificatePfx) ||
                value.TlsCertificatePfx.Length > 20000 || Convert.FromBase64String(value.Secret).Length != 32) throw new FormatException();
            using var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(value.TlsCertificatePfx), null, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) throw new FormatException();
            return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or CryptographicException)
        { throw new ArgumentException("Mã tham gia không hợp lệ. Hãy sao chép đầy đủ mã từ máy đã tạo mạng chat."); }
    }
    public static async Task SaveAsync(NodeOptions options, string invitation, CancellationToken ct = default, bool creator = false)
    {
        var parsed = ParseInvitation(invitation);
        options.ClusterId = parsed.ClusterId; options.ClusterSecret = Convert.FromBase64String(parsed.Secret);
        options.BootstrapNodeId = parsed.BootstrapNodeId; options.TlsCertificatePfx = Convert.FromBase64String(parsed.TlsCertificatePfx!);
        if (creator) options.InitialNodeId = options.BootstrapNodeId;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cấu hình người dùng yêu cầu Windows DPAPI.");
        var protectedSecret = ProtectedData.Protect(options.ClusterSecret, null, DataProtectionScope.CurrentUser);
        var protectedTls = ProtectedData.Protect(options.TlsCertificatePfx, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "cluster.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new ClusterConfiguration(options.ClusterId,
            Convert.ToBase64String(protectedSecret), options.ServicePort, options.DiscoveryPort, options.BootstrapNodeId, Convert.ToBase64String(protectedTls), options.InitialNodeId)), ct);
        File.Move(temp, path, true);
    }
    public static async Task<bool> LoadAsync(NodeOptions options, CancellationToken ct = default)
    {
        var path = Path.Combine(options.DataDirectory, "cluster.json");
        if (!File.Exists(path)) return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var value = JsonSerializer.Deserialize<ClusterConfiguration>(await File.ReadAllTextAsync(path, ct)) ?? throw new InvalidDataException("Cấu hình cluster bị hỏng.");
        if (!options.OverrideServicePort) options.ServicePort = value.ServicePort;
        if (!options.OverrideDiscoveryPort) options.DiscoveryPort = value.DiscoveryPort;
        options.ClusterId = value.ClusterId;
        options.ClusterSecret = ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedSecret), null, DataProtectionScope.CurrentUser);
        options.BootstrapNodeId = value.BootstrapNodeId; options.InitialNodeId = value.InitialNodeId;
        if (value.ProtectedTlsPfx is not null) options.TlsCertificatePfx = ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedTlsPfx), null, DataProtectionScope.CurrentUser);
        if (options.ClusterId == Guid.Empty || options.ClusterSecret.Length != 32) throw new InvalidDataException("Cấu hình cluster không hợp lệ.");
        return true;
    }
    public static string GetInvitation(NodeOptions options) => EncodeInvitation(new(options.ClusterId, Convert.ToBase64String(options.ClusterSecret), 2,
        options.BootstrapNodeId, Convert.ToBase64String(options.TlsCertificatePfx)));
    public static async Task<Guid> ReadExistingNodeIdAsync(string dataDirectory, CancellationToken ct = default)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "lanchat.db"), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString); await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT NodeId FROM LocalSettings LIMIT 1";
        return Guid.Parse((string)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidDataException("Không tìm thấy NodeId cũ.")));
    }
}

public static class ServiceRegistration
{
    public static IHost BuildHost(NodeOptions options, Action<IServiceCollection>? configure = null)
    {
        ValidateDataDirectory(options.DataDirectory);
        if (options.ServicePort is < 1024 or > 65535 || options.DiscoveryPort is < 1024 or > 65535)
            throw new ArgumentException("Cổng phải trong khoảng 1024–65535.");
        if (options.ClusterId == Guid.Empty || options.ClusterSecret.Length != 32) throw new ArgumentException("Cần tạo hoặc tham gia mạng chat trước.");
        Directory.CreateDirectory(options.DataDirectory);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var logger = new LoggerConfiguration().MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(options.DataDirectory, "Logs", "lanchat-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true).CreateLogger();
        builder.Logging.AddSerilog(logger, dispose: true);
        builder.Services.AddSingleton(options);
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(options.DataDirectory, "lanchat.db"), DefaultTimeout = 5, Pooling = false }.ToString();
        builder.Services.AddDbContextFactory<ChatDbContext>(x => x.UseSqlite(connection));
        builder.Services.AddSingleton<ChatStore>();
        builder.Services.AddSingleton<IChatStore>(x => x.GetRequiredService<ChatStore>());
        builder.Services.AddSingleton<IDistributedStore>(x => x.GetRequiredService<ChatStore>());
        builder.Services.AddSingleton<IChatService, ChatService>();
        builder.Services.AddSingleton<ILocalNetworkInfoProvider, LocalNetworkInfoProvider>();
        builder.Services.AddSingleton<NetworkHealth>();
        builder.Services.AddSingleton<WireProtocol>();
        builder.Services.AddSingleton<ClusterTls>();
        builder.Services.AddSingleton<FaultInjection>();
        builder.Services.AddSingleton<NodeRuntimeState>();
        builder.Services.AddSingleton<PeerTransport>();
        builder.Services.AddSingleton<IPeerTransport>(x => x.GetRequiredService<PeerTransport>());
        builder.Services.AddSingleton<IClusterTransport>(x => x.GetRequiredService<PeerTransport>());
        builder.Services.AddSingleton<ConsensusService>();
        builder.Services.AddSingleton<IConsensusService>(x => x.GetRequiredService<ConsensusService>());
        builder.Services.AddSingleton<ReplicationBackgroundService>();
        builder.Services.AddSingleton<PeerFileTransferService>();
        builder.Services.AddSingleton<IFileTransferService>(x => x.GetRequiredService<PeerFileTransferService>());
        builder.Services.AddSingleton<ILanScanner, LanScanner>();
        builder.Services.AddHostedService<PeerConnectionManager>();
        builder.Services.AddSingleton<PeerDiscoveryBackgroundService>();
        builder.Services.AddHostedService(x => x.GetRequiredService<PeerDiscoveryBackgroundService>());
        builder.Services.AddHostedService(x => x.GetRequiredService<ConsensusService>());
        builder.Services.AddHostedService(x => x.GetRequiredService<ReplicationBackgroundService>());
        builder.Services.AddHostedService<FallbackScanBackgroundService>();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }
    public static void ValidateDataDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (new Uri(full).IsUnc || OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new ArgumentException("Mỗi máy cần thư mục dữ liệu trên ổ đĩa cục bộ. Không đặt SQLite trên thư mục mạng.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && current.LinkTarget is not null)
                throw new ArgumentException("Không đặt dữ liệu trong thư mục liên kết; hãy chọn thư mục cục bộ trực tiếp.");
    }
}
