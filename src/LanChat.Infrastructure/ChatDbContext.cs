using LanChat.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LanChat.Infrastructure;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<LocalUserProfile> Users => Set<LocalUserProfile>();
    public DbSet<PeerNode> Peers => Set<PeerNode>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> Members => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<LocalSettings> Settings => Set<LocalSettings>();
    public DbSet<ReplicationEvent> Events => Set<ReplicationEvent>();
    public DbSet<ReplicationCursor> Cursors => Set<ReplicationCursor>();
    public DbSet<OutboxItem> Outbox => Set<OutboxItem>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<MessageReaction> Reactions => Set<MessageReaction>();
    public DbSet<MessageReceipt> Receipts => Set<MessageReceipt>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ClusterState> Cluster => Set<ClusterState>();
    public DbSet<ControlLogEntry> ControlLog => Set<ControlLogEntry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LocalUserProfile>().ToTable("Users").HasKey(x => x.Id);
        model.Entity<PeerNode>().ToTable("PeerNodes").HasIndex(x => x.NodeId).IsUnique();
        model.Entity<Conversation>().ToTable("Conversations");
        model.Entity<ConversationMember>().ToTable("ConversationMembers").HasIndex(x => new { x.ConversationId, x.UserId }).IsUnique();
        model.Entity<ConversationMember>().HasIndex(x => x.UserId);
        model.Entity<Message>().ToTable("Messages").HasIndex(x => x.ClientMessageId).IsUnique();
        model.Entity<Message>().HasIndex(x => new { x.OriginNodeId, x.OriginSequence }).IsUnique();
        model.Entity<Message>().HasIndex(x => new { x.ConversationId, x.PhysicalTicks, x.LogicalCounter, x.OriginNodeId, x.Id });
        model.Entity<Message>().HasIndex(x => x.CreatedAtTicks);
        model.Entity<Message>().HasIndex(x => new { x.Status, x.NextRetryTicks });
        model.Entity<LocalSettings>().ToTable("LocalSettings");
        model.Entity<ReplicationEvent>().ToTable("ReplicationEvents").HasIndex(x => new { x.OriginNodeId, x.OriginSequence }).IsUnique();
        model.Entity<ReplicationEvent>().HasIndex(x => new { x.EntityId, x.PhysicalTicks, x.LogicalCounter });
        model.Entity<ReplicationCursor>().ToTable("ReplicationCursors").HasIndex(x => x.OriginNodeId).IsUnique();
        model.Entity<OutboxItem>().ToTable("OutboxItems").HasIndex(x => new { x.Status, x.NextRetryTicks });
        model.Entity<OutboxItem>().HasIndex(x => x.ReplicationEventId).IsUnique();
        model.Entity<Group>().ToTable("Groups");
        model.Entity<GroupMember>().ToTable("GroupMembers").HasIndex(x => new { x.GroupId, x.UserId }).IsUnique();
        model.Entity<MessageReaction>().ToTable("MessageReactions").HasIndex(x => new { x.MessageId, x.UserId, x.Emoji }).IsUnique();
        model.Entity<MessageReceipt>().ToTable("MessageReceipts").HasIndex(x => new { x.MessageId, x.UserId }).IsUnique();
        model.Entity<Attachment>().ToTable("Attachments").HasIndex(x => x.Sha256);
        model.Entity<Attachment>().HasIndex(x => x.MessageId);
        model.Entity<ClusterState>().ToTable("ClusterState");
        model.Entity<ControlLogEntry>().ToTable("ControlLog").HasIndex(x => x.Index).IsUnique();
    }
}

public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ChatDbContext>()
        .UseSqlite("Data Source=lanchat-design.db").Options);
}
