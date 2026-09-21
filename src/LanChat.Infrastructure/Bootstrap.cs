using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanChat.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LanChat.Infrastructure;

public sealed record ClusterConfiguration(Guid ClusterId, string ProtectedSecret, int ServicePort, int DiscoveryPort);
public sealed record JoinInvitation(Guid ClusterId, string Secret, int Version = 1);

public static class ClusterConfigurationStore
{
    public static string CreateInvitation() => EncodeInvitation(new(Guid.CreateVersion7(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    private static string EncodeInvitation(JoinInvitation value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));
    public static JoinInvitation ParseInvitation(string text)
    {
        try
        {
            var value = JsonSerializer.Deserialize<JoinInvitation>(Convert.FromBase64String(text.Trim())) ?? throw new FormatException();
            if (value.ClusterId == Guid.Empty || value.Version != 1 || Convert.FromBase64String(value.Secret).Length != 32) throw new FormatException();
            return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { throw new ArgumentException("Mã tham gia không hợp lệ. Hãy sao chép đầy đủ mã từ máy đã tạo mạng chat."); }
    }
    public static async Task SaveAsync(NodeOptions options, string invitation, CancellationToken ct = default)
    {
        var parsed = ParseInvitation(invitation);
        options.ClusterId = parsed.ClusterId; options.ClusterSecret = Convert.FromBase64String(parsed.Secret);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cấu hình người dùng yêu cầu Windows DPAPI.");
        var protectedSecret = ProtectedData.Protect(options.ClusterSecret, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "cluster.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new ClusterConfiguration(options.ClusterId,
            Convert.ToBase64String(protectedSecret), options.ServicePort, options.DiscoveryPort)), ct);
        File.Move(temp, path, true);
    }
    public static async Task<bool> LoadAsync(NodeOptions options, CancellationToken ct = default)
    {
        var path = Path.Combine(options.DataDirectory, "cluster.json");
        if (!File.Exists(path)) return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var value = JsonSerializer.Deserialize<ClusterConfiguration>(await File.ReadAllTextAsync(path, ct)) ?? throw new InvalidDataException("Cấu hình cluster bị hỏng.");
        options.ClusterId = value.ClusterId;
        options.ClusterSecret = ProtectedData.Unprotect(Convert.FromBase64String(value.ProtectedSecret), null, DataProtectionScope.CurrentUser);
        if (options.ClusterId == Guid.Empty || options.ClusterSecret.Length != 32) throw new InvalidDataException("Cấu hình cluster không hợp lệ.");
        return true;
    }
    public static string GetInvitation(NodeOptions options) => EncodeInvitation(new(options.ClusterId, Convert.ToBase64String(options.ClusterSecret)));
}

public static class ServiceRegistration
{
    public static IHost BuildHost(NodeOptions options)
    {
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
        builder.Services.AddSingleton<IChatStore, ChatStore>();
        builder.Services.AddSingleton<IChatService, ChatService>();
        builder.Services.AddSingleton<ILocalNetworkInfoProvider, LocalNetworkInfoProvider>();
        builder.Services.AddSingleton<NetworkHealth>();
        builder.Services.AddSingleton<WireProtocol>();
        builder.Services.AddSingleton<IPeerTransport, PeerTransport>();
        builder.Services.AddSingleton<ILanScanner, LanScanner>();
        builder.Services.AddHostedService<PeerConnectionManager>();
        builder.Services.AddHostedService<PeerDiscoveryBackgroundService>();
        builder.Services.AddHostedService<PendingMessageDeliveryService>();
        builder.Services.AddHostedService<FallbackScanBackgroundService>();
        return builder.Build();
    }
}
