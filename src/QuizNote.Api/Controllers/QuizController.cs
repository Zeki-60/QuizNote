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

    /// <summary>Kullanıcının yeni bir konu (Topic) açması; "+ Konu ekle" butonu bunu çağırır.</summary>
    [Authorize]
    [HttpPost("topics")]
    public async Task<ActionResult<TopicDto>> CreateTopic(CreateTopicRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Konu adı boş olamaz." });

        var name = req.Name.Trim();
        if (await db.Topics.AnyAsync(t => t.Name == name, ct))
            return BadRequest(new { message = "Bu isimde bir konu zaten var." });

        var topic = new Topic { Name = name, Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim() };
        db.Topics.Add(topic);
        await db.SaveChangesAsync(ct);

        return Ok(new TopicDto(topic.Id, topic.Name, topic.Description, 0));
    }

    /// <summary>Konu adını/açıklamasını günceller; konu kartındaki ✎ butonu bunu çağırır.</summary>
    [Authorize]
    [HttpPut("topics/{topicId:guid}")]
    public async Task<ActionResult<TopicDto>> UpdateTopic(Guid topicId, CreateTopicRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Konu adı boş olamaz." });

        var topic = await db.Topics.Include(t => t.Questions).FirstOrDefaultAsync(t => t.Id == topicId, ct);
        if (topic is null) return NotFound(new { message = "Konu bulunamadı." });

        var name = req.Name.Trim();
        if (name != topic.Name && await db.Topics.AnyAsync(t => t.Name == name, ct))
            return BadRequest(new { message = "Bu isimde bir konu zaten var." });

        topic.Name = name;
        topic.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        await db.SaveChangesAsync(ct);

        return Ok(new TopicDto(topic.Id, topic.Name, topic.Description, topic.Questions.Count));
    }

    /// <summary>
    /// Konuyu ve altındaki TÜM notları/soruları/şıkları siler (cascade delete).
    /// Konu kartındaki 🗑 butonu, kullanıcı onayladıktan sonra bunu çağırır.
    /// </summary>
    [Authorize]
    [HttpDelete("topics/{topicId:guid}")]
    public async Task<ActionResult> DeleteTopic(Guid topicId, CancellationToken ct)
    {
        var topic = await db.Topics.FirstOrDefaultAsync(t => t.Id == topicId, ct);
        if (topic is null) return NotFound(new { message = "Konu bulunamadı." });

        db.Topics.Remove(topic);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Kullanıcının arayüzden yeni soru eklemesi. Not her zaman yeni oluşturulur;
    /// başlık/gövde boş bırakılırsa soru metninden türetilen bir başlıkla ve boş
    /// gövdeyle otomatik bir not açılır. Eklenen soru <see cref="Question.CreatedByUserId"/>
    /// ile işaretlenir — "Kendi Sorularım" kartı bunu kullanır.
    /// </summary>
    [Authorize]
    [HttpPost("questions")]
    public async Task<ActionResult> CreateUserQuestion(CreateUserQuestionRequest req, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { message = "Soru metni boş olamaz." });

        if (!await db.Topics.AnyAsync(t => t.Id == req.TopicId, ct))
            return BadRequest(new { message = "Konu bulunamadı." });

        var choices = req.Choices?.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList() ?? [];
        if (choices.Count < 2)
            return BadRequest(new { message = "En az iki şık girilmelidir." });
        if (!choices.Any(c => c.IsCorrect))
            return BadRequest(new { message = "En az bir doğru şık işaretlenmelidir." });
        if (!choices.Any(c => !c.IsCorrect))
            return BadRequest(new { message = "En az bir yanlış şık işaretlenmelidir." });

        var noteTitle = string.IsNullOrWhiteSpace(req.NoteTitle)
            ? (req.Text.Length > 80 ? req.Text[..80] + "…" : req.Text)
            : req.NoteTitle.Trim();
        var noteBody = string.IsNullOrWhiteSpace(req.NoteBody) ? " " : req.NoteBody.Trim();

        var note = new Note { TopicId = req.TopicId, Title = noteTitle, Body = noteBody };
        db.Notes.Add(note);

        var question = new Question
        {
            TopicId = req.TopicId,
            Note = note,
            Type = QuestionType.MultipleChoice,
            Text = req.Text.Trim(),
            Explanation = string.IsNullOrWhiteSpace(req.Explanation) ? null : req.Explanation.Trim(),
            CreatedByUserId = userId,
        };

        for (var i = 0; i < choices.Count; i++)
            question.Choices.Add(new Choice { Text = choices[i].Text.Trim(), IsCorrect = choices[i].IsCorrect, OrderIndex = i + 1 });

        db.Questions.Add(question);
        await db.SaveChangesAsync(ct);

        return Ok(new { id = question.Id });
    }

    /// <summary>
    /// Sonsuz akış için tek bir soru döndürür; havuzdan rastgele seçilir.
    /// <paramref name="topicId"/> verilmezse tüm konuların soruları havuza girer.
    /// <paramref name="prioritizeHard"/> açıkken seçim seviyeye göre ağırlıklandırılır:
    /// seviyesi düşük (zorlanılan) sorular daha sık gelir.
    /// <paramref name="excludeIds"/> son gösterilen soruları dışlar; aynı soru üst üste gelmez.
    /// <paramref name="favoritesOnly"/> açıkken yalnızca favori sorulardan seçilir.
    /// <paramref name="orderIndex"/> verilmişse rastgele seçim yapılmaz; yalnızca
    /// <paramref name="topicId"/> ile belirtilen konuda o sıra numarasına sahip soru
    /// döner. Yalnızca bir konu içindeyken anlamlıdır; <paramref name="topicId"/>
    /// verilmemişse veya <paramref name="favoritesOnly"/>/<paramref name="myQuestionsOnly"/>
    /// açıksa reddedilir.
    /// </summary>
    [HttpGet("next-question")]
    public async Task<ActionResult<QuestionDto>> GetNextQuestion(
        [FromQuery] Guid? topicId,
        [FromQuery] bool prioritizeHard,
        [FromQuery] bool favoritesOnly,
        [FromQuery] bool myQuestionsOnly,
        [FromQuery] string? excludeIds,
        [FromQuery] int? orderIndex,
        CancellationToken ct)
    {
        if (topicId is not null && !await db.Topics.AnyAsync(t => t.Id == topicId, ct))
            return NotFound(new { message = "Konu bulunamadı." });

        if (orderIndex is not null && (topicId is null || favoritesOnly || myQuestionsOnly))
            return BadRequest(new { message = "Sıra numarasıyla arama yalnızca bir konu içindeyken kullanılabilir." });

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

        if (myQuestionsOnly)
        {
            if (userId is null)
                return Unauthorized(new { message = "Kendi sorularınız için giriş yapmalısınız." });

            query = query.Where(q => q.CreatedByUserId == userId);
        }

        if (orderIndex is not null)
            query = query.Where(q => q.OrderIndex == orderIndex);

        var questions = await query
            .Include(q => q.Choices)
            .Include(q => q.MatchPairs)
            .Include(q => q.Note)
            .ToListAsync(ct);

        if (questions.Count == 0)
            return NotFound(new
            {
                message = orderIndex is not null
                    ? $"Bu konuda {orderIndex} numaralı soru bulunamadı."
                    : myQuestionsOnly
                        ? "Henüz eklediğiniz bir soru yok."
                        : favoritesOnly
                            ? "Favori sorunuz yok. Sorulardaki kalp simgesine dokunarak ekleyebilirsiniz."
                            : "Soru bulunamadı."
            });

        // Sıra numarasıyla aranıyorsa dışlama/rastgelelik uygulanmaz; eşleşen soru
        // (varsa birden fazla olsa da) doğrudan gösterilir.
        List<Question> pool;
        if (orderIndex is not null)
        {
            pool = questions;
        }
        else
        {
            // Son gösterilen soruları dışla; havuz tükenirse dışlama kaldırılır.
            var excluded = ParseIds(excludeIds);
            pool = questions.Where(q => !excluded.Contains(q.Id)).ToList();
            if (pool.Count == 0) pool = questions;
        }

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

        return Ok(ToDto(picked, LevelOf(picked), isFavorite));
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

    private static QuestionDto ToDto(Question q, int level, bool isFavorite) => new(
        q.Id,
        q.Type,
        q.Text,
        // Soru no arama kutusunda gösterilir; sıra numarası konu içindeki gerçek değeridir.
        q.OrderIndex,
        q.NoteId,
        q.Note.Title,
        BuildChoices(q),
        q.MatchPairs.OrderBy(m => m.OrderIndex).Select(m => new MatchLeftDto(m.Id, m.LeftText)).ToList(),
        // Sağ taraf karıştırılır, yoksa eşleştirme sırayla çözülebilir olurdu.
        q.MatchPairs.OrderBy(_ => Guid.NewGuid()).Select(m => new MatchRightDto(m.Id, m.RightText)).ToList(),
        level,
        UserQuestionLevel.MaxLevel,
        isFavorite);

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

    /// <summary>
    /// Kullanıcının bu sorudaki seviyesini doğrudan maksimuma (5) ayarlar. Soru
    /// ekranındaki "Max" butonu bunu çağırır; yeni seviyeyi döndürür.
    /// </summary>
    [Authorize]
    [HttpPost("questions/{questionId:guid}/max-level")]
    public async Task<ActionResult> SetMaxLevel(Guid questionId, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        if (!await db.Questions.AnyAsync(q => q.Id == questionId, ct))
            return NotFound(new { message = "Soru bulunamadı." });

        var levelRow = await db.UserQuestionLevels
            .FirstOrDefaultAsync(l => l.UserId == userId && l.QuestionId == questionId, ct);

        if (levelRow is null)
        {
            levelRow = new UserQuestionLevel { UserId = userId.Value, QuestionId = questionId };
            db.UserQuestionLevels.Add(levelRow);
        }

        levelRow.Level = UserQuestionLevel.MaxLevel;
        levelRow.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(new { level = levelRow.Level, maxLevel = UserQuestionLevel.MaxLevel });
    }

    /// <summary>
    /// Bir soruyu (ve bağlı şıklarını) kalıcı olarak siler. Soru kartındaki 🗑 butonu,
    /// kullanıcı onayladıktan sonra bunu çağırır. Bağlı not silinmez.
    /// </summary>
    [Authorize]
    [HttpDelete("questions/{questionId:guid}")]
    public async Task<ActionResult> DeleteQuestion(Guid questionId, CancellationToken ct)
    {
        var question = await db.Questions.FirstOrDefaultAsync(q => q.Id == questionId, ct);
        if (question is null) return NotFound(new { message = "Soru bulunamadı." });

        db.Questions.Remove(question);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Konular ekranı için özet: toplam soru sayısı, favori sayısı ve kullanıcının
    /// kendi eklediği soru sayısı. "Tümü", "Favorilerim" ve "Kendi Sorularım" kartları
    /// ile "Toplam soru: ..." göstergesi bunu kullanır.
    /// </summary>
    [HttpGet("me/summary")]
    public async Task<ActionResult> Summary(CancellationToken ct)
    {
        var userId = CurrentUserId;

        var totalQuestions = await db.Questions.CountAsync(ct);

        var favoriteCount = userId is null
            ? 0
            : await db.FavoriteQuestions.CountAsync(f => f.UserId == userId, ct);

        var myQuestionsCount = userId is null
            ? 0
            : await db.Questions.CountAsync(q => q.CreatedByUserId == userId, ct);

        return Ok(new { totalQuestions, favoriteCount, myQuestionsCount });
    }

    /// <summary>
    /// Soru kartının yanındaki bilgi kartı için: aktif kapsamdaki (konu / favoriler /
    /// kendi sorularım / tümü) toplam soru sayısı, seviye dağılımı, favori sayısı.
    /// <paramref name="scope"/>: "topic" | "favorites" | "myQuestions" | "all".
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
        else if (scope == "myQuestions")
            query = userId is null
                ? query.Where(q => false)
                : query.Where(q => q.CreatedByUserId == userId);
        // "all" için ek filtre uygulanmaz.

        var ids = await query.Select(q => q.Id).ToListAsync(ct);

        var levelCounts = new int[UserQuestionLevel.MaxLevel + 1];
        var levelSum = 0;
        if (userId is not null && ids.Count > 0)
        {
            var levels = await db.UserQuestionLevels
                .Where(l => l.UserId == userId && ids.Contains(l.QuestionId))
                .Select(l => l.Level)
                .ToListAsync(ct);

            foreach (var level in levels)
                levelCounts[Math.Clamp(level, UserQuestionLevel.MinLevel, UserQuestionLevel.MaxLevel)]++;

            // Seviye kaydı olmayan sorular başlangıç seviyesindedir (0 katkı yapar).
            levelCounts[UserQuestionLevel.StartLevel] += ids.Count - levels.Count;
            levelSum = levels.Sum();
        }
        else
        {
            levelCounts[UserQuestionLevel.StartLevel] = ids.Count;
        }

        // Konu puanı: seviyelerin toplamının, ulaşılabilecek maksimuma (soru sayısı *
        // MaxLevel) oranı; tüm sorular seviye 5 ise 100, hepsi 0 ise 0 olur.
        var scorePercent = ids.Count == 0
            ? 0
            : (int)Math.Round(100.0 * levelSum / (ids.Count * UserQuestionLevel.MaxLevel));

        var favoriteCount = userId is null
            ? 0
            : await db.FavoriteQuestions.CountAsync(f => f.UserId == userId && ids.Contains(f.QuestionId), ct);

        return Ok(new ScopeStatsDto(ids.Count, levelCounts, favoriteCount, scorePercent));
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

    // --- Soru/Not düzenleme ekranı ---

    /// <summary>Düzenleme ekranı için soruyu TÜM şıklarıyla (doğru/yanlış fark etmeksizin) döner.</summary>
    [Authorize]
    [HttpGet("questions/{questionId:guid}/edit")]
    public async Task<ActionResult<QuestionEditDto>> GetQuestionForEdit(Guid questionId, CancellationToken ct)
    {
        var q = await db.Questions
            .Include(x => x.Choices)
            .Include(x => x.Note)
            .FirstOrDefaultAsync(x => x.Id == questionId, ct);

        if (q is null) return NotFound(new { message = "Soru bulunamadı." });

        return Ok(new QuestionEditDto(
            q.Id, q.TopicId, q.Type, q.Text, q.IsNegative, q.Explanation, q.OrderIndex,
            q.NoteId, q.Note.Title, q.Note.Body,
            q.Choices.OrderBy(c => c.OrderIndex)
                .Select(c => new ChoiceEditDto(c.Id, c.Text, c.IsCorrect, c.OrderIndex))
                .ToList()));
    }

    /// <summary>Soru metnini, ters soru bayrağını ve açıklamasını günceller.</summary>
    [Authorize]
    [HttpPut("questions/{questionId:guid}")]
    public async Task<ActionResult> UpdateQuestion(Guid questionId, UpdateQuestionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { message = "Soru metni boş olamaz." });

        var q = await db.Questions.FirstOrDefaultAsync(x => x.Id == questionId, ct);
        if (q is null) return NotFound(new { message = "Soru bulunamadı." });

        q.Text = req.Text.Trim();
        q.IsNegative = req.IsNegative;
        q.Explanation = string.IsNullOrWhiteSpace(req.Explanation) ? null : req.Explanation.Trim();

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sorunun bağlı olduğu notu (başlık ve gövde) günceller.</summary>
    [Authorize]
    [HttpPut("notes/{noteId:guid}")]
    public async Task<ActionResult> UpdateNote(Guid noteId, UpdateNoteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body))
            return BadRequest(new { message = "Not başlığı ve içeriği boş olamaz." });

        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return NotFound(new { message = "Not bulunamadı." });

        note.Title = req.Title.Trim();
        note.Body = req.Body.Trim();

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Bir soruya yeni şık ekler; eklenen şıkkı döner.</summary>
    [Authorize]
    [HttpPost("questions/{questionId:guid}/choices")]
    public async Task<ActionResult<ChoiceEditDto>> AddChoice(
        Guid questionId, CreateChoiceForQuestionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { message = "Şık metni boş olamaz." });

        var q = await db.Questions.Include(x => x.Choices).FirstOrDefaultAsync(x => x.Id == questionId, ct);
        if (q is null) return NotFound(new { message = "Soru bulunamadı." });

        var nextOrder = q.Choices.Count == 0 ? 1 : q.Choices.Max(c => c.OrderIndex) + 1;
        var choice = new Choice
        {
            QuestionId = questionId,
            Text = req.Text.Trim(),
            IsCorrect = req.IsCorrect,
            OrderIndex = nextOrder,
        };

        db.Choices.Add(choice);
        await db.SaveChangesAsync(ct);

        return Ok(new ChoiceEditDto(choice.Id, choice.Text, choice.IsCorrect, choice.OrderIndex));
    }

    /// <summary>Bir şıkkın metnini ve doğru/yanlış durumunu günceller.</summary>
    [Authorize]
    [HttpPut("choices/{choiceId:guid}")]
    public async Task<ActionResult> UpdateChoice(Guid choiceId, UpdateChoiceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { message = "Şık metni boş olamaz." });

        var choice = await db.Choices.FirstOrDefaultAsync(c => c.Id == choiceId, ct);
        if (choice is null) return NotFound(new { message = "Şık bulunamadı." });

        // Son doğru şık yanlışa çevrilemez — havuzda en az 1 doğru kalmalı.
        if (choice.IsCorrect && !req.IsCorrect)
        {
            var otherCorrectCount = await db.Choices
                .CountAsync(c => c.QuestionId == choice.QuestionId && c.Id != choiceId && c.IsCorrect, ct);
            if (otherCorrectCount == 0)
                return BadRequest(new { message = "Son doğru şık yanlış olarak işaretlenemez; en az bir doğru şık kalmalıdır." });
        }

        // Son yanlış şık doğruya çevrilemez — havuzda en az 1 yanlış kalmalı.
        if (!choice.IsCorrect && req.IsCorrect)
        {
            var otherWrongCount = await db.Choices
                .CountAsync(c => c.QuestionId == choice.QuestionId && c.Id != choiceId && !c.IsCorrect, ct);
            if (otherWrongCount == 0)
                return BadRequest(new { message = "Son yanlış şık doğru olarak işaretlenemez; en az bir yanlış şık kalmalıdır." });
        }

        choice.Text = req.Text.Trim();
        choice.IsCorrect = req.IsCorrect;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Bir şıkkı siler. Havuzdaki son doğru veya son yanlış şık silinemez.</summary>
    [Authorize]
    [HttpDelete("choices/{choiceId:guid}")]
    public async Task<ActionResult> DeleteChoice(Guid choiceId, CancellationToken ct)
    {
        var choice = await db.Choices.FirstOrDefaultAsync(c => c.Id == choiceId, ct);
        if (choice is null) return NotFound(new { message = "Şık bulunamadı." });

        if (choice.IsCorrect)
        {
            var otherCorrectCount = await db.Choices
                .CountAsync(c => c.QuestionId == choice.QuestionId && c.Id != choiceId && c.IsCorrect, ct);
            if (otherCorrectCount == 0)
                return BadRequest(new { message = "Son doğru şık silinemez; en az bir doğru şık kalmalıdır." });
        }
        else
        {
            var otherWrongCount = await db.Choices
                .CountAsync(c => c.QuestionId == choice.QuestionId && c.Id != choiceId && !c.IsCorrect, ct);
            if (otherWrongCount == 0)
                return BadRequest(new { message = "Son yanlış şık silinemez; en az bir yanlış şık kalmalıdır." });
        }

        db.Choices.Remove(choice);
        await db.SaveChangesAsync(ct);
        return NoContent();
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

    /// <summary>
    /// Soru çözme ekranındaki aktif kapsama (tümü / konu / favoriler / kendi sorularım)
    /// göre soruları (ve notlarını) DbSeeder.cs'deki seed veri formatına benzer C# kodu
    /// olarak bir .txt dosyası şeklinde indirir. Soru ekranındaki indirme ikonu bunu
    /// çağırır; <paramref name="scope"/>: "topic" | "favorites" | "myQuestions" | "all".
    /// <paramref name="scope"/> "topic" ise <paramref name="topicId"/> zorunludur.
    /// </summary>
    [HttpGet("questions/export")]
    public async Task<ActionResult> ExportQuestions(
        [FromQuery] string scope, [FromQuery] Guid? topicId, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var query = db.Questions.AsQueryable();
        string scopeLabel;

        if (scope == "topic")
        {
            if (topicId is null)
                return BadRequest(new { message = "Konu belirtilmelidir." });

            var topic = await db.Topics.FirstOrDefaultAsync(t => t.Id == topicId, ct);
            if (topic is null) return NotFound(new { message = "Konu bulunamadı." });

            query = query.Where(q => q.TopicId == topicId);
            scopeLabel = $"\"{topic.Name}\" konusu";
        }
        else if (scope == "favorites")
        {
            if (userId is null) return Unauthorized(new { message = "Favoriler için giriş yapmalısınız." });
            query = query.Where(q => db.FavoriteQuestions.Any(f => f.UserId == userId && f.QuestionId == q.Id));
            scopeLabel = "Favorilerim";
        }
        else if (scope == "myQuestions")
        {
            if (userId is null) return Unauthorized(new { message = "Kendi sorularınız için giriş yapmalısınız." });
            query = query.Where(q => q.CreatedByUserId == userId);
            scopeLabel = "Kendi sorularım";
        }
        else if (scope == "all")
        {
            scopeLabel = "Tüm sorular";
        }
        else
        {
            return BadRequest(new { message = "Geçersiz kapsam." });
        }

        var questions = await query
            .Include(q => q.Choices)
            .Include(q => q.Note)
            .Include(q => q.Topic)
            .OrderBy(q => q.Topic.Name).ThenBy(q => q.CreatedAt)
            .ToListAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"// {scopeLabel} — DbSeeder.cs seed formatında dışa aktarıldı.");
        sb.AppendLine($"// Dışa aktarma tarihi: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        // Her sorunun notu ayrı bir değişkene atanır; aynı isim (örn. "not") tüm
        // sorularda tekrarlanırsa dosya tek bir metot scope'una yapıştırıldığında
        // "değişken zaten tanımlı" derleme hatası verir. Rastgele kısa bir kimlikle
        // benzersizleştirilir.
        foreach (var byTopic in questions.GroupBy(q => q.Topic.Name))
        {
            sb.AppendLine($"// --- Konu: {byTopic.Key} ---");
            sb.AppendLine();

            foreach (var q in byTopic)
            {
                var noteVar = $"not_{Guid.NewGuid():N}"[..12];

                sb.AppendLine($"var {noteVar} = new Note");
                sb.AppendLine("{");
                sb.AppendLine($"    Title = {CsString(q.Note.Title)},");
                sb.AppendLine($"    Body = {CsString(q.Note.Body)},");
                sb.AppendLine("};");
                sb.AppendLine();

                sb.AppendLine("new Question");
                sb.AppendLine("{");
                sb.AppendLine($"    Note = {noteVar},");
                sb.AppendLine("    Type = QuestionType.MultipleChoice,");
                sb.AppendLine($"    Text = {CsString(q.Text)},");
                if (!string.IsNullOrWhiteSpace(q.Explanation))
                    sb.AppendLine($"    Explanation = {CsString(q.Explanation)},");
                sb.AppendLine($"    OrderIndex = {q.OrderIndex},");
                sb.AppendLine("    Choices =");
                sb.AppendLine("    {");
                foreach (var c in q.Choices.OrderBy(c => c.OrderIndex))
                    sb.AppendLine(
                        $"        new Choice {{ Text = {CsString(c.Text)}, IsCorrect = {(c.IsCorrect ? "true" : "false")}, OrderIndex = {c.OrderIndex} }},");
                sb.AppendLine("    }");
                sb.AppendLine("},");
                sb.AppendLine();
            }
        }

        if (questions.Count == 0)
            sb.AppendLine("// Bu kapsamda soru bulunamadı.");

        var fileName = scope switch
        {
            "topic" => "konu-sorulari.txt",
            "favorites" => "favori-sorularim.txt",
            "myQuestions" => "kendi-sorularim.txt",
            _ => "tum-sorular.txt",
        };

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/plain; charset=utf-8", fileName);
    }

    /// <summary>Bir metni C# string literal'ine (çift tırnak + kaçış) çevirir.</summary>
    private static string CsString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n") + "\"";

}
