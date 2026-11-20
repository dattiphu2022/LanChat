using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LanChat.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        VerifiedInstaller? pending;
        try { pending = Run(args); }
        catch (Exception ex)
        {
            MessageBox.Show(UiText.HideIpAddresses(ex.Message), "Không khởi động được LanChat", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using (pending)
        {
            if (pending is null) return;
            try
            {
                using var installer = Process.Start(new ProcessStartInfo(pending.Path)
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(pending.Path) });
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText.HideIpAddresses(ex.Message), "Không mở được bộ cài LanChat", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private static VerifiedInstaller? Run(string[] args)
    {
        var options = new NodeOptions();
        string? verification = null;
#if DEBUG
        string? verificationUpdate = null;
        var verificationLegacy = false;
        var verificationLegacyMentions = false;
#endif
        var startup = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Thiếu giá trị tham số.");
            switch (args[i])
            {
                case "--startup": startup = true; break;
                case "--data-dir": options.DataDirectory = Path.GetFullPath(Value()); break;
                case "--port": options.ServicePort = int.Parse(Value()); options.OverrideServicePort = true; break;
                case "--discovery-port": options.DiscoveryPort = int.Parse(Value()); options.OverrideDiscoveryPort = true; break;
                case "--discovery-target": options.DiscoveryTargets.Add(Value()); break;
                case "--display-name": options.InitialDisplayName = Value(); break;
#if DEBUG
                case "--verify-ui": verification = Path.GetFullPath(Value()); break;
                case "--verify-update-package": verificationUpdate = Path.GetFullPath(Value()); break;
                case "--verify-legacy-peer": verificationLegacy = true; break;
                case "--verify-legacy-mentions": verificationLegacyMentions = true; break;
#endif
                default: throw new ArgumentException("Tham số không hỗ trợ: " + args[i]);
            }
        }

        // Release builds share a single process slot across Windows sessions and data directories.
        var acquired = true;
        using var instance = verification is null ? AcquireInstance(out acquired) : null;
        if (instance is not null && !acquired)
        {
            if (!startup) MessageBox.Show("LanChat đang chạy. Hãy mở cửa sổ hiện tại từ khay hệ thống.", "LanChat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        ServiceRegistration.ValidateDataDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        using var dataLock = new FileStream(Path.Combine(options.DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
#if DEBUG
        if (verification is not null)
        {
            System.Windows.Forms.Application.Run(new VerificationStartup(options, verification, verificationUpdate, verificationLegacy, verificationLegacyMentions));
            return null;
        }
#endif
        using var updates = new UpdateService(options.DataDirectory);
        using var window = new NetworkWorkspace(new NetworkCatalog(options, updates.Packages), new WindowsStartup(options.DataDirectory), updates);
        window.Shown += async (_, _) =>
        {
            try { await window.StartAsync(); }
            catch (OperationCanceledException) { window.ExitApplication(); }
            catch (Exception ex)
            {
                MessageBox.Show(window, UiText.HideIpAddresses(ex.Message), "Không khởi động được LanChat", MessageBoxButtons.OK, MessageBoxIcon.Error);
                window.ExitApplication();
            }
        };
        System.Windows.Forms.Application.Run(window);
        return window.PendingInstaller;
    }

    private static Mutex AcquireInstance(out bool acquired)
    {
        const string name = @"Global\LanChat.WinForms.3D1D7FFD9E1C47B499752218E7C71FF3";
        var security = new MutexSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            MutexRights.Synchronize, AccessControlType.Allow));
        while (true)
        {
            try { return new Mutex(false, name, out acquired, security); }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    var existing = Mutex.OpenExisting(name, MutexRights.Synchronize);
                    acquired = false;
                    return existing;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The other process can close the mutex between create and open.
                    Thread.Sleep(1);
                }
            }
        }
    }
}

#if DEBUG
internal sealed class VerificationStartup : Form
{
    public VerificationStartup(NodeOptions options, string report, string? updatePackage, bool legacyPeer, bool legacyMentions)
    {
        Shown += async (_, _) =>
        {
            try
            {
                var updates = updatePackage is null ? null : new UpdateService(options.DataDirectory);
                var catalog = new NetworkCatalog(options, updates?.Packages);
                await catalog.LoadAsync();
                if (updates is not null)
                {
                    var package = await updates.Packages.ImportAsync(updatePackage!);
                    await updates.ConfigureAsync(new(UpdateSourceKind.Local, Package: package, Sharing: true,
                        Networks: [catalog.Entries.Single(e => e.Primary).Id]), default);
                }
                var network = await catalog.StartAsync(catalog.Entries.Single(e => e.Primary), false, this);
                var main = ActivatorUtilities.CreateInstance<MainForm>(network.Services);
                main.Shown += async (_, _) => await main.VerifyUiAsync(report, updates, legacyPeer, legacyMentions);
                main.FormClosed += async (_, _) => { try { await network.StopAsync(); } finally { updates?.Dispose(); Close(); } };
                main.Show(); Hide();
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                await FrameworkIO.WriteTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new { Success = false, Error = ex.ToString() }));
                Close();
            }
        };
    }
}
#endif
