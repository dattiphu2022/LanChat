using System.Drawing;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Xunit;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Fact]
    public Task MemberSearchPreservesHiddenUnsavedEdits() => OnUi(host =>
    {
        using var members = new GroupMembersControl(true); host.Controls.Add(members);
        var own=Guid.NewGuid(); var a=Guid.NewGuid(); var b=Guid.NewGuid();
        UserSummary Person(Guid id,string name)=>new(id,name,"Đơn vị","Máy","127.0.0.1",1,Guid.NewGuid(),0,PresenceStatus.Offline);
        var contacts=new Dictionary<Guid,UserSummary> { [a]=Person(a,"Nguyễn An"), [b]=Person(b,"Trần Bình") };
        var group=new GroupSummary(Guid.NewGuid(),"Nhóm thử",own,[own,a],false,null);
        members.Bind(group,contacts,own,new PersonName("Chủ nhóm")); members.SetActions(true,true);
        var search=Children(members).OfType<PromptTextBox>().Single(); search.Text="Binh";
        Assert.Single(members.List.Items.Cast<object>()); members.List.SetItemChecked(0,true);
        search.Text="Nguyen An"; members.List.SetItemChecked(members.List.Items.Cast<SelectionItem>().ToList().FindIndex(item=>item.Id==a),false); search.Clear();
        GroupMemberChanges? changes=null; members.SaveRequested+=value=>changes=value;
        Button(members,"Lưu thành viên").PerformClick(); Assert.NotNull(changes);
        Assert.Contains(changes!.Edits,e=>e.Key==b&&e.Value); Assert.Contains(changes.Edits,e=>e.Key==a&&!e.Value);
        Assert.Contains(Children(members).OfType<Label>(),l=>l.Text.Contains("2 thay đổi chưa lưu"));
        var output=Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if(!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); using var bitmap=new Bitmap(members.Width,members.Height); members.DrawToBitmap(bitmap,new Rectangle(Point.Empty,members.Size)); bitmap.Save(Path.Combine(output,"group-members.png")); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public Task QrPreviewCopiesExactUtf8TextAndKeepsButtonsAccessible(int dpi) => OnUi(async host =>
    {
        var original="Nguyên văn tiếng Việt\n\nDòng tiếp theo 👋";
        var copied = new List<string>();
        using var form = new QrShareForm(QrText.From([new LanChat.Domain.Message { Id=Guid.NewGuid(),Text=original,CreatedAtTicks=DateTime.UtcNow.Ticks }]),
            (_, text) => { copied.Add(text); return true; });
        form.Show(host); using var fonts=SimulateDpi(form,dpi); form.ClientSize=new Size(540*dpi/96,480*dpi/96); await Task.Delay(30);
        Button(form,"Sao chép văn bản").PerformClick();
        Assert.Contains(Children(form).OfType<Label>(),label=>label.Text.Contains("Đã sao chép nguyên văn"));
        Assert.Equal(original, Assert.Single(copied));
        foreach(var button in Children(form).OfType<Button>().Where(b=>b.Visible))
        {
            var bounds=button.RectangleToScreen(button.ClientRectangle); var visible=bounds;
            for(var parent=button.Parent;parent is not null;parent=parent.Parent) visible=Rectangle.Intersect(visible,parent.RectangleToScreen(parent.ClientRectangle));
            Assert.Equal(bounds,visible);
        }
        var output=Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if(!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); using var bitmap=new Bitmap(form.Width,form.Height); form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(Path.Combine(output,$"qr-{dpi}.png")); }
    });

    [Fact]
    public Task ClosingTransferCenterDoesNotCancelNetworkPreparationOrDuplicateWindowWork() => OnUi(async host =>
    {
        using var network=new CancellationTokenSource(); var files=new HeldPreparation(); using var actions=new AttachmentActions(files);
        var file=new AttachmentDto(Guid.NewGuid(),Guid.NewGuid(),"Báo cáo.docx","application/octet-stream",2048,new string('a',64));
        using var center=new TransferCenterForm(actions,network.Token,_=>Task.FromResult(new[] {new TransferEntry(file,"Nhóm kiểm thử")}));
        center.Show(host); await Until(()=>Children(center).OfType<DataGridView>().Single().Rows.Count==1);
        var grid=Children(center).OfType<DataGridView>().Single(); grid.CurrentCell=grid.Rows[0].Cells[0];
        Button(center,"Chuẩn bị / Thử lại").PerformClick(); await files.Started.Task;
        Assert.True(actions.IsBusy(file.Id));
        var output=Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if(!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); using var bitmap=new Bitmap(center.Width,center.Height); center.DrawToBitmap(bitmap,new Rectangle(Point.Empty,center.Size)); bitmap.Save(Path.Combine(output,"transfer-center.png")); }
        center.Close(); Assert.True(center.IsDisposed);
        Assert.False(files.Token.IsCancellationRequested); Assert.True(actions.HasActiveOperations);
        files.Finish.SetResult(true); await Until(()=>!actions.IsBusy(file.Id)); Assert.False(network.IsCancellationRequested);
    });

    private sealed class HeldPreparation : IFileTransferService
    {
        internal TaskCompletionSource<bool> Started {get;}=Signal(); internal TaskCompletionSource<bool> Finish {get;}=Signal();
        internal CancellationToken Token;
        public FileProgress? GetPreparationState(Guid id)=>Started.Task.IsCompleted&&!Finish.Task.IsCompleted?new(10,100,AttachmentPhase.Downloading):null;
        public async Task<string> PrepareOpenAsync(AttachmentDto file,IProgress<FileProgress>? progress=null,CancellationToken ct=default)
        { Token=ct; Started.TrySetResult(true); progress?.Report(new(10,100)); await Wait(Finish.Task,ct); return "prepared"; }
        public Task<LocalAttachmentLocation> GetLocalLocationAsync(AttachmentDto file,CancellationToken ct=default)=>Task.FromResult(new LocalAttachmentLocation("prepared",false,Finish.Task.IsCompleted));
        public Task<AttachmentDto> ImportLocalAsync(string path,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<AttachmentDto> ImportFolderAsync(string path,IProgress<string>? progress=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<string?> GetLocalPathAsync(string sha256,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<string> DownloadAsync(AttachmentDto file,IProgress<FileProgress>? progress=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<string> SaveLocalAsync(AttachmentDto file,string destination,bool overwrite=false,IProgress<FileProgress>? progress=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task RelinkLocalAsync(AttachmentDto file,string path,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<string> RestoreLocalAsync(AttachmentDto file,IProgress<FileProgress>? progress=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<AttachmentDto[]> PrepareForwardCopiesAsync(AttachmentDto file,int copies,IProgress<FileProgress>? progress=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<AttachmentDto> PrepareForwardAsync(AttachmentDto file,IProgress<FileProgress>? progress=null,CancellationToken ct=default)=>throw new NotSupportedException();
    }
}
