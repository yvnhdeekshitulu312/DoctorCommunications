// DoctorCommunicationsDbContext.cs
using Microsoft.EntityFrameworkCore;

public class DoctorCommunicationsDbContext : DbContext
{
    public DoctorCommunicationsDbContext(DbContextOptions<DoctorCommunicationsDbContext> options) : base(options) { }

    public DbSet<ChatConversationEntity> Conversations => Set<ChatConversationEntity>();
    public DbSet<ChatParticipantEntity> ConversationParticipants => Set<ChatParticipantEntity>();
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatConversationEntity>(e =>
        {
            e.ToTable("DoctorCommunications_Conversations");
            e.HasKey(c => c.Id);
        });

        modelBuilder.Entity<ChatParticipantEntity>(e =>
        {
            e.ToTable("DoctorCommunications_ConversationParticipants");
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.ConversationId, p.UserId }).IsUnique();
            e.HasIndex(p => new { p.UserId, p.Status });
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);

            e.HasOne(p => p.Conversation)
                .WithMany(c => c.Participants)
                .HasForeignKey(p => p.ConversationId);
        });

        modelBuilder.Entity<ChatMessageEntity>(e =>
        {
            e.ToTable("DoctorCommunications_ChatMessages");
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ConversationId, m.SentAtUtc });

            e.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId);
        });
    }
}
