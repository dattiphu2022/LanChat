using System.Globalization;

namespace LanChat.WinForms.Controls;

internal sealed class SearchResultListBox : ListBox
{
    internal Func<object,(string Heading,string Text)> Describe { get; set; } = item => ("",item.ToString() ?? "");
    internal string Query { get; set; } = "";
    internal SearchResultListBox()
    {
        DrawMode=DrawMode.OwnerDrawFixed; IntegralHeight=false; HorizontalScrollbar=false;
        BorderStyle=BorderStyle.None; BackColor=Color.White; ForeColor=Theme.Ink;
        UpdateHeight();
    }
    private void UpdateHeight() => ItemHeight=Math.Max(this.ScalePixels(48),Font.Height*2+this.ScalePixels(10));
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateHeight(); }
    internal static int KeywordIndex(string text, string keyword) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(
        text.Replace('đ','d').Replace('Đ','D'), keyword.Replace('đ','d').Replace('Đ','D'), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if(e.Index<0 || e.Index>=Items.Count) return;
        var selected=(e.State&DrawItemState.Selected)!=0;
        using(var background=new SolidBrush(selected?Theme.Selection:Color.White)) e.Graphics.FillRectangle(background,e.Bounds);
        var (heading,text)=Describe(Items[e.Index]); text=text.Replace('\r',' ').Replace('\n',' ');
        var tokens=Query.Split(new[]{' ','"'},StringSplitOptions.RemoveEmptyEntries);
        var first=tokens.Select(t=>KeywordIndex(text,t)).Where(i=>i>=0).DefaultIfEmpty(-1).Min();
        if(first>45) text="…"+text.Substring(first-35);
        var inset=this.ScalePixels(6); var headingBox=new Rectangle(e.Bounds.X+inset,e.Bounds.Y+2,e.Bounds.Width-inset*2,Font.Height+2);
        var bodyBox=new Rectangle(headingBox.X,headingBox.Bottom,e.Bounds.Width-inset*2,Font.Height+3);
        var flags=TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics,heading,Font,headingBox,Theme.Muted,flags);
        TextRenderer.DrawText(e.Graphics,text,Font,bodyBox,Theme.Ink,flags);
        foreach(var token in tokens.Take(12))
        {
            var start=KeywordIndex(text,token); if(start<0 || start+token.Length>text.Length) continue;
            var prefix=TextRenderer.MeasureText(e.Graphics,text.Substring(0,start),Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
            var match=text.Substring(start,token.Length);
            var width=TextRenderer.MeasureText(e.Graphics,match,Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width;
            var area=new Rectangle(bodyBox.X+prefix,bodyBox.Y,width,bodyBox.Height);
            if(area.Right>bodyBox.Right-this.ScalePixels(16)) continue;
            using var marker=new SolidBrush(Color.FromArgb(255,235,160)); e.Graphics.FillRectangle(marker,area);
            TextRenderer.DrawText(e.Graphics,match,Font,area,Theme.Ink,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
        }
        e.DrawFocusRectangle();
    }
}
