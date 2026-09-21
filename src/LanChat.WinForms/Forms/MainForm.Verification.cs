#if DEBUG
using System.Drawing.Imaging;
using System.Text.Json;
using LanChat.Application;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm
{
    // Explicit debug-only integration harness: uses two separate processes and isolated data directories.
    internal async Task VerifyUiAsync(string reportPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var captures = new List<string>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            IReadOnlyList<UserSummary> contacts;
            do { await Task.Delay(200, deadline.Token); contacts = await _store.GetUsersAsync(ct: deadline.Token); } while (contacts.Count == 0);
            var other = contacts[0]; var profile = await _store.GetProfileAsync(deadline.Token);
            var conversation = await _store.OpenDirectAsync(other.UserId, deadline.Token);
            await SelectConversationAsync(conversation, other.UserId);
            var text = $"Xin chào từ {profile.DisplayName} 👋\nTin nhắn nhiều dòng qua TCP, không có server riêng.";
            await _chat.SubmitForVerificationAsync(text);
            IReadOnlyList<Domain.Message> messages;
            do
            {
                await Task.Delay(200, deadline.Token); messages = await _store.GetMessagesAsync(conversation, ct: deadline.Token);
            } while (messages.Count < 2 || await _store.GetPendingCountAsync(deadline.Token) != 0);
            foreach (var size in new[] { new Size(1240, 800), new Size(1020, 650) })
            {
                ClientSize = size; await RefreshAsync(); await Task.Delay(350, deadline.Token);
                var path = Path.ChangeExtension(reportPath, $"{size.Width}.png"); CaptureForm(this, path); captures.Add(path);
                static object Describe(Control c) => new { Type = c.GetType().Name, Bounds = c.Bounds.ToString(), Minimum = c.MinimumSize.ToString(), c.AutoSize, Dock = c.Dock.ToString(), Children = c.Controls.Cast<Control>().Select(Describe).ToArray() };
                await File.WriteAllTextAsync(Path.ChangeExtension(reportPath, $"{size.Width}.layout.json"), JsonSerializer.Serialize(Describe(this), new JsonSerializerOptions { WriteIndented = true }));
                VerifyVisibleControls();
            }
            using (var settings = ActivatorUtilities.CreateInstance<SettingsForm>(_services))
            {
                settings.Show(this); await Task.Delay(350, deadline.Token);
                var path = Path.ChangeExtension(reportPath, "settings.png"); CaptureForm(settings, path); captures.Add(path); settings.Close();
            }
            using (var diagnostics = ActivatorUtilities.CreateInstance<ClusterDiagnosticsForm>(_services))
            {
                diagnostics.Show(this); await Task.Delay(350, deadline.Token);
                var path = Path.ChangeExtension(reportPath, "diagnostics.png"); CaptureForm(diagnostics, path); captures.Add(path); diagnostics.Close();
            }
            var identity = await _store.GetIdentityAsync(deadline.Token);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Success = true, identity.NodeId, identity.UserId,
                Messages = messages.Select(x => new { x.Id, x.Text, x.Status }).ToArray(), Captures = captures }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Success = false, Error = ex.ToString(), Captures = captures }));
        }
        finally { Close(); }
    }
    private void VerifyVisibleControls()
    {
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => Descendants(c).Prepend(c));
        var controls = Descendants(this).ToArray();
        if (controls.OfType<MessageBubbleControl>().Count() < 2) throw new InvalidOperationException("UI chưa render đủ tin nhắn.");
        foreach (var control in controls.Where(c => c is NavigationItemControl or MessageBubbleControl or ChatInputControl))
        {
            var original = control.RectangleToScreen(control.ClientRectangle); var visible = original;
            for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            if (!control.Visible || original.Width < 80 || original.Height < 25 || visible.Width < original.Width * 0.9 || visible.Height < original.Height * 0.9)
                throw new InvalidOperationException($"Control bị khuất: {control.GetType().Name}, bounds={original}, visible={visible}");
        }
    }
    private static void CaptureForm(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, form.ClientRectangle with { Width = form.Width, Height = form.Height });
        bitmap.Save(path, ImageFormat.Png);
    }
}
#endif
