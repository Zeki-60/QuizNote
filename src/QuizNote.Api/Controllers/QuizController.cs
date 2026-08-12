using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizNote.Application.Dtos;
using QuizNote.Application.Entities;
using QuizNote.Persistence;

namespace QuizNote.Api.Controllers;

[ApiController]
[Route("api")]
public class QuizController(QuizNoteDbContext db) : ControllerBase
{
    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    [HttpGet("topics")]
    public async Task<ActionResult<IEnumerable<TopicDto>>> GetTopics(CancellationToken ct)
    {
        var topics = await db.Topics
            .OrderBy(t => t.Name)
            .Select(t => new TopicDto(t.Id, t.Name, t.Description, t.Questions.Count))
            .ToListAsync(ct);

        return Ok(topics);
    }

    /// <summary>
    /// Sonsuz akış için tek bir soru döndürür; havuzdan rastgele seçilir.
    /// <paramref name="topicId"/> verilmezse tüm konuların soruları havuza girer.
    /// <paramref name="prioritizeHard"/> açıkken seçim seviyeye göre ağırlıklandırılır:
    /// seviyesi düşük (zorlanılan) sorular daha sık gelir.
    /// <paramref name="excludeIds"/> son gösterilen soruları dışlar; aynı soru üst üste gelmez.
    /// <paramref name="favoritesOnly"/> açıkken yalnızca favori sorulardan seçilir.
    /// <paramref name="inactiveOnly"/> açıkken yalnızca pasif (aktif olmayan) işaretli
    /// sorulardan seçilir; kapalıyken (normal akış) kullanıcının pasif işaretlediği
    /// sorular havuza hiç girmez.
    /// </summary>
    [HttpGet("next-question")]
    public async Task<ActionResult<QuestionDto>> GetNextQuestion(
        [FromQuery] Guid? topicId,
        [FromQuery] bool prioritizeHard,
        [FromQuery] bool favoritesOnly,
        [FromQuery] bool inactiveOnly,
        [FromQuery] string? excludeIds,
        CancellationToken ct)
    {
        if (topicId is not null && !await db.Topics.AnyAsync(t => t.Id == topicId, ct))
            return NotFound(new { message = "Konu bulunamadı." });

        var userId = CurrentUserId;

        var query = db.Questions.AsQueryable();

        if (topicId is not null)
            query = query.Where(q => q.TopicId == topicId);

        if (favoritesOnly)
        {
            if (userId is null)
                return Unauthorized(new { message = "Favoriler için giriş yapmalısınız." });

            query = query.Where(q => db.FavoriteQuestions
                .Any(f => f.UserId == userId && f.QuestionId == q.Id));
        }

        if (inactiveOnly)
        {
            if (userId is null)
                return Unauthorized(new { message = "Aktif olmayanlar için giriş yapmalısınız." });

            query = query.Where(q => db.InactiveQuestions
                .Any(i => i.UserId == userId && i.QuestionId == q.Id));
        }
        else if (userId is not null)
        {
            // Normal akışta (Tümü / konu / favoriler) kullanıcının pasife aldığı
            // sorular havuza hiç girmez.
            query = query.Where(q => !db.InactiveQuestions
                .Any(i => i.UserId == userId && i.QuestionId == q.Id));
        }

        var questions = await query
            .Include(q => q.Choices)
            .Include(q => q.MatchPairs)
            .Include(q => q.Note)
            .ToListAsync(ct);

        if (questions.Count == 0)
            return NotFound(new
            {
                message = inactiveOnly
                    ? "Aktif olmayan sorunuz yok."
                    : favoritesOnly
                        ? "Favori sorunuz yok. Sorulardaki kalp simgesine dokunarak ekleyebilirsiniz."
                        : "Soru bulunamadı."
            });

        // Son gösterilen soruları dışla; havuz tükenirse dışlama kaldırılır.
        var excluded = ParseIds(excludeIds);
        var pool = questions.Where(q => !excluded.Contains(q.Id)).ToList();
        if (pool.Count == 0) pool = questions;

        // Seviyeler havuzdaki sorulara göre çekilir; konu filtresinden bağımsızdır.
        var poolIds = pool.Select(q => q.Id).ToList();
        var levels = userId is null
            ? new Dictionary<Guid, int>()
            : await db.UserQuestionLevels
                .Where(l => l.UserId == userId && poolIds.Contains(l.QuestionId))
                .ToDictionaryAsync(l => l.QuestionId, l => l.Level, ct);

        int LevelOf(Question q) => levels.GetValueOrDefault(q.Id, UserQuestionLevel.StartLevel);

        // Seçim: normalde düz rastgele. "Zorlandıklarımı sık sor" açıkken ağırlıklı
        // seçim yapılır; seviyesi düşük sorunun seçilme şansı yüksektir.
        var picked = prioritizeHard && userId is not null
            ? pool.MinBy(q => WeightedSortKey(LevelOf(q)))!
            : pool[Random.Shared.Next(pool.Count)];

        var isFavorite = userId is not null && await db.FavoriteQuestions
            .AnyAsync(f => f.UserId == userId && f.QuestionId == picked.Id, ct);
        var isInactive = userId is not null && await db.InactiveQuestions
            .AnyAsync(i => i.UserId == userId && i.QuestionId == picked.Id, ct);

        return Ok(ToDto(picked, LevelOf(picked), isFavorite, isInactive));
    }

    /// <summary>Virgülle ayrılmış GUID listesini ayrıştırır; geçersizleri yok sayar.</summary>
    private static HashSet<Guid> ParseIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
    }

    private static QuestionDto ToDto(Question q, int level, bool isFavorite, bool isInactive) => new(
        q.Id,
        q.Type,
        q.Text,
        // Sonsuz akışta sıra numarası anlamsız; alan uyumluluk için 0 kalır.
        0,
        q.NoteId,
        q.Note.Title,
        BuildChoices(q),
        q.MatchPairs.OrderBy(m => m.OrderIndex).Select(m => new MatchLeftDto(m.Id, m.LeftText)).ToList(),
        // Sağ taraf karıştırılır, yoksa eşleştirme sırayla çözülebilir olurdu.
        q.MatchPairs.OrderBy(_ => Guid.NewGuid()).Select(m => new MatchRightDto(m.Id, m.RightText)).ToList(),
        level,
        UserQuestionLevel.MaxLevel,
        isFavorite,
        isInactive);

    /// <summary>Favori durumunu tersine çevirir ve yeni durumu döndürür.</summary>
    [Authorize]
    [HttpPost("questions/{questionId:guid}/favorite")]
    public async Task<ActionResult> ToggleFavorite(Guid questionId, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        if (!await db.Questions.AnyAsync(q => q.Id == questionId, ct))
            return NotFound(new { message = "Soru bulunamadı." });

        var existing = await db.FavoriteQuestions
            .FirstOrDefaultAsync(f => f.UserId == userId && f.QuestionId == questionId, ct);

        if (existing is null)
            db.FavoriteQuestions.Add(new FavoriteQuestion { UserId = userId.Value, QuestionId = questionId });
        else
            db.FavoriteQuestions.Remove(existing);

        await db.SaveChangesAsync(ct);
        return Ok(new { isFavorite = existing is null });
    }

    /// <summary>Pasif (aktif olmayan) durumunu tersine çevirir ve yeni durumu döndürür.</summary>
    [Authorize]
    [HttpPost("questions/{questionId:guid}/inactive")]
    public async Task<ActionResult> ToggleInactive(Guid questionId, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        if (!await db.Questions.AnyAsync(q => q.Id == questionId, ct))
            return NotFound(new { message = "Soru bulunamadı." });

        var existing = await db.InactiveQuestions
            .FirstOrDefaultAsync(i => i.UserId == userId && i.QuestionId == questionId, ct);

        if (existing is null)
            db.InactiveQuestions.Add(new InactiveQuestion { UserId = userId.Value, QuestionId = questionId });
        else
            db.InactiveQuestions.Remove(existing);

        await db.SaveChangesAsync(ct);
        return Ok(new { isInactive = existing is null });
    }

    /// <summary>
    /// Konular ekranı için özet: toplam soru sayısı, favori sayısı ve pasif soru sayısı.
    /// "Tümü", "Favorilerim" ve "Aktif Olmayanlar" kartları ile "Toplam soru: ..." göstergesi bunu kullanır.
    /// </summary>
    [HttpGet("me/summary")]
    public async Task<ActionResult> Summary(CancellationToken ct)
    {
        var userId = CurrentUserId;

        var totalQuestions = await db.Questions.CountAsync(ct);

        var favoriteCount = userId is null
            ? 0
            : await db.FavoriteQuestions.CountAsync(f => f.UserId == userId, ct);

        var inactiveCount = userId is null
            ? 0
            : await db.InactiveQuestions.CountAsync(i => i.UserId == userId, ct);

        return Ok(new { totalQuestions, favoriteCount, inactiveCount });
    }

    /// <summary>
    /// Soru kartının yanındaki bilgi kartı için: aktif kapsamdaki (konu / favoriler /
    /// aktif olmayanlar / tümü) toplam soru sayısı, seviye dağılımı, favori ve pasif
    /// soru sayıları. <paramref name="scope"/>: "topic" | "favorites" | "inactive" | "all".
    /// </summary>
    [HttpGet("me/scope-stats")]
    public async Task<ActionResult<ScopeStatsDto>> ScopeStats(
        [FromQuery] Guid? topicId,
        [FromQuery] string scope,
        CancellationToken ct)
    {
        var userId = CurrentUserId;

        var query = db.Questions.AsQueryable();

        if (scope == "topic" && topicId is not null)
            query = query.Where(q => q.TopicId == topicId);
        else if (scope == "favorites")
            query = userId is null
                ? query.Where(q => false)
                : query.Where(q => db.FavoriteQuestions.Any(f => f.UserId == userId && f.QuestionId == q.Id));
        else if (scope == "inactive")
            query = userId is null
                ? query.Where(q => false)
                : query.Where(q => db.InactiveQuestions.Any(i => i.UserId == userId && i.QuestionId == q.Id));
        // "all" için ek filtre uygulanmaz.

        var ids = await query.Select(q => q.Id).ToListAsync(ct);

        var levelCounts = new int[UserQuestionLevel.MaxLevel + 1];
        if (userId is not null && ids.Count > 0)
        {
            var levels = await db.UserQuestionLevels
                .Where(l => l.UserId == userId && ids.Contains(l.QuestionId))
                .Select(l => l.Level)
                .ToListAsync(ct);

            foreach (var level in levels)
                levelCounts[Math.Clamp(level, UserQuestionLevel.MinLevel, UserQuestionLevel.MaxLevel)]++;

            // Seviye kaydı olmayan sorular başlangıç seviyesindedir.
            levelCounts[UserQuestionLevel.StartLevel] += ids.Count - levels.Count;
        }
        else
        {
            levelCounts[UserQuestionLevel.StartLevel] = ids.Count;
        }

        var favoriteCount = userId is null
            ? 0
            : await db.FavoriteQuestions.CountAsync(f => f.UserId == userId && ids.Contains(f.QuestionId), ct);

        var inactiveCount = userId is null
            ? 0
            : await db.InactiveQuestions.CountAsync(i => i.UserId == userId && ids.Contains(i.QuestionId), ct);

        return Ok(new ScopeStatsDto(ids.Count, levelCounts, favoriteCount, inactiveCount));
    }

    /// <summary>
    /// Ağırlıklı rastgele sıralama anahtarı (exponential jitter). Her soruya
    /// -ln(U)/weight değeri atanır; küçükten büyüğe sıralayınca ağırlığı yüksek olan
    /// öne geçme olasılığını artırır ama sonuç yine de rastgele kalır — aynı sorular
    /// her seferinde aynı sırada gelmez.
    ///
    /// Ağırlık = MaxLevel + 1 - level, yani seviye 0 → 6 kat, seviye 5 → 1 kat şans.
    /// </summary>
    private static double WeightedSortKey(int level)
    {
        var weight = UserQuestionLevel.MaxLevel + 1 - Math.Clamp(
            level, UserQuestionLevel.MinLevel, UserQuestionLevel.MaxLevel);

        // Random.Shared.NextDouble() [0,1) döndürür; log(0) sonsuza gitmesin diye kaydırılır.
        var u = 1.0 - Random.Shared.NextDouble();
        return -Math.Log(u) / weight;
    }

    /// <summary>Bir soruda kullanıcıya gösterilecek şık sayısı: 1 doğru + 4 yanlış.</summary>
    private const int ChoicesPerQuestion = 5;

    /// <summary>
    /// Şık havuzundan sunulacak şıkları seçer. Normal soruda havuzdaki doğrulardan
    /// rastgele 1, yanlışlardan rastgele 4 tane alınır. Ters soruda (IsNegative)
    /// seçim tersine döner: 1 yanlış + 4 doğru sunulur, aranan cevap yanlış olandır.
    /// Havuzda yeterli şık yoksa eldeki kadarı gösterilir.
    /// Eşleştirme soruları şık kullanmaz; onlarda boş liste döner.
    /// </summary>
    private static List<ChoiceDto> BuildChoices(Question question)
    {
        if (question.Type == QuestionType.Matching || question.Choices.Count == 0)
            return [];

        // Ters soruda "aranan" şık yanlış olandır; havuzların rolü yer değiştirir.
        var soughtPool = question.Choices.Where(c => c.IsCorrect != question.IsNegative).ToList();
        var fillerPool = question.Choices.Where(c => c.IsCorrect == question.IsNegative).ToList();

        var picked = new List<Choice>();

        // Aranan havuzdan rastgele bir tanesi sorulur.
        if (soughtPool.Count > 0)
            picked.Add(soughtPool[Random.Shared.Next(soughtPool.Count)]);

        picked.AddRange(fillerPool
            .OrderBy(_ => Random.Shared.Next())
            .Take(ChoicesPerQuestion - picked.Count));

        // Aranan cevap hep aynı sırada çıkmasın diye son bir karıştırma.
        return picked
            .OrderBy(_ => Random.Shared.Next())
            .Select(c => new ChoiceDto(c.Id, c.Text))
            .ToList();
    }

    /// <summary>Sorunun ilgili not parçası — "İlgili notu göster" butonu bunu çağırır.</summary>
    [HttpGet("questions/{questionId:guid}/note")]
    public async Task<ActionResult<NoteDto>> GetQuestionNote(Guid questionId, CancellationToken ct)
    {
        var note = await db.Questions
            .Where(q => q.Id == questionId)
            .Select(q => new NoteDto(q.Note.Id, q.Note.TopicId, q.Note.Title, q.Note.Body))
            .FirstOrDefaultAsync(ct);

        return note is null ? NotFound(new { message = "Soru veya not bulunamadı." }) : Ok(note);
    }

    [HttpGet("notes/{noteId:guid}")]
    public async Task<ActionResult<NoteDto>> GetNote(Guid noteId, CancellationToken ct)
    {
        var note = await db.Notes
            .Where(n => n.Id == noteId)
            .Select(n => new NoteDto(n.Id, n.TopicId, n.Title, n.Body))
            .FirstOrDefaultAsync(ct);

        return note is null ? NotFound(new { message = "Not bulunamadı." }) : Ok(note);
    }

    /// <summary>Cevabı değerlendirir; sonucu ve ilgili notu birlikte döndürür.</summary>
    [HttpPost("answers")]
    public async Task<ActionResult<AnswerResultDto>> SubmitAnswer(SubmitAnswerRequest req, CancellationToken ct)
    {
        var question = await db.Questions
            .Include(q => q.Choices)
            .Include(q => q.MatchPairs)
            .Include(q => q.Note)
            .FirstOrDefaultAsync(q => q.Id == req.QuestionId, ct);

        if (question is null)
            return NotFound(new { message = "Soru bulunamadı." });

        bool isCorrect;
        Guid? correctChoiceId = null;
        Dictionary<Guid, Guid>? correctPairs = null;

        if (question.Type == QuestionType.Matching)
        {
            // Doğru eşleştirme: her çiftin kendi id'si hem sol hem sağ tarafı temsil eder.
            correctPairs = question.MatchPairs.ToDictionary(m => m.Id, m => m.Id);
            var submitted = req.Pairs ?? new Dictionary<Guid, Guid>();
            isCorrect = correctPairs.Count > 0
                        && submitted.Count == correctPairs.Count
                        && correctPairs.All(p => submitted.TryGetValue(p.Key, out var v) && v == p.Value);
        }
        else
        {
            // Havuzda birden fazla aranan şık olabilir; kullanıcıya bunlardan yalnızca biri
            // gösterilir. Bu yüzden "havuzun ilki" ile değil, seçilen şıkkın kendi
            // IsCorrect değeriyle karşılaştırılır. Ters soruda aranan cevap yanlış olandır.
            var selected = question.Choices.FirstOrDefault(c => c.Id == req.SelectedChoiceId);
            isCorrect = selected is not null && selected.IsCorrect != question.IsNegative;

            // Yanlış cevapta yeşil işaretlenecek şık, kullanıcının ekranda gördüğü aranan
            // şık olmalı. Gösterilen set bildirilmişse o setin içinden seçilir;
            // bildirilmemişse havuzdaki herhangi bir arananla yetinilir.
            correctChoiceId = isCorrect
                ? selected!.Id
                : question.Choices
                    .Where(c => c.IsCorrect != question.IsNegative)
                    .Where(c => req.PresentedChoiceIds is null || req.PresentedChoiceIds.Contains(c.Id))
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefault();
        }

        var userId = CurrentUserId;

        // Seviye takibi: giriş yapan herkes için, switch kapalı olsa bile işlenir.
        // Switch yalnızca soru seçimindeki ağırlığı devreye alır.
        int? newLevel = null;
        int? previousLevel = null;

        if (userId is not null)
        {
            var levelRow = await db.UserQuestionLevels
                .FirstOrDefaultAsync(l => l.UserId == userId && l.QuestionId == question.Id, ct);

            if (levelRow is null)
            {
                levelRow = new UserQuestionLevel { UserId = userId.Value, QuestionId = question.Id };
                db.UserQuestionLevels.Add(levelRow);
            }

            previousLevel = levelRow.Level;
            levelRow.Apply(isCorrect);
            newLevel = levelRow.Level;

            await db.SaveChangesAsync(ct);
        }

        var noteDto = new NoteDto(question.Note.Id, question.Note.TopicId, question.Note.Title, question.Note.Body);
        return Ok(new AnswerResultDto(
            isCorrect, question.Explanation, correctChoiceId, correctPairs, noteDto,
            newLevel, previousLevel, UserQuestionLevel.MaxLevel));
    }

}
