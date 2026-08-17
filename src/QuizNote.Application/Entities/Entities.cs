namespace QuizNote.Application.Entities;

public enum QuestionType
{
    MultipleChoice = 0,
    Matching = 1,
    TrueFalse = 2
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserQuestionLevel> QuestionLevels { get; set; } = new List<UserQuestionLevel>();
    public ICollection<FavoriteQuestion> Favorites { get; set; } = new List<FavoriteQuestion>();
}

/// <summary>Bir konu başlığı; sorular ve notlar bunun altında gruplanır.</summary>
public class Topic
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Note> Notes { get; set; } = new List<Note>();
    public ICollection<Question> Questions { get; set; } = new List<Question>();
}

/// <summary>Soruya bağlanan not parçası. Body markdown olarak tutulur.</summary>
public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TopicId { get; set; }
    public Topic Topic { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string Body { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Question> Questions { get; set; } = new List<Question>();
}

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TopicId { get; set; }
    public Topic Topic { get; set; } = null!;

    /// <summary>Her sorunun ilgili not parçası. Zorunlu — projenin çekirdek fikri bu bağ.</summary>
    public Guid NoteId { get; set; }
    public Note Note { get; set; } = null!;

    public QuestionType Type { get; set; }
    public string Text { get; set; } = null!;

    /// <summary>Soruya eklenmiş tekil resim; opsiyoneldir. Havuzdaki mevcut bir
    /// resim başka bir soruya da bağlanabilir (aynı QuestionImage birden fazla
    /// soru tarafından referans alınabilir).</summary>
    public Guid? ImageId { get; set; }
    public QuestionImage? Image { get; set; }

    /// <summary>
    /// Ters soru ("hangisi söylenemez / yanlıştır") işareti. Havuzdan şık çekilirken
    /// seçim tersine döner: 1 yanlış + 4 doğru sunulur ve aranan cevap yanlış olandır.
    /// </summary>
    public bool IsNegative { get; set; }

    /// <summary>Cevaptan sonra gösterilen kısa açıklama; tam not değil.</summary>
    public string? Explanation { get; set; }
    public int OrderIndex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soruyu ekleyen kullanıcı. Seed verisindeki sorularda null'dır; bir kullanıcı
    /// arayüzden yeni soru eklediğinde kendi Id'si yazılır. "Kendi Sorularım" kartı
    /// ve düzenleme ekranındaki köken bilgisi bunu kullanır.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public ICollection<Choice> Choices { get; set; } = new List<Choice>();
    public ICollection<MatchPair> MatchPairs { get; set; } = new List<MatchPair>();
}

/// <summary>Çoktan seçmeli / doğru-yanlış sorularının şıkları.</summary>
public class Choice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public string Text { get; set; } = null!;
    public bool IsCorrect { get; set; }
    public int OrderIndex { get; set; }
}

/// <summary>Eşleştirme sorularının sol-sağ çiftleri.</summary>
public class MatchPair
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public string LeftText { get; set; } = null!;
    public string RightText { get; set; } = null!;
    public int OrderIndex { get; set; }
}

/// <summary>
/// Bir kullanıcının tek bir sorudaki ustalık seviyesi. Doğru cevapta +1, yanlışta -2;
/// <see cref="MinLevel"/> ile <see cref="MaxLevel"/> arasında sıkıştırılır.
/// Seviye düşük olan sorular "zorlanılan" sayılır ve daha sık sorulur.
/// </summary>
public class UserQuestionLevel
{
    public const int MinLevel = 0;
    public const int MaxLevel = 5;
    public const int StartLevel = 0;

    public const int CorrectStep = 1;
    public const int WrongStep = -2;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public int Level { get; set; } = StartLevel;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cevaba göre seviyeyi günceller ve izin verilen aralıkta tutar.</summary>
    public void Apply(bool isCorrect)
    {
        var next = Level + (isCorrect ? CorrectStep : WrongStep);
        Level = Math.Clamp(next, MinLevel, MaxLevel);
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Yüklenmiş bir resim dosyası. Global bir havuzdur; aynı resim birden fazla
/// soruya bağlanabilir. Diskteki gerçek dosya adı çakışmayı önlemek için
/// benzersiz üretilir; kullanıcının gördüğü/aradığı isim ayrıca tutulur.
/// </summary>
public class QuestionImage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Kullanıcının yüklerken verdiği/gördüğü orijinal dosya adı; arama bunun üzerinden yapılır.</summary>
    public string FileName { get; set; } = null!;

    /// <summary>wwwroot/uploads/images altındaki gerçek dosya adı (GUID tabanlı, benzersiz).</summary>
    public string StoredFileName { get; set; } = null!;

    public string ContentType { get; set; } = null!;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Kullanıcının favori olarak işaretlediği soru.</summary>
public class FavoriteQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

