using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LanChat.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var options = new NodeOptions();
            string? verificationReport = null;
            for (var i = 0; i < args.Length; i++)
            {
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Thiếu giá trị tham số.");
                switch (args[i])
                {
                    case "--data-dir": options.DataDirectory = Path.GetFullPath(Value()); break;
                    case "--port": options.ServicePort = int.Parse(Value()); options.OverrideServicePort = true; break;
                    case "--discovery-port": options.DiscoveryPort = int.Parse(Value()); options.OverrideDiscoveryPort = true; break;
                    case "--discovery-target": options.DiscoveryTargets.Add(Value()); break;
                    case "--display-name": options.InitialDisplayName = Value(); break;
#if DEBUG
                    case "--verify-ui": verificationReport = Path.GetFullPath(Value()); break;
#endif
                    default: throw new ArgumentException("Tham số không hỗ trợ: " + args[i]);
                }
            }
            ServiceRegistration.ValidateDataDirectory(options.DataDirectory);
            Directory.CreateDirectory(options.DataDirectory);
            using var dataLock = new FileStream(Path.Combine(options.DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            System.Windows.Forms.Application.Run(new StartupForm(options, verificationReport));
        }
        catch (Exception ex) { MessageBox.Show("Không khởi động được LanChat. Mỗi instance cần thư mục dữ liệu riêng.\n\n" + ex.Message, "LanChat", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal sealed class StartupForm : Form
{
    private IHost? _host;
    public StartupForm(NodeOptions options, string? verificationReport = null)
    {
        Text = "LanChat"; ClientSize = new Size(410, 100); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Text = "Đang mở dữ liệu và kết nối mạng nội bộ…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        Shown += async (_, _) =>
        {
            try
            {
                if (!await ClusterConfigurationStore.LoadAsync(options))
                {
                    using var setup = new SetupForm();
                    if (setup.ShowDialog(this) != DialogResult.OK) { Close(); return; }
                    options.InitialDisplayName = setup.DisplayName;
                    await ClusterConfigurationStore.SaveAsync(options, setup.Invitation, creator: setup.IsCreator);
                }
                if (options.TlsCertificatePfx.Length == 0 || options.BootstrapNodeId == Guid.Empty)
                {
                    using var upgrade = new ClusterUpgradeForm(options);
                    if (upgrade.ShowDialog(this) != DialogResult.OK) { Close(); return; }
                }
                _host = ServiceRegistration.BuildHost(options, services => services.AddSingleton<Controls.ChatImageService>());
                await _host.Services.GetRequiredService<IChatStore>().InitializeAsync();
                await _host.StartAsync();
                var main = ActivatorUtilities.CreateInstance<MainForm>(_host.Services);
#if DEBUG
                if (verificationReport is not null)
                    main.Shown += async (_, _) => await main.VerifyUiAsync(verificationReport);
#endif
                main.FormClosed += async (_, _) =>
                {
                    try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); await _host.StopAsync(timeout.Token); }
                    finally { _host.Dispose(); _host = null; Close(); }
                };
                main.Show(); Hide();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Không khởi động được LanChat", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (_host is not null) { await _host.StopAsync(); _host.Dispose(); _host = null; } Close();
            }
        };
    }
}
