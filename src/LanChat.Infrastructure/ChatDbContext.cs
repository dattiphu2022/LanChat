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
    }
}

public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ChatDbContext>()
        .UseSqlite("Data Source=lanchat-design.db").Options);
}
