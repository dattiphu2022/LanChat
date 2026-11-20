using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

// Reuses the existing profile, invitation and scanning screens; no second network workflow.
internal sealed class GettingStartedForm : DpiForm
{
    internal GettingStartedForm(Func<string> profile, Func<bool> hasNetwork, Func<Task> settings, Func<Task> networks, Func<Task> scan)
    {
        Text="Bắt đầu với LanChat"; Icon=AppIcon.Value; ClientSize=new Size(570,430); MinimumSize=new Size(420,330);
        StartPosition=FormStartPosition.CenterParent; AutoScroll=true;
        var root=Theme.Stack(7); root.Dock=DockStyle.Top; root.AutoSize=true; root.AutoSizeMode=AutoSizeMode.GrowAndShrink; root.Padding=new Padding(20);
        for(var i=0;i<7;i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title=Theme.Label("Trò chuyện trong mạng nội bộ",16,true); UiInteraction.Wrap(title,root); root.Controls.Add(title,0,0);
        var hint=Theme.Label("Thiết lập hồ sơ, tạo hoặc tham gia mạng rồi mở Danh bạ để bắt đầu. Bạn có thể hoàn tất sau trong Cài đặt và Mạng chat.");
        UiInteraction.Wrap(hint,root); root.Controls.Add(hint,0,1);
        var state=Theme.Label(""); UiInteraction.Wrap(state,root); state.AccessibleRole=AccessibleRole.StatusBar;
        var identify=Theme.Secondary("1. Nhập tên và đơn vị…",UiIcon.Contacts);
        var connect=Theme.Secondary("2. Tạo / Tham gia mạng…",UiIcon.Network);
        var check=Theme.Secondary("3. Kiểm tra kết nối",UiIcon.Scan);
        foreach(var button in new[]{identify,connect,check}) { button.Dock=DockStyle.Top; button.TextAlign=ContentAlignment.MiddleLeft; }
        root.Controls.Add(identify,0,2); root.Controls.Add(connect,0,3); root.Controls.Add(check,0,4); root.Controls.Add(state,0,5);
        var footer=Theme.Toolbar(); var done=Theme.Button("Bắt đầu trò chuyện",UiIcon.Chat); done.DialogResult=DialogResult.OK;
        var later=UiInteraction.CloseButton(this,"Để sau"); footer.Controls.Add(done); footer.Controls.Add(later); root.Controls.Add(footer,0,6); Controls.Add(root);
        var busy=false;
        void RefreshState() { check.Enabled=hasNetwork(); state.Text="Hồ sơ: "+profile()+"\n"+(hasNetwork()?"Đã mở mạng. Mở Danh bạ để chọn người trò chuyện; quét lại nếu chưa thấy máy khác.":"Chưa mở mạng. Nếu tham gia, nhập mã mời trong Mạng chat → Thêm mã."); }
        async Task RunAsync(Func<Task> action)
        {
            if(busy) return; busy=true; root.Enabled=false;
            try { await action(); RefreshState(); }
            catch(Exception ex) { state.Text=UiText.HideIpAddresses(ex.Message); }
            finally { busy=false; if(!IsDisposed) root.Enabled=true; }
        }
        identify.Click+=async(_,_)=>await RunAsync(settings); connect.Click+=async(_,_)=>await RunAsync(networks); check.Click+=async(_,_)=>await RunAsync(scan);
        FormClosing+=(_,e)=>{ if(busy)e.Cancel=true; }; Shown+=(_,_)=>RefreshState();
    }
}
