using QuizNote.Application.Entities;

namespace QuizNote.Application.Dtos;

// --- Auth ---
public record RegisterRequest(string Email, string DisplayName, string Password);
public record LoginRequest(string Email, string Password);
public record AuthResponse(string Token, Guid UserId, string Email, string DisplayName);

// --- Topic ---
public record TopicDto(Guid Id, string Name, string? Description, int QuestionCount);
public record CreateTopicRequest(string Name, string? Description);

/// <summary>
/// Soru kartının yanındaki bilgi kartı için: aktif kapsamdaki (konu/favoriler/tümü)
/// toplam soru sayısı, seviye dağılımı, favori sayısı, 0-100 aralığında ortalama
/// başarı puanı.
/// </summary>
public record ScopeStatsDto(
    int TotalQuestions,
    /// <summary>Index'i seviye (0-5), değeri o seviyedeki soru sayısı olan dizi.</summary>
    IReadOnlyList<int> LevelCounts,
    int FavoriteCount,
    /// <summary>
    /// Kapsamdaki tüm soruların seviye ortalamasının yüzdeye çevrilmiş hali:
    /// (seviyelerin toplamı) / (soru sayısı * MaxLevel) * 100. Hiç soru yoksa 0.
    /// Çözülmemiş sorular başlangıç seviyesinde (0) sayılır.
    /// </summary>
    int ScorePercent);

// --- Note ---
public record NoteDto(Guid Id, Guid TopicId, string Title, string Body);
public record CreateNoteRequest(Guid TopicId, string Title, string Body);
public record UpdateNoteRequest(string Title, string Body);

// --- Question (çözerken gönderilen hali: doğru cevap bilgisi YOK) ---
public record ChoiceDto(Guid Id, string Text);
public record MatchLeftDto(Guid Id, string LeftText);
public record MatchRightDto(Guid Id, string RightText);

public record QuestionDto(
    Guid Id,
    QuestionType Type,
    string Text,
    int OrderIndex,
    Guid NoteId,
    string NoteTitle,
    IReadOnlyList<ChoiceDto> Choices,
    IReadOnlyList<MatchLeftDto> MatchLefts,
    IReadOnlyList<MatchRightDto> MatchRights,
    /// <summary>Kullanıcının bu sorudaki güncel seviyesi (0-5); yıldızlarla gösterilir.</summary>
    int Level,
    int MaxLevel,
    /// <summary>Kullanıcı bu soruyu favorilere eklemiş mi?</summary>
    bool IsFavorite,
    /// <summary>Soruya bağlı resmin göreli URL'i (örn. /uploads/images/xxx.jpg); yoksa null.</summary>
    string? ImageUrl);

/// <summary>Resim havuzundaki bir kayıt; arama/listeleme sonuçlarında kullanılır.</summary>
public record QuestionImageDto(Guid Id, string FileName, string Url, DateTime UploadedAt);

// --- Cevaplama ---
/// <summary>
/// Çoktan seçmeli için SelectedChoiceId, eşleştirme için Pairs (leftId -> rightId) doldurulur.
/// Şıklar havuzdan rastgele seçildiği için PresentedChoiceIds ile ekranda gösterilen şıklar
/// bildirilir; doğru cevap bu set içinden işaretlenir.
/// </summary>
public record SubmitAnswerRequest(
    Guid QuestionId,
    Guid? SelectedChoiceId,
    Dictionary<Guid, Guid>? Pairs,
    List<Guid>? PresentedChoiceIds);

public record AnswerResultDto(
    bool IsCorrect,
    string? Explanation,
    Guid? CorrectChoiceId,
    Dictionary<Guid, Guid>? CorrectPairs,
    NoteDto Note,
    /// <summary>Cevaptan sonraki yeni seviye; giriş yapılmamışsa null.</summary>
    int? Level,
    int? PreviousLevel,
    int MaxLevel);

// --- Admin: soru oluşturma ---
public record CreateChoiceRequest(string Text, bool IsCorrect, int OrderIndex);
public record CreateMatchPairRequest(string LeftText, string RightText, int OrderIndex);
public record CreateQuestionRequest(
    Guid TopicId,
    Guid NoteId,
    QuestionType Type,
    string Text,
    string? Explanation,
    int OrderIndex,
    List<CreateChoiceRequest>? Choices,
    List<CreateMatchPairRequest>? MatchPairs);

// --- Düzenleme ekranı: soruyu TÜM şıklarıyla (IsCorrect dahil) döner ---
/// <summary>Düzenleme ekranında gösterilen şık; havuzdaki tüm şıklar (doğru/yanlış fark etmeksizin).</summary>
public record ChoiceEditDto(Guid Id, string Text, bool IsCorrect, int OrderIndex);

public record QuestionEditDto(
    Guid Id,
    Guid TopicId,
    QuestionType Type,
    string Text,
    bool IsNegative,
    string? Explanation,
    int OrderIndex,
    Guid NoteId,
    string NoteTitle,
    string NoteBody,
    IReadOnlyList<ChoiceEditDto> Choices);

public record UpdateQuestionRequest(string Text, bool IsNegative, string? Explanation);

public record SetQuestionImageRequest(Guid ImageId);

public record CreateChoiceForQuestionRequest(string Text, bool IsCorrect);
public record UpdateChoiceRequest(string Text, bool IsCorrect);

// --- Kullanıcının arayüzden yeni soru eklemesi ---
/// <summary>
/// Kullanıcı yeni soru eklerken: konu seçilir, not her zaman yeni oluşturulur
/// (NoteTitle/NoteBody boş bırakılabilir — bu durumda soru metninden türetilen
/// bir başlık ve boş bir gövdeyle otomatik bir not açılır). Şıklarda en az bir
/// IsCorrect=true olmalıdır.
/// </summary>
public record CreateUserQuestionRequest(
    Guid TopicId,
    string? NoteTitle,
    string? NoteBody,
    string Text,
    string? Explanation,
    List<CreateChoiceForQuestionRequest> Choices);

/// <summary>Kullanıcının konular ekranında listelediği kendi eklediği sorular için özet kart.</summary>
public record MyQuestionsSummaryDto(int Count);
