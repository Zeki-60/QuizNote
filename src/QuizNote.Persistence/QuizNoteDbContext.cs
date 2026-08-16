using Microsoft.EntityFrameworkCore;
using QuizNote.Application.Entities;

namespace QuizNote.Persistence;

public class QuizNoteDbContext(DbContextOptions<QuizNoteDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Choice> Choices => Set<Choice>();
    public DbSet<MatchPair> MatchPairs => Set<MatchPair>();
    public DbSet<UserQuestionLevel> UserQuestionLevels => Set<UserQuestionLevel>();
    public DbSet<FavoriteQuestion> FavoriteQuestions => Set<FavoriteQuestion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
        });

        b.Entity<Topic>(e =>
        {
            e.ToTable("topics");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Note>(e =>
        {
            e.ToTable("notes");
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).IsRequired();
            e.HasOne(x => x.Topic).WithMany(t => t.Notes)
                .HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Question>(e =>
        {
            e.ToTable("questions");
            e.Property(x => x.Text).IsRequired();
            e.HasOne(x => x.Topic).WithMany(t => t.Questions)
                .HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
            // Not silinince ona bağlı sorular da gitsin — notsuz soru bu projede anlamsız.
            e.HasOne(x => x.Note).WithMany(n => n.Questions)
                .HasForeignKey(x => x.NoteId).OnDelete(DeleteBehavior.Cascade);
            // Ekleyen kullanıcı silinirse soru silinmesin; sadece köken bilgisi kaybolsun.
            e.HasOne(x => x.CreatedByUser).WithMany()
                .HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Choice>(e =>
        {
            e.ToTable("choices");
            e.Property(x => x.Text).IsRequired();
            e.HasOne(x => x.Question).WithMany(q => q.Choices)
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MatchPair>(e =>
        {
            e.ToTable("match_pairs");
            e.Property(x => x.LeftText).IsRequired();
            e.Property(x => x.RightText).IsRequired();
            e.HasOne(x => x.Question).WithMany(q => q.MatchPairs)
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserQuestionLevel>(e =>
        {
            e.ToTable("user_question_levels");
            e.HasOne(x => x.User).WithMany(u => u.QuestionLevels)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Question).WithMany()
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            // Kullanıcı-soru çifti başına tek satır; ağırlıklandırma buna dayanıyor.
            e.HasIndex(x => new { x.UserId, x.QuestionId }).IsUnique();
        });

        b.Entity<FavoriteQuestion>(e =>
        {
            e.ToTable("favorite_questions");
            e.HasOne(x => x.User).WithMany(u => u.Favorites)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Question).WithMany()
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            // Aynı soru bir kullanıcıda yalnızca bir kez favori olabilir.
            e.HasIndex(x => new { x.UserId, x.QuestionId }).IsUnique();
        });

    }
}
