namespace LanChat.Application;

public sealed record MessageMention(int Start, int Length, bool All, Guid[] UserIds)
{ public bool ResolveAtSend { get; init; } }

public static class Mentions
{
    public static MessageMention[] Normalize(string text, MessageMention[]? mentions, Guid[] audience, bool group, bool trim = false, bool resolveAll = true)
    {
        // New @All resolves at commit; editing an existing range keeps its frozen audience.
        if (trim && mentions is not null) mentions = mentions.Select(m => m.All ? m with { UserIds = resolveAll || m.ResolveAtSend ? audience.ToArray() : (m.UserIds ?? []).Intersect(audience).ToArray() } : m).ToArray();
        var leading = trim ? text.Length - text.TrimStart().Length : 0;
        var length = trim ? text.Trim().Length : text.Length;
        var result = new List<MessageMention>();
        foreach (var mention in (mentions ?? []).OrderBy(x => x.Start))
        {
            if (!group || mention.Start < leading || mention.Length < 2 || mention.Start > text.Length - mention.Length ||
                mention.Start - leading > length - mention.Length || text[mention.Start] != '@' ||
                mention.UserIds is null || mention.UserIds.Length == 0 || mention.UserIds.Length > 1000 ||
                mention.UserIds.Any(id => id == Guid.Empty || !audience.Contains(id)) || (!mention.All && mention.UserIds.Distinct().Count() != 1) ||
                (mention.All && text.Substring(mention.Start, mention.Length) != "@All") ||
                (result.Count > 0 && result[result.Count - 1].Start + result[result.Count - 1].Length > mention.Start - leading))
                throw new ArgumentException("Lời nhắc không hợp lệ hoặc thành viên đã rời nhóm. Hãy chọn lại tên từ danh sách gợi ý.");
            result.Add(mention with { Start = mention.Start - leading, UserIds = mention.UserIds.Distinct().ToArray(), ResolveAtSend = false });
        }
        return result.ToArray();
    }
}
