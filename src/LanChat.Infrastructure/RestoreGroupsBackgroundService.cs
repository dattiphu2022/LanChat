using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

internal sealed class RestoreGroupsBackgroundService(ChatDatabase database, ChatStore store, IConsensusService consensus,
    ILogger<RestoreGroupsBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var local = await store.GetIdentityAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pending = ReadGroups(ct);
                if (pending.Length == 0) return;
                foreach (var group in pending)
                {
                    using var db = database.Open(ct);
                    if (!db.Exists<Group>("Id=$0 AND OwnerUserId=$1", group.Id, local.UserId))
                    {
                        var result = await consensus.ProposeAsync(new(AppIdentity.NewId(), ControlCommandType.GroupCreated,
                            local.UserId, group.Id, group.Name, Members: group.Members), ct);
                        if (!result.Success) continue;
                    }
                    await store.WriteAsync(tx => Task.FromResult(tx.Execute("DELETE FROM GroupsToRestore WHERE GroupId=$0", group.Id)), ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { logger.LogWarning(ex, "Không thể khôi phục nhóm sau khi nâng cấp, sẽ thử lại."); }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private (Guid Id, string Name, Guid[] Members)[] ReadGroups(CancellationToken ct)
    {
        using var db = database.Open(ct);
        using var command = db.Command("SELECT GroupId,Name,MembersJson FROM GroupsToRestore ORDER BY GroupId");
        using var reader = command.ExecuteReader();
        var groups = new List<(Guid, string, Guid[])>();
        while (reader.Read()) groups.Add((reader.GetGuid(0), reader.GetString(1), JsonSerializer.Deserialize<Guid[]>(reader.GetString(2))!));
        return groups.ToArray();
    }
}
