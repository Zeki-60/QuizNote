using Microsoft.EntityFrameworkCore;
using QuizNote.Application.Entities;

namespace QuizNote.Persistence;

/// <summary>
/// Uygulama açılışında çalışır: migration'ları uygular ve seed içeriğini veritabanına yazar.
/// Seed <b>idempotenttir</b>: her açılışta çalışır, var olan kayıtları atlar ve yalnızca
/// yeni eklenenleri yazar. Böylece yeni soru eklemek için veritabanını silmek gerekmez;
/// kullanıcıların seviye ve favori kayıtları korunur.
///
/// <b>Not:</b> Seed verisi (sorular/notlar) artık bu dosyada tutulmuyor — içerik
/// veritabanına taşındı ve siteden (API üzerinden) yönetiliyor. <see cref="BuildSeedData"/>
/// şu an boş bir liste döndürür; bu yüzden <see cref="MigrateAndSeedAsync"/> yalnızca
/// migration'ları uygular, veritabanındaki mevcut verilere dokunmaz.
///
/// <b>Yeniden seed eklemek istersen</b> (bkz. <see cref="BuildSeedData"/> ve aşağıdaki
/// BuildXxx() fonksiyonları içindeki yorumlar, dosyanın en altındaki tam şablon):
///   1) İlgili BuildXxx() içindeki Notes/Questions listelerini doldur.
///   2) BuildSeedData() içinde o fonksiyonun çağrısının başındaki "//" işaretini kaldır.
/// </summary>
public static class DbSeeder
{
    public static async Task MigrateAndSeedAsync(QuizNoteDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        var topics = BuildSeedData();
        if (topics.Count == 0) return;

        foreach (var topic in topics)
            Validate(topic);

        foreach (var topic in topics)
            await SyncTopicAsync(db, topic, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Seed içeriğini veritabanına yazmadan önce doğrular. Bir sorunun bağlı olduğu not
    /// <see cref="Topic.Notes"/> listesine eklenmemişse EF onu sahipsiz bir kayıt olarak
    /// yazmaya çalışır ve anlaşılması güç bir foreign key hatası alınır; burada erkenden
    /// ve hangi notun eksik olduğunu söyleyerek durdurulur.
    ///
    /// Ayrıca aynı konu içinde tekrarlanan Question.OrderIndex değerleri de yakalanır:
    /// <see cref="SyncTopicAsync"/> soruları OrderIndex'e göre eşleştirdiği için, aynı
    /// konuda iki soru aynı OrderIndex'i paylaşırsa biri sessizce yok sayılır.
    /// </summary>
    private static void Validate(Topic topic)
    {
        var registered = topic.Notes.Select(n => n.Title).ToHashSet();

        var missing = topic.Questions
            .Where(q => q.Note is not null && !registered.Contains(q.Note.Title))
            .Select(q => q.Note.Title)
            .Distinct()
            .ToList();

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"'{topic.Name}' konusunda şu notlar sorulara bağlı ama Notes listesine " +
                $"eklenmemiş: {string.Join(", ", missing)}");

        var duplicateOrderIndexes = topic.Questions
            .GroupBy(q => q.OrderIndex)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateOrderIndexes.Count > 0)
            throw new InvalidOperationException(
                $"'{topic.Name}' konusunda şu OrderIndex değerleri birden fazla soruda " +
                $"tekrarlanıyor: {string.Join(", ", duplicateOrderIndexes)}. Her sorunun " +
                "OrderIndex'i kendi konusu içinde benzersiz olmalıdır.");
    }

    /// <summary>
    /// Bir konuyu veritabanıyla eşitler. Eşleştirme doğal anahtarla yapılır
    /// (Topic → Name, Note → Title, Question → OrderIndex); ID'ler her derlemede
    /// yeniden üretildiği için ID üzerinden karşılaştırma yapılamaz.
    /// Mevcut kayıtların içeriğine dokunulmaz, yalnızca eksikler eklenir.
    /// </summary>
    private static async Task SyncTopicAsync(QuizNoteDbContext db, Topic seedTopic, CancellationToken ct)
    {
        var dbTopic = await db.Topics
            .Include(t => t.Notes)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Name == seedTopic.Name, ct);

        // Konu hiç yoksa tüm ağacı (notlar + sorular + şıklar) olduğu gibi ekle.
        if (dbTopic is null)
        {
            db.Topics.Add(seedTopic);
            return;
        }

        // Başlığa göre eşleme: sonuçta her başlık için tek bir Note nesnesi kalır.
        // Veritabanında varsa oradaki kayıt, yoksa seed'deki yeni nesne kullanılır.
        // Aynı başlık birden fazla kez geçerse ilki esas alınır; ToDictionary yerine
        // gruplama kullanılmasının sebebi budur (mükerrer başlık seed'i çökertmesin).
        var noteByTitle = dbTopic.Notes
            .GroupBy(n => n.Title)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var seedNote in seedTopic.Notes)
        {
            if (noteByTitle.ContainsKey(seedNote.Title)) continue;

            seedNote.TopicId = dbTopic.Id;
            db.Notes.Add(seedNote);
            noteByTitle[seedNote.Title] = seedNote;
        }

        // OrderIndex'i veritabanında bulunmayan soruları, şıklarıyla birlikte ekle.
        // Metin yerine OrderIndex'e göre eşleştirilir: kullanıcı bir soruyu API üzerinden
        // (metnini) güncellediğinde, seed'deki eski metin artık veritabanındakiyle
        // eşleşmez ve metin bazlı karşılaştırma bunu yanlışlıkla "yeni soru" sayıp
        // kopyalardı. OrderIndex, Konu içinde soru başına sabit bir kimlik gibi kullanılır.
        var existingOrderIndexes = dbTopic.Questions.Select(q => q.OrderIndex).ToHashSet();

        foreach (var seedQuestion in seedTopic.Questions)
        {
            if (existingOrderIndexes.Contains(seedQuestion.OrderIndex)) continue;

            // Soru seed'de bir Note nesnesine referansla bağlıdır. O not veritabanında
            // zaten varsa referans oradaki kayda çevrilmeli; aksi halde EF aynı başlıkta
            // ikinci bir not eklemeye çalışır.
            if (!noteByTitle.TryGetValue(seedQuestion.Note.Title, out var targetNote))
                continue;

            seedQuestion.Note = targetNote;
            seedQuestion.TopicId = dbTopic.Id;

            db.Questions.Add(seedQuestion);
            existingOrderIndexes.Add(seedQuestion.OrderIndex);
        }
    }

    /// <summary>
    /// Seed içeriği burada tanımlanır. Şu an tüm BuildXxx() fonksiyonları boş
    /// (Notes/Questions içermiyor); bu yüzden bu metot [] döndürüyor ve
    /// MigrateAndSeedAsync seed adımını atlıyor. Konu iskeletleri (isim/açıklama)
    /// aşağıda hazır duruyor.
    ///
    /// YENİ SEED EKLEMEK İÇİN 2 ADIM:
    ///   1) Aşağıdaki ilgili BuildXxx() fonksiyonunun içindeki Notes/Questions
    ///      listelerini doldur (o fonksiyonun içindeki yorumlara bak).
    ///   2) Bu listede o fonksiyonun çağrısının başındaki "//" işaretini kaldır
    ///      (yoksa BuildXxx() hiç çağrılmaz, doldurduğun içerik veritabanına yazılmaz).
    /// </summary>
    private static List<Topic> BuildSeedData() =>
    [
        // Aşağıdaki satırlardan hangisinin konusuna içerik eklediysen, o satırın
         //başındaki "// " işaretini silip yorumdan çıkar:
         BuildIslamiyetOncesiTurkTarihi(),
         BuildIslamiyetOncesiKulturVeMedeniyet(),
         BuildIlkTurkIslamDevletleri(),
         BuildIlkTurkIslamDevletleriKulturVeMedeniyeti(),
         BuildAnadoluSelcukluDevleti(),
         BuildOsmanliDevletiKurulusDonemi(),
         BuildOsmanliDevletiYukselmeDonemi(),
         BuildOsmanliDevletiKulturVeMedeniyeti(),
         BuildMimariEserler(),
         BuildOsmanliDevletiDuraklamaDonemi(),
         BuildDevletler(),
         BuildOsmanliDevletiGerilemeDonemi(),
         BuildOnDokuzOsmanlı(),
         BuildOnDokuzuncuYuzyilIslahatlari(),
    ];

    private static Topic BuildIslamiyetOncesiTurkTarihi()
    {
        // Notes = { not1, not2, ... },           <-- buraya var'larla tanımladığın Note nesnelerini ekle
        // Questions = { soru1, soru2, ... },      <-- buraya var'larla tanımladığın Question nesnelerini ekle
        // Tam örnek için dosyanın en altındaki "Seed yazarken örnek şablon" yorumuna bak.
        var notKipcakDestanVeEser = new Note
        {
            Title = "Kıpçaklar — Destanlar ve Eser",
            Body = """
    • Kıpçakların Oğuzlarla yaptıkları mücadeleler Dede Korkut Hikâyelerine konu olmuştur
    • Kıpçakların Ruslarla yaptıkları mücadeleler İgor Destanı'na konu olmuştur
    • Codex Cumanicus Kıpçaklarla ilgili önemli bir eserdir ve bir sözlük olarak anlatılmıştır
    """
        };
        return new Topic
        {
            Name = "İslamiyet Öncesi Türk Tarihi",
            Description = "Türklerin ilk ana yurdu ve Orta Asya",
            Notes = { notKipcakDestanVeEser },
            Questions = {

























            new Question
{
    Note = notKipcakDestanVeEser,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Oğuzlarla yaptıkları mücadelelerin Dede Korkut Hikâyelerine konu olması hangi Türk topluluğuna aittir?",
    Explanation = "Kıpçakların Oğuzlarla yaptıkları mücadeleler Dede Korkut Hikâyelerine konu olmuştur.",
    OrderIndex = 510,
    Choices =
    {
        new Choice { Text = "Kıpçaklar", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Peçenekler", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Karluklar", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kırgızlar", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Türgişler", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notKipcakDestanVeEser,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Ruslarla yaptıkları mücadelelerin İgor Destanı'na konu olması hangi Türk topluluğuna aittir?",
    Explanation = "Kıpçakların Ruslarla yaptıkları mücadeleler İgor Destanı'na konu olmuştur.",
    OrderIndex = 511,
    Choices =
    {
        new Choice { Text = "Kıpçaklar", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Uzlar", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Avarlar", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Hazarlar", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kimekler", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notKipcakDestanVeEser,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Codex Cumanicus adlı eserin önemli bir kaynak olması ve bir sözlük olarak anılması hangi Türk topluluğuna aittir?",
    Explanation = "Codex Cumanicus, Kıpçaklarla ilgili önemli bir eser ve sözlük olarak anlatılmıştır.",
    OrderIndex = 512,
    Choices =
    {
        new Choice { Text = "Kıpçaklar", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Oğuzlar", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Basmiller", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sibirler", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Karluklar", IsCorrect = false, OrderIndex = 5 }
    }
},},
        };
    }

    private static Topic BuildIslamiyetOncesiKulturVeMedeniyet()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        var notKutAileleri = new Note
        {
            Title = "Yönetim Anlayışı — Kut Verilen Aileler",
            Body = """
• Kağan olabilmek için Kök Tengri tarafından kut verilmiş bir aileden gelmek gerekir.
• Hunlarda kut verilen aile Tuk ailesidir.
• Göktürklerde kut verilen aile Aşina ailesidir.
• Uygurlarda kut verilen aile Yağakar ailesidir.
"""
        }; var notKutKucUlus = new Note
        {
            Title = "Yönetim Anlayışı — Kut, Küç ve Ülüş",
            Body = """
• Kut, Gök Tanrı'nın hükümdara ve ailesine devleti yönetme yetkisi vermesidir.
• Küç, güç anlamına gelir ve askerî güçle ilgilidir.
• Ülüş, pay veya hisse anlamına gelir.
"""
        };

        var notDinAdamlari = new Note
        {
            Title = "Din ve İnanış — Din Adamları",
            Body = """
• Kam veya Baksı, din adamlarına verilen isimdir.
• Otacı, eczacılıkla ilişkilendirilen kişidir.
• Türklerde ruhban sınıfı bulunmaz.
• Din adamları, dinî konular görüşülse bile kurultayda yer almaz.
"""
        };

        var notDinVeAhiret = new Note
        {
            Title = "Din ve İnanış — Ahiret Kavramları",
            Body = """
• Tamu, cehennem demektir.
• Uçma, cennet demektir.
• Muyan, sevap anlamına gelir.
• Sakış Günü, ahiret ve hesap verme günü anlamına gelir.
"""
        };

        var notEsikInanisi = new Note
        {
            Title = "Din ve İnanış — Eşik İnancı",
            Body = """
• Eşikte kişinin ruhunun bulunduğuna inanılmıştır.
• Bu inanış nedeniyle eşikte oturulmaması ve eşiğe basılmaması anlayışı günümüze kadar sürmüştür.
"""
        }; var notKonilikUzlukTuzlukInsanlik = new Note
        {
            Title = "Könilik, Uzluk, Tüzlük, İnsanlık",
            Body = """
• Könilik, adalet anlamına gelir.

• Uzluk, iyilik anlamına gelir.

• Tüzlük, eşitlik anlamına gelir.

• İnsanlık, insan haklarına ve insan onuruna uygunluk anlamına gelir.
"""
        };
        return new Topic
        {
            Name = "İslamiyet Öncesi Türk Devletleri Kültür ve Medeniyeti",
            Description = "Devlet yönetimi, ordu, din, hukuk, ekonomi, dil-yazı ve sanat",
            Notes = { notKutAileleri, notEsikInanisi , notDinVeAhiret ,notDinAdamlari, notKutKucUlus, notKonilikUzlukTuzlukInsanlik },
            Questions = {
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                
                // --- SORU 526 ---
new Question
{
    Note = notKonilikUzlukTuzlukInsanlik,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Adalet anlamına gelen kavram hangisidir?",
    Explanation = "Könilik, adalet anlamına gelir.",
    OrderIndex = 526,
    Choices =
    {
        new Choice { Text = "Könilik", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Uzluk", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tüzlük", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "İnsanlık", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kut", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 527 ---
new Question
{
    Note = notKonilikUzlukTuzlukInsanlik,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İyilik anlamına gelen kavram hangisidir?",
    Explanation = "Uzluk, iyilik anlamına gelir.",
    OrderIndex = 527,
    Choices =
    {
        new Choice { Text = "Uzluk", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Könilik", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tüzlük", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "İnsanlık", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Küç", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 528 ---
new Question
{
    Note = notKonilikUzlukTuzlukInsanlik,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Eşitlik anlamına gelen kavram hangisidir?",
    Explanation = "Tüzlük, eşitlik anlamına gelir.",
    OrderIndex = 528,
    Choices =
    {
        new Choice { Text = "Tüzlük", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Könilik", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Uzluk", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "İnsanlık", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ülüş", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 529 ---
new Question
{
    Note = notKonilikUzlukTuzlukInsanlik,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İnsan haklarına ve insan onuruna uygunluk anlamına gelen kavram hangisidir?",
    Explanation = "İnsanlık, insan haklarına ve insan onuruna uygunluk anlamına gelir.",
    OrderIndex = 529,
    Choices =
    {
        new Choice { Text = "İnsanlık", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Könilik", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Uzluk", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Tüzlük", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kut", IsCorrect = false, OrderIndex = 5 }
    }
},
                
                // --- SORU 515 ---
new Question
{
    Note = notKutKucUlus,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde Gök Tanrı'nın hükümdara ve ailesine devleti yönetme yetkisi vermesine ne ad verilir?",
    Explanation = "Gök Tanrı'nın hükümdara ve ailesine devleti yönetme yetkisi vermesine kut adı verilmiştir.",
    OrderIndex = 515,
    Choices =
    {
        new Choice { Text = "Kut", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Küç", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ülüş", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Muyan", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Tamu", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 516 ---
new Question
{
    Note = notKutKucUlus,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde güç anlamına gelen ve askerî güçle ilişkilendirilen kavram hangisidir?",
    Explanation = "Küç, güç anlamına gelir ve askerî güçle ilişkilidir.",
    OrderIndex = 516,
    Choices =
    {
        new Choice { Text = "Küç", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kut", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ülüş", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Uçma", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Muyan", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 517 ---
new Question
{
    Note = notKutKucUlus,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde pay veya hisse anlamına gelen kavram hangisidir?",
    Explanation = "Ülüş, pay veya hisse anlamına gelir.",
    OrderIndex = 517,
    Choices =
    {
        new Choice { Text = "Ülüş", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kut", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Küç", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Tamu", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Sakış Günü", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 518 ---
new Question
{
    Note = notDinAdamlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde din adamlarına verilen ad hangisidir?",
    Explanation = "Kam veya Baksı, din adamlarına verilen isimdir.",
    OrderIndex = 518,
    Choices =
    {
        new Choice { Text = "Kam veya Baksı", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Otacı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tudun", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Aygucı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Bitikçi", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 519 ---
new Question
{
    Note = notDinAdamlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde eczacılıkla ilişkilendirilen kişiye ne ad verilir?",
    Explanation = "Otacı, eczacılıkla ilişkilendirilen kişidir.",
    OrderIndex = 519,
    Choices =
    {
        new Choice { Text = "Otacı", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kam", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tudun", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Şad", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Buyruk", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 520 ---
new Question
{
    Note = notDinAdamlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde din adamlarının kurultayda yer almaması ve ayrı bir dinî sınıf oluşturmaması hangi kavramla açıklanır?",
    Explanation = "Türklerde ruhban sınıfı bulunmamış ve din adamları kurultayda yer almamıştır.",
    OrderIndex = 520,
    Choices =
    {
        new Choice { Text = "Ruhban sınıfının bulunmaması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İkili teşkilat", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Kut anlayışı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Ülüş sistemi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kızılelma anlayışı", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 521 ---
new Question
{
    Note = notDinVeAhiret,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türk inancında cehenneme ne ad verilir?",
    Explanation = "Tamu, cehennem anlamına gelir.",
    OrderIndex = 521,
    Choices =
    {
        new Choice { Text = "Tamu", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Uçma", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Muyan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Ülüş", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kut", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 522 ---
new Question
{
    Note = notDinVeAhiret,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türk inancında cennete ne ad verilir?",
    Explanation = "Uçma, cennet anlamına gelir.",
    OrderIndex = 522,
    Choices =
    {
        new Choice { Text = "Uçma", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Tamu", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Muyan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sakış Günü", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Eşik", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 523 ---
new Question
{
    Note = notDinVeAhiret,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde sevap anlamına gelen kavram hangisidir?",
    Explanation = "Muyan, sevap anlamına gelir.",
    OrderIndex = 523,
    Choices =
    {
        new Choice { Text = "Muyan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Tamu", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Uçma", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kut", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Küç", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 524 ---
new Question
{
    Note = notDinVeAhiret,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türklerde ahiret ve hesap verme günü anlamına gelen kavram hangisidir?",
    Explanation = "Sakış Günü, ahiret ve hesap verme günü anlamına gelir.",
    OrderIndex = 524,
    Choices =
    {
        new Choice { Text = "Sakış Günü", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Muyan", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Uçma", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Tamu", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ülüş", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 525 ---
new Question
{
    Note = notEsikInanisi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İslamiyet öncesi Türk inancında kişinin ruhunun bulunduğuna inanılan yer neresidir?",
    Explanation = "Kişinin ruhunun eşikte bulunduğuna inanılmış, bu nedenle eşikte oturulmaması ve eşiğe basılmaması anlayışı oluşmuştur.",
    OrderIndex = 525,
    Choices =
    {
        new Choice { Text = "Eşik", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kurgan", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Otağ", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kurultay", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Toy", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notKutAileleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kut anlayışına göre Hunlarda Kök Tengri tarafından kut verilen aile hangisidir?",
    Explanation = "Hunlarda hükümdarlık yetkisinin Kök Tengri tarafından Tuk ailesine verildiği kabul edilmiştir.",
    OrderIndex = 512,
    Choices =
    {
        new Choice { Text = "Tuk ailesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Aşina ailesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yağakar ailesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Selçuk ailesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Osmanoğlu ailesi", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 513 ---
new Question
{
    Note = notKutAileleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kut anlayışına göre Göktürklerde Kök Tengri tarafından kut verilen aile hangisidir?",
    Explanation = "Göktürklerde hükümdarlık yetkisinin Kök Tengri tarafından Aşina ailesine verildiği kabul edilmiştir.",
    OrderIndex = 513,
    Choices =
    {
        new Choice { Text = "Aşina ailesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Tuk ailesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yağakar ailesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kayı ailesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Karahan ailesi", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 514 ---
new Question
{
    Note = notKutAileleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kut anlayışına göre Uygurlarda Kök Tengri tarafından kut verilen aile hangisidir?",
    Explanation = "Uygurlarda hükümdarlık yetkisinin Kök Tengri tarafından Yağakar ailesine verildiği kabul edilmiştir.",
    OrderIndex = 514,
    Choices =
    {
        new Choice { Text = "Yağakar ailesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Tuk ailesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Aşina ailesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kayı ailesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Avşar ailesi", IsCorrect = false, OrderIndex = 5 }
    }
}, },
        };
    }

    private static Topic BuildIlkTurkIslamDevletleri()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        var notKarahanlilarGenel = new Note
        {
            Title = "Devletler — Karahanlılar",
            Body = """
    • **Orta Asya'da kurulan ilk Müslüman Türk devletidir.**
    • Kurucusu **Bilge Kül Kadır Han**'dır.
    • İlk Müslüman olan hükümdarı **Satuk Buğra Han**'dır.
    • Satuk Buğra Han, Müslüman olduktan sonra **Abdülkerim** ismini almıştır.
    • **Karluk, Yağma, Çiğil ve Tuhsi** boyları tarafından kurulmuştur.
    • **Han ve Hakan** unvanlarını kullanmışlardır.
    • **İkili teşkilat** kullanmışlardır.
    • Resmî dilleri **Uygur Türkçesi ve Türkçe**dir.
    • Daha sonraki dönemlerde **Doğu Karahanlılar ve Batı Karahanlılar** olarak ayrılmıştır.
    • İpek Yolu'nun önemli bir bölümü Karahanlı topraklarından geçmiştir.
    • **Ribat** adı verilen kervansarayların ilk örneklerine Karahanlılarda rastlanmıştır.
    • Karahanlılarda ilk Türk-İslam yazılı edebî eserleri verilmiştir.
    • **Tamgaç Buğra Han** zamanında Semerkant Medresesi açılmıştır.
    • Semerkant Medresesinde dünya tarihinin **ilk burslu öğrencilik sistemi** başlatılmıştır.
    """
        };
        var notGaznelilerGenel = new Note
        {
            Title = "Devletler — Gazneliler",
            Body = """
    • **Afganistan'ın Gazne kentinde kurulmuştur.**
    • Diğer adı **Yeminliler Devleti**'dir.
    • **963 yılında kurulmuş, 1187 yılında yıkılmıştır.**
    • En önemli hükümdarı **Gazneli Mahmut**'tur.
    • Gazneli Mahmut'un babası **Sebük Tegin**'dir.
    • Gazneli Mahmut ve oğlu Mesut dönemlerinde **Hindistan'a birçok sefer** düzenlenmiştir.
    • Gazneliler, **Abbasileri korumuştur.**
    • Selçuklularla **Nesa, Serahs ve Dandanakan** savaşlarını yapmışlardır.
    • Bu üç savaşı da **Selçuklular kazanmıştır.**
    • **Dandanakan Savaşı'ndan sonra** yıkılış sürecine girmiştir.
    • Çok geniş bir coğrafyaya hükmetmeleri ve farklı milletleri bünyelerinde barındırmaları, **devlet-millet bağının zayıflamasına** neden olmuştur.
    • **Gurlular tarafından yıkılmıştır.**
    """
        };
        return new Topic
        {
            Name = "İlk Türk İslam Devletleri",
            Description = "Türk islam",
            Notes = { notKarahanlilarGenel, notGaznelilerGenel },
            Questions = {

            new Question
{
    Note = notKarahanlilarGenel,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Karahanlılarla ilgili aşağıdaki bilgilerden hangisi yanlıştır?",
    Explanation = "Karahanlılarla ilgili doğru bilgiler soru havuzunda yanlış cevap olarak yer almaktadır. Karahanlılarla ilgisi olmayan veya kaynakta verilen bilgilerle çelişen ifadeler doğru cevap havuzunu oluşturmaktadır.",
    OrderIndex = 28,
    Choices =
    {
        new Choice { Text = "Karahanlılar, Orta Asya'da kurulan ilk Müslüman Türk devletidir.", IsCorrect = false, OrderIndex = 1 },
        new Choice { Text = "Karahanlı Devleti'nin kurucusu Bilge Kül Kadır Han'dır.", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Karahanlılarda ilk Müslüman olan hükümdar Satuk Buğra Han'dır.", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Satuk Buğra Han, Müslüman olduktan sonra Abdülkerim ismini almıştır.", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Karahanlılar; Karluk, Yağma, Çiğil ve Tuhsi boyları tarafından kurulmuştur.", IsCorrect = false, OrderIndex = 5 },
        new Choice { Text = "Karahanlı hükümdarları Han ve Hakan unvanlarını kullanmıştır.", IsCorrect = false, OrderIndex = 6 },
        new Choice { Text = "Karahanlılarda ikili teşkilat uygulanmıştır.", IsCorrect = false, OrderIndex = 7 },
        new Choice { Text = "Karahanlıların resmî dilleri Uygur Türkçesi ve Türkçedir.", IsCorrect = false, OrderIndex = 8 },
        new Choice { Text = "Karahanlılar daha sonraki dönemlerde Doğu Karahanlılar ve Batı Karahanlılar olarak ayrılmıştır.", IsCorrect = false, OrderIndex = 9 },
        new Choice { Text = "İpek Yolu'nun önemli bir bölümü Karahanlı topraklarından geçmiştir.", IsCorrect = false, OrderIndex = 10 },
        new Choice { Text = "Ribat adı verilen kervansarayların ilk örneklerine Karahanlılarda rastlanmıştır.", IsCorrect = false, OrderIndex = 11 },
        new Choice { Text = "İlk Türk-İslam yazılı edebî eserleri Karahanlılarda verilmiştir.", IsCorrect = false, OrderIndex = 12 },
        new Choice { Text = "Tamgaç Buğra Han zamanında Semerkant Medresesi açılmıştır.", IsCorrect = false, OrderIndex = 13 },
        new Choice { Text = "Semerkant Medresesinde dünya tarihinin ilk burslu öğrencilik sistemi başlatılmıştır.", IsCorrect = false, OrderIndex = 14 },

        new Choice { Text = "Karahanlılar Anadolu'da kurulan ilk Müslüman Türk devleti olmuştur.", IsCorrect = true, OrderIndex = 15 },
        new Choice { Text = "Karahanlı Devleti'nin ilk Müslüman hükümdarı Bilge Kül Kadır Han'dır.", IsCorrect = true, OrderIndex = 16 },
        new Choice { Text = "Karahanlılar yalnızca tek merkezden yönetilmiş ve ikili teşkilat uygulamamıştır.", IsCorrect = true, OrderIndex = 17 },
        new Choice { Text = "Karahanlılarda ilk burslu öğrencilik sistemi değil, ilk devşirme sistemi başlatılmıştır.", IsCorrect = true, OrderIndex = 18 },
        new Choice { Text = "Karahanlı Devleti Osmanlı Devleti'nden sonra kurulmuştur.", IsCorrect = true, OrderIndex = 19 }
    }
},
            
            // --- SORU 10 ---
new Question
{
    Note = notGaznelilerGenel,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Gaznelilerle ilgili aşağıdaki bilgilerden hangisi yanlıştır?",
    Explanation = "Gaznelilerle ilgili kaynakta verilen doğru bilgiler yanlış cevap havuzunda, Gaznelilerle ilgili yanlış bilgiler ise doğru cevap havuzunda yer almaktadır.",
    OrderIndex = 40,
    Choices =
    {
        new Choice { Text = "Gazneliler Afganistan'ın Gazne kentinde kurulmuştur.", IsCorrect = false, OrderIndex = 1 },
        new Choice { Text = "Gaznelilerin diğer adı Yeminliler Devleti'dir.", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Gazneliler 963 yılında kurulmuş ve 1187 yılında yıkılmıştır.", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Gaznelilerin en önemli hükümdarı Gazneli Mahmut'tur.", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Gazneli Mahmut'un babası Sebük Tegin'dir.", IsCorrect = false, OrderIndex = 5 },
        new Choice { Text = "Gazneli Mahmut ve oğlu Mesut dönemlerinde Hindistan'a birçok sefer düzenlenmiştir.", IsCorrect = false, OrderIndex = 6 },
        new Choice { Text = "Gazneliler Abbasileri korumuştur.", IsCorrect = false, OrderIndex = 7 },
        new Choice { Text = "Gazneliler Selçuklularla Nesa, Serahs ve Dandanakan savaşlarını yapmıştır.", IsCorrect = false, OrderIndex = 8 },
        new Choice { Text = "Nesa, Serahs ve Dandanakan savaşlarının üçünü de Selçuklular kazanmıştır.", IsCorrect = false, OrderIndex = 9 },
        new Choice { Text = "Gazneliler Dandanakan Savaşı'ndan sonra yıkılış sürecine girmiştir.", IsCorrect = false, OrderIndex = 10 },
        new Choice { Text = "Gaznelilerin çok geniş bir coğrafyaya hükmetmesi ve farklı milletleri bünyesinde barındırması devlet-millet bağının zayıflamasına neden olmuştur.", IsCorrect = false, OrderIndex = 11 },
        new Choice { Text = "Gazneliler Gurlular tarafından yıkılmıştır.", IsCorrect = false, OrderIndex = 12 },

        new Choice { Text = "Gazneliler Anadolu'da kurulmuş ilk Müslüman Türk devletidir.", IsCorrect = true, OrderIndex = 13 },
        new Choice { Text = "Gaznelilerin en önemli hükümdarı Satuk Buğra Han'dır.", IsCorrect = true, OrderIndex = 14 },
        new Choice { Text = "Gazneliler Selçuklularla yaptıkları Nesa, Serahs ve Dandanakan savaşlarının tamamını kazanmıştır.", IsCorrect = true, OrderIndex = 15 },
        new Choice { Text = "Gazneliler Dandanakan Savaşı'ndan sonra güçlenerek en parlak dönemini yaşamıştır.", IsCorrect = true, OrderIndex = 16 },
        new Choice { Text = "Gazneliler Abbasiler tarafından yıkılmıştır.", IsCorrect = true, OrderIndex = 17 }
    }
},

            },
        };
    }

    private static Topic BuildIlkTurkIslamDevletleriKulturVeMedeniyeti()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        return new Topic
        {
            Name = "İlk Türk İslam Devletleri Kültür ve Medeniyeti",
            Description = "...",
            Notes = { },
            Questions = { },
        };
    }

    private static Topic BuildAnadoluSelcukluDevleti()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },

        var notAnadoluSelcukluEnParlakDonem = new Note
        {
            Title = "Anadolu Selçuklu Devleti — En Parlak Dönem",
            Body = """
• Anadolu Selçuklu Devleti en parlak dönemini Alaaddin Keykubat zamanında yaşamıştır.

• Alaaddin Keykubat, 1220-1237 yılları arasında 17 yıl hükümdarlık yapmıştır.
"""
        }; var notAnadoluSelcukluIlkleri = new Note
        {
            Title = "Anadolu Selçuklu Devleti — Mimari İlkler",
            Body = """
• Anadolu Selçuklu Devleti'nin ilk hanı Alayhan'dır.

• Anadolu Selçuklu Devleti'nin ilk camisi Konya Alaaddin Camii'dir.

• Anadolu Selçuklu Devleti'nin ilk medresesi Kayseri'deki Koca Hasan Medresesi'dir.
"""
        }; var notBirinciKilicArslan = new Note
        {
            Title = "Anadolu Selçuklu Devleti — I. Kılıç Arslan Dönemi",
            Body = """
• I. Haçlı Seferi sırasında hükümdardır.

• I. Haçlı Seferi sonrasında İznik kaybedilmiştir.

• I. Dorileon Savaşı'nda Haçlılarla mücadele etmiştir.

• Devlet merkezini İznik'ten Konya'ya taşımıştır.
"""
        }; var notBirinciMesud = new Note
        {
            Title = "Anadolu Selçuklu Devleti — I. Mesud Dönemi",
            Body = """
• II. Haçlı Seferi I. Mesud döneminde yaşanmıştır.

• Eskişehir'deki II. Dorileon Muharebesi'nde Haçlılar mağlup edilmiştir.
"""
        }; var notIkinciKilicArslan = new Note
        {
            Title = "Anadolu Selçuklu Devleti — II. Kılıç Arslan Dönemi",
            Body = """
• Danişmentlilere Malatya'da son vermiştir.

• 1176 Miryokefalon Zaferi'ni Bizans'a karşı kazanmıştır.

• Ülke topraklarını sağlığında 11 oğlu arasında paylaştırmıştır.

• Bu paylaşım taht kavgalarına yol açmıştır.
"""
        }; var notBirinciGiyaseddinKeyhusrev = new Note
        {
            Title = "Anadolu Selçuklu Devleti — I. Gıyaseddin Keyhüsrev Dönemi",
            Body = """
• Farklı tarihlerde iki defa tahta çıkmıştır.

• İlk hükümdarlığı 1192-1196 yılları arasındadır.

• İkinci hükümdarlığı 1205-1211 yılları arasındadır.

• Antalya'yı almıştır.
"""
        }; var notBirinciIzzeddinKeykavus = new Note
        {
            Title = "Anadolu Selçuklu Devleti — I. İzzeddin Keykavus Dönemi",
            Body = """
• Sinop ve Samsun'u almıştır.
"""
        }; var notBirinciAlaaddinKeykubat = new Note
        {
            Title = "Anadolu Selçuklu Devleti — I. Alaaddin Keykubat Dönemi",
            Body = """
• Anadolu Selçuklu Devleti en parlak dönemini onun zamanında yaşamıştır.

• Suğdak Limanı'nı almıştır.

• Kolonoros'u fethederek Alaiye adını vermiştir; burası günümüzde Alanya'dır.

• Harezmşahların Ahlat'a saldırması üzerine Yassıçemen Savaşı yapılmıştır.

• Yassıçemen Savaşı'nın kazanılmasından sonra Moğollarla komşu olunmuştur.

• Moğol tehlikesine karşı surlar yaptırmaya başlamıştır.
"""
        }; var notIkinciGiyaseddinKeyhusrev = new Note
        {
            Title = "Anadolu Selçuklu Devleti — II. Gıyaseddin Keyhüsrev Dönemi",
            Body = """
• Sadeddin Köpek'i görevlendirmiştir.

• Sadeddin Köpek'in çeşitli devlet adamlarını uzaklaştırması devletin zayıflamasına neden olmuştur.

• 1240-1242 yıllarında Baba İshak İsyanı yaşanmıştır.

• 1243 Kösedağ Savaşı'nda Moğollara karşı savaşılmış ve savaş kaybedilerek Tokat'a çekilinmiştir.

• Kösedağ yenilgisinden sonra Anadolu Selçuklu Devleti önce Moğollara, daha sonra İlhanlılara tabi hâle gelmiştir.
"""
        };





        return new Topic
        {
            Name = "Anadolu Selçuklu Devleti",
            Description = "...",
            Notes = { notAnadoluSelcukluEnParlakDonem, notAnadoluSelcukluIlkleri , notIkinciGiyaseddinKeyhusrev,notBirinciAlaaddinKeykubat, notBirinciIzzeddinKeykavus, notBirinciKilicArslan, notBirinciMesud , notBirinciGiyaseddinKeyhusrev,notIkinciKilicArslan },
            Questions = {




















                new Question
{
    Note = notIkinciGiyaseddinKeyhusrev,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sadeddin Köpek'i görevlendiren ve onun çeşitli devlet adamlarını uzaklaştırması sonucunda devletin zayıfladığı Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "Sadeddin Köpek, II. Gıyaseddin Keyhüsrev döneminde görevlendirilmiş ve devlet adamlarını uzaklaştırması devletin zayıflamasına neden olmuştur.",
    OrderIndex = 78,
    Choices =
    {
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notIkinciGiyaseddinKeyhusrev,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1240-1242 yıllarında yaşanan Baba İshak İsyanı hangi Anadolu Selçuklu hükümdarı döneminde çıkmıştır?",
    Explanation = "Baba İshak İsyanı, II. Gıyaseddin Keyhüsrev döneminde 1240-1242 yılları arasında yaşanmıştır.",
    OrderIndex = 79,
    Choices =
    {
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notIkinciGiyaseddinKeyhusrev,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1243 Kösedağ Savaşı'nda Moğollara karşı savaşıp yenilerek Tokat'a çekilen ve bu yenilgi sonrasında Anadolu Selçuklu Devleti'nin önce Moğollara, daha sonra İlhanlılara tabi hâle geldiği dönemin hükümdarı kimdir?",
    Explanation = "Kösedağ Savaşı II. Gıyaseddin Keyhüsrev döneminde kaybedilmiş, savaş sonrasında Anadolu Selçuklu Devleti önce Moğollara, daha sonra İlhanlılara tabi hâle gelmiştir.",
    OrderIndex = 80,
    Choices =
    {
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notBirinciAlaaddinKeykubat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti'nin en parlak dönemini yaşadığı hükümdar kimdir?",
    Explanation = "Anadolu Selçuklu Devleti en parlak dönemini I. Alaaddin Keykubat zamanında yaşamıştır.",
    OrderIndex = 74,
    Choices =
    {
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBirinciAlaaddinKeykubat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Suğdak Limanı'nı alan Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "Suğdak Limanı I. Alaaddin Keykubat döneminde alınmıştır.",
    OrderIndex = 75,
    Choices =
    {
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBirinciAlaaddinKeykubat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kolonoros'u fethederek buraya Alaiye adını veren Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "I. Alaaddin Keykubat Kolonoros'u fethederek buraya Alaiye adını vermiştir.",
    OrderIndex = 76,
    Choices =
    {
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},

                new Question
{
    Note = notBirinciAlaaddinKeykubat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Harezmşahların Ahlat'a saldırması üzerine Yassıçemen Savaşı'nın yapılması, bu savaşın kazanılmasından sonra Moğollarla komşu olunması ve Moğol tehlikesine karşı surlar yaptırılması hangi Anadolu Selçuklu hükümdarı döneminde yaşanmıştır?",
    Explanation = "Yassıçemen Savaşı I. Alaaddin Keykubat döneminde yapılmış, zaferden sonra Moğollarla komşu olunmuş ve Moğol tehlikesine karşı surlar yaptırılmaya başlanmıştır.",
    OrderIndex = 77,
    Choices =
    {
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notBirinciIzzeddinKeykavus,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sinop ve Samsun'u alan Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "Sinop ve Samsun, I. İzzeddin Keykavus döneminde alınmıştır.",
    OrderIndex = 73,
    Choices =
    {
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},

                new Question
{
    Note = notBirinciGiyaseddinKeyhusrev,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Farklı tarihlerde iki defa tahta çıkan ve Antalya'yı alan Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "I. Gıyaseddin Keyhüsrev iki farklı dönemde tahta çıkmış ve Antalya'yı almıştır.",
    OrderIndex = 72,
    Choices =
    {
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notIkinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Danişmentlilere Malatya'da son veren Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "Danişmentlilere Malatya'da II. Kılıç Arslan son vermiştir.",
    OrderIndex = 69,
    Choices =
    {
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notIkinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1176 Miryokefalon Zaferi'ni Bizans'a karşı kazanan Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "1176 Miryokefalon Zaferi, II. Kılıç Arslan döneminde Bizans'a karşı kazanılmıştır.",
    OrderIndex = 70,
    Choices =
    {
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notIkinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Ülke topraklarını sağlığında 11 oğlu arasında paylaştırarak taht kavgalarının yaşanmasına yol açan Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "II. Kılıç Arslan'ın ülke topraklarını 11 oğlu arasında paylaştırması taht kavgalarına yol açmıştır.",
    OrderIndex = 71,
    Choices =
    {
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notBirinciMesud,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "II. Haçlı Seferi'nin yaşandığı ve Eskişehir'deki II. Dorileon Muharebesi'nde Haçlıların mağlup edildiği dönemin Anadolu Selçuklu hükümdarı kimdir?",
    Explanation = "II. Haçlı Seferi ve Eskişehir'deki II. Dorileon Muharebesi I. Mesud döneminde yaşanmıştır.",
    OrderIndex = 68,
    Choices =
    {
        new Choice { Text = "I. Mesud", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Kılıç Arslan", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notBirinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "I. Haçlı Seferi hangi Anadolu Selçuklu hükümdarı zamanında yaşanmıştır?",
    Explanation = "I. Haçlı Seferi sırasında Anadolu Selçuklu Devleti'nin hükümdarı I. Kılıç Arslan'dır.",
    OrderIndex = 64,
    Choices =
    {
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBirinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "I. Haçlı Seferi sonrasında İznik'in kaybedilmesi hangi Anadolu Selçuklu hükümdarı zamanında yaşanmıştır?",
    Explanation = "İznik, I. Haçlı Seferi sonrasında I. Kılıç Arslan döneminde kaybedilmiştir.",
    OrderIndex = 65,
    Choices =
    {
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBirinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "I. Dorileon Savaşı'nda Haçlılarla mücadele edilmesi hangi Anadolu Selçuklu hükümdarı zamanında yaşanmıştır?",
    Explanation = "I. Dorileon Savaşı'nda Haçlılarla mücadele I. Kılıç Arslan döneminde yaşanmıştır.",
    OrderIndex = 66,
    Choices =
    {
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. İzzeddin Keykavus", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBirinciKilicArslan,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti'nin merkezinin İznik'ten Konya'ya taşınması hangi hükümdar zamanında gerçekleşmiştir?",
    Explanation = "Devlet merkezi I. Kılıç Arslan döneminde İznik'ten Konya'ya taşınmıştır.",
    OrderIndex = 67,
    Choices =
    {
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Mesud", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Gıyaseddin Keyhüsrev", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "I. Alaaddin Keykubat", IsCorrect = false, OrderIndex = 5 }
    }
},



                new Question
{
    Note = notAnadoluSelcukluEnParlakDonem,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti en parlak dönemini hangi hükümdar zamanında yaşamıştır?",
    Explanation = "Anadolu Selçuklu Devleti'nin en parlak dönemi Alaaddin Keykubat zamanında yaşanmıştır.",
    OrderIndex = 60,
    Choices =
    {
        new Choice { Text = "Alaaddin Keykubat", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "I. Gıyasettin Keyhüsrev", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "I. İzzettin Keykavus", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "I. Kılıç Arslan", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "II. Kılıç Arslan", IsCorrect = false, OrderIndex = 5 }
    }
},
            new Question
{
    Note = notAnadoluSelcukluIlkleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti'nin ilk hanı hangisidir?",
    Explanation = "Anadolu Selçuklu Devleti'nin ilk hanı Alayhan'dır.",
    OrderIndex = 61,
    Choices =
    {
        new Choice { Text = "Alayhan", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Sultanhanı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ağzıkara Han", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Zazadin Han", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Karatay Han", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notAnadoluSelcukluIlkleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti'nin ilk camisi hangisidir?",
    Explanation = "Anadolu Selçuklu Devleti'nin yaptığı ilk cami Konya Alaaddin Camii'dir.",
    OrderIndex = 62,
    Choices =
    {
        new Choice { Text = "Konya Alaaddin Camii", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Niğde Alaaddin Camii", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Kayseri Ulu Camii", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sivas Ulu Camii", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Malatya Ulu Camii", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notAnadoluSelcukluIlkleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti'nin ilk medresesi hangisidir?",
    Explanation = "Anadolu Selçuklu Devleti'nin ilk medresesi Kayseri'de bulunan Koca Hasan Medresesi'dir.",
    OrderIndex = 63,
    Choices =
    {
        new Choice { Text = "Koca Hasan Medresesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Karatay Medresesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Çifte Minareli Medrese", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Gök Medrese", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "İnce Minareli Medrese", IsCorrect = false, OrderIndex = 5 }
    }
},

            },
        };
    }

    private static Topic BuildOsmanliDevletiKurulusDonemi()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        return new Topic
        {
            Name = "Osmanlı Devleti Kuruluş Dönemi",
            Description = "...",
            Notes = { },
            Questions = { },
        };
    }

    private static Topic BuildOsmanliDevletiYukselmeDonemi()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        return new Topic
        {
            Name = "Osmanlı Devleti Yükselme Dönemi",
            Description = "...",
            Notes = { },
            Questions = { },
        };
    }

    private static Topic BuildOsmanliDevletiKulturVeMedeniyeti()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        return new Topic
        {
            Name = "Osmanlı Devleti Kültür ve Medeniyeti",
            Description = "...",
            Notes = { },
            Questions = { },
        };
    }

    private static Topic BuildOsmanliDevletiDuraklamaDonemi()
    {
        var notDuraklamaGenel = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Genel Durum",
            Body = """
• **Duraklama Dönemi**, Osmanlı kaynaklarında tereddi ve tagayyür, yani bozulma ve yozlaşma olarak ifade edilmiştir.
• Bozulma ve yozlaşma **17. yüzyıldan itibaren** belirginleşmiştir.
• Duraklama, sınırların tamamen genişlememesinden çok **devletin iç mekanizmalarının bozulması** anlamına gelir.
• Osmanlı Devleti duraklama döneminde de geniş sınırlara ulaşmıştır.
"""
        };

        var notDuraklamaNedenleri = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Duraklamanın Nedenleri",
            Body = """
• **Merkezi otoritenin bozulması** duraklamanın temel nedenlerinden biridir.
• Küçük ve deneyimsiz padişahların tahta çıkması devlet yönetimini zayıflatmıştır.
• Saray kadınlarının devlet yönetimine karışması etkili olmuştur.
• Saray masrafları artmış, rüşvet ve adam kayırma yaygınlaşmıştır.
• Ganimet gelirleri azalmış, savaşlar uzamış ve mağlubiyetler artmıştır.
• Sık padişah değişikliği ve cülus bahşişlerinin artması devlet hazinesine zarar vermiştir.
• **Doğal sınırlara ulaşılması**, fetihlerin yavaşlamasına neden olmuştur.
• Kapitülasyonların yaygınlaşması, devlet gelirlerini olumsuz etkilemiştir.
• Coğrafi Keşifler sonucunda **İpek ve Baharat yolları önem kaybetmiştir**.
• Avrupa bilim ve teknoloji alanında ilerlerken Osmanlı bu gelişmelere ayak uyduramamıştır.
• Tımar sisteminin bozulması ve iltizam sisteminin yaygınlaşması duraklamayı hızlandırmıştır.
• İsyanların çıkması ve devlet otoritesinin zayıflaması duraklamayı derinleştirmiştir.
"""
        };

        var notKafesVeEkberErsed = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Kafes Usulü ve Ekber ve Erşed",
            Body = """
• **III. Mehmet**, sancağa çıkma sistemini kaldırmıştır.
• III. Mehmet, sancaktan yetişerek tahta çıkan **son Osmanlı padişahıdır**.
• Şehzadelerin tutulduğu kafesin diğer adı **Şimşirlik**tir.
• Kafes usulü, şehzadelerin devlet yönetimi konusunda deneyimsiz yetişmesine neden olmuştur.
• Kafesten yetişerek tahta çıkan ilk padişah **I. Ahmet**tir.
• I. Ahmet, veraset sisteminde son değişikliği yaparak **Ekber ve Erşed sistemini** getirmiştir.
• Ekber, **en büyük**; erşed ise **en akıllı ve olgun** anlamına gelir.
"""
        };

        var notYeniCeriBozulmasi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Yeniçeri Ocağının Bozulması",
            Body = """
• Yeniçeriler kuruluşta **ocak devlet içindir** anlayışıyla hareket ederken zamanla bu anlayış bozulmuştur.
• Devşirme Kanunu'na aykırı kişilerin ocağa alınması yeniçeri disiplinini bozmuştur.
• Yeniçerilerin evlenmesi, ticaret ve esnaflık yapması askerî disiplinin bozulmasına neden olmuştur.
• Ulufelerin düşük ayarda verilmesi ve cülus bahşişi beklentisi isyanlara yol açmıştır.
• Yeniçeriler sık sık padişah değiştirmeye başlamıştır.
• Tımar sisteminin bozulmasıyla İstanbul'a gelen tımarlı sipahilerin bir kısmı yeniçeri ocağına katılmış ve ocağın sayısı artmıştır.
"""
        };

        var notMedreseVeBesikUlemasi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Medreselerin Bozulması",
            Body = """
• **Beşik ulemalığı**, alimin oğlunun alim sayılması anlayışına dayanır.
• Beşik ulemalığı, medreselerde liyakat ve eğitim kalitesini olumsuz etkilemiştir.
• Medreselerden pozitif bilimlerin çıkarılması bilimsel gerilemeye yol açmıştır.
• Kapasitenin üzerinde öğrenci alınması medrese düzenini bozmuştur.
• Rüşvet ve iltimasın yaygınlaşması medrese sistemini olumsuz etkilemiştir.
"""
        };

        var notTimarBozulmasi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Tımar Sisteminin Bozulması",
            Body = """
• Tımar sisteminin temel amaçlarından biri **toprağın boş kalmasını önlemek**tir.
• Tımar sistemi düzenli vergi toplanmasını, memur maaşlarının karşılanmasını ve masrafsız asker yetiştirilmesini sağlamıştır.
• Tımarların sipahiler dışında kişilere verilmesi sistemi bozmuştur.
• Tımarların özel mülk veya vakfa çevrilmesi tımar sistemini zayıflatmıştır.
• Rüşvet karşılığı dirlik verilmesi ve dirliklerin para ile alınıp satılması bozulmayı hızlandırmıştır.
• Nüfus artışı, enflasyon, Avrupa'nın silah teknolojisine uyum sağlanamaması ve uzun savaşlar da tımar sistemini olumsuz etkilemiştir.
• Tımar sisteminin bozulmasıyla **iltizam sistemi yaygınlaşmıştır**.
"""
        };

        var notVenedikGirit = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Venedik ve Girit",
            Body = """
• Osmanlı Devleti, Venedik'e ait **Girit Adası'nı 1645-1669 yılları arasında 24 yıl kuşatmıştır**.
• Girit, 24 yıl süren kuşatmanın sonunda fethedilmiştir.
• Girit Kuşatması sırasında Venedik'in Boğazlar ve çevresini ablukaya alması İstanbul'da kıtlığa neden olmuştur.
• Venedik ablukasını **Köprülü Mehmet Paşa** kaldırmıştır.
• Girit Kuşatması'nı başarıyla sonuçlandıran devlet adamı **Köprülü Fazıl Ahmet Paşa**dır.
"""
        };

        var notRusyaBahcesaray = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Rusya ve Bahçesaray Antlaşması",
            Body = """
• **1681 Bahçesaray Antlaşması**, diğer adıyla Çehrin Antlaşması'dır.
• Bahçesaray Antlaşması, **ilk Osmanlı-Rus antlaşmasıdır**.
• Osmanlı-Rus ilişkilerinde Rusya'nın sıcak denizlere inme politikası önemli bir etkendir.
"""
        };

        var notIranKasriSirin = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — İran ve Kasr-ı Şirin Antlaşması",
            Body = """
• Osmanlı-İran ilişkilerinde Nasuh Paşa ve Serav Antlaşmaları sınır düzenlemeleriyle ilgilidir.
• IV. Murat, İran üzerine **iki Irak Seferi** düzenlemiştir.
• **1639 Kasr-ı Şirin Antlaşması** Osmanlı Devleti ile Safeviler arasında imzalanmıştır.
• Kasr-ı Şirin Antlaşması ile **Bağdat Osmanlı Devleti'nde kalmıştır**.
• Kasr-ı Şirin Antlaşması, günümüz Türkiye-İran sınırını büyük ölçüde belirlemiştir.
• Bu antlaşma, **Türkiye'nin doğudaki en eski sınırını belirleyen antlaşma** olarak kabul edilir.
"""
        };

        var notLehistanHotinBucas = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Lehistan, Hotin ve Bucaş",
            Body = """
• **1621 Hotin Seferi** sırasında yeniçerilerin isteksiz ve disiplinsiz davranması belirginleşmiştir.
• Hotin Seferi'ndeki yeniçeri disiplinsizliği, II. Osman'ın Yeniçeri Ocağı'nı kaldırmayı düşünmesine neden olmuştur.
• II. Osman, yeni bir ordu kurmayı planlamış ancak bu düşüncesini gerçekleştirememiştir.
• II. Osman, Yeniçeri Ocağı'nı kaldırma düşüncesi nedeniyle isyan eden yeniçeriler tarafından Yedikule Zindanları'nda öldürülmüştür.
• **1672 Bucaş Antlaşması** ile Osmanlı Devleti batıda en geniş sınırlara ulaşmıştır.
• Podolya, Bucaş Antlaşması ile Osmanlı Devleti'nin eline geçmiştir.
"""
        };

        var notAvusturya = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Avusturya ile İlişkiler",
            Body = """
• **1596 Haçova Meydan Muharebesi** Osmanlı Devleti tarafından kazanılmıştır.
• Haçova Savaşı, geri hizmette bulunanların kepçe ve kazan gibi araçlarla savaşa katılması nedeniyle **Kepçe-Kazan Savaşı** olarak da anılır.
• Haçova Savaşı sonrasında Eğri, Estergon ve Kanije kaleleri alınmıştır.
• Kanije savunmasındaki başarısından dolayı **Tiryaki Hasan Paşa**, Kanije Kahramanı olarak tanınmıştır.
• **1606 Zitvatorok Antlaşması** Avusturya ile imzalanmıştır.
• Zitvatorok Antlaşması ile Osmanlı padişahı ile Avusturya hükümdarı siyasi bakımdan eşit kabul edilmiştir.
• Bu antlaşma ile Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğü sona ermiştir.
• **1664 Vasvar Antlaşması**, Osmanlı Devleti'nin Avusturya ile bu dönemde yaptığı en kazançlı antlaşma olarak kabul edilir.
• Uyvar Kalesi'nin savunması Avrupa'da **"Uyvar önünde güçlü bir Türk gibi"** sözüyle anılmıştır.
"""
        };

        var notIkinciViyanaVeKarlovca = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — II. Viyana Kuşatması ve Karlovça",
            Body = """
• **1683 II. Viyana Kuşatması** Osmanlı Devleti'nin başarısızlığıyla sonuçlanmıştır.
• Kuşatmanın başarısız olmasından sonra **Merzifonlu Kara Mustafa Paşa idam edilmiştir**.
• Osmanlı Devleti, 1697 yılında **Zenta Savaşı'nda yenilmiştir**.
• **1699 Karlofça Antlaşması** ile Osmanlı Devleti batıda ilk kez büyük çaplı toprak kaybetmiştir.
• Karlofça Antlaşması, Osmanlı Devleti'nin duraklama döneminin sona erip gerileme döneminin başlamasının önemli göstergelerindendir.
• Karlofça Antlaşması'nın devamı niteliğinde Rusya ile **1700 İstanbul Antlaşması** imzalanmıştır.
• İstanbul Antlaşması ile **Azak Kalesi Rusya'ya bırakılmış**, Rusya Karadeniz'e ilk kez inme fırsatı elde etmiştir.
"""
        };

        var notIstanbulIsyanlari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — İstanbul İsyanları",
            Body = """
• İstanbul isyanlarının temel nedenlerinden biri **merkezi otoritenin bozulmasıdır**.
• Devşirme Kanunu'na aykırı asker alınması ve yeniçerilerin devlet yönetimine karışması isyanları artırmıştır.
• Ulufelerin düşük ayarda verilmesi ve cülus bahşişi için padişah değiştirilmesi önemli nedenlerdendir.
• Yeniçerilerin evlenmesi ve ticaret yapması askerî disiplinin bozulmasına neden olmuştur.
• İstanbul isyanları sonucunda can ve mal güvenliği kalmamış, merkezi otorite daha da zayıflamıştır.
• II. Osman'ın öldürülmesi İstanbul isyanlarının önemli sonuçlarından biridir.
• **1656 Vak'a-i Vakvakiye**, diğer adıyla Çınar Vakası, IV. Mehmet döneminde yaşanmıştır.
• Vak'a-i Vakvakiye'de yaklaşık 30 devlet adamı çınar ağaçlarına asılmıştır.
"""
        };

        var notCelaliIsyanlari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Celali İsyanları",
            Body = """
• Celali İsyanları, Anadolu'da ortaya çıkan ve büyük ölçüde ekonomik ve sosyal sorunlara dayanan isyanlardır.
• İlk Celali ayaklanmasının öncüsü **1519'da Yavuz Sultan Selim döneminde isyan eden Şeyh Celal**dir.
• Şeyh Celal'den sonra Anadolu'da çıkan benzer nitelikteki isyanlara Celali İsyanları denilmiştir.
• Ekonominin bozulması, fiyatların artması, ağır vergiler ve enflasyon Celali İsyanlarının nedenlerindendir.
• Tımar sisteminin bozulması ve iltizam sisteminin yaygınlaşması isyanları artırmıştır.
• Uzun savaşlar ve yöneticilerin halka kötü davranması isyanların nedenlerindendir.
• Haçova Meydan Muharebesi'nden kaçan bazı askerlerin eşkıya olması da Celali İsyanlarını artırmıştır.
• Karayazıcı, Kalenderoğlu, Kör Mahmut, Tavil Ahmet, Canbulatoğlu, Deli Hasan ve Katırcıoğlu önemli Celali isyancılarındandır.
"""
        };

        var notCelaliSonuclari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Celali İsyanlarının Sonuçları",
            Body = """
• Celali İsyanları sonucunda tarımsal üretim düşmüş ve vergi gelirleri azalmıştır.
• Köyden kente göç artmıştır.
• **1603-1610 yılları arasında Anadolu'dan büyük şehirlere gerçekleşen yoğun göç hareketine Büyük Kaçgun denir**.
• Boşalan köylere eşkıyalar yerleşmiştir.
• Anadolu'da can ve mal güvenliği kalmamıştır.
• Şehirlerde işsizlik ve suç oranları artmıştır.
"""
        };

        var notEyaletVeSuhte = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Eyalet ve Suhte İsyanları",
            Body = """
• Eyalet isyanlarının nedenleri arasında merkezi otoritenin ve eyaletlerde devlet otoritesinin zayıflaması bulunur.
• Devlet yöneticilerinin halka kötü davranması eyalet isyanlarını artırmıştır.
• Bu isyanlar, 1789 Fransız İhtilali'nden önce gerçekleştiği için Fransız İhtilali'nin yaydığı ulusçuluk akımıyla ilgili değildir.
• Genç Osman'ın öldürülmesi üzerine Abaza Mehmet Paşa isyan etmiş, Erzurum Valiliği verilerek isyan bastırılmıştır.
• Osmanlı Devleti'nde medrese öğrencilerine **Suhte veya Softa** denilmiştir.
• Suhte isyanlarının nedenleri arasında ulema çocuklarının kayrılması, rüşvet ve iltimas, kapasitenin üzerinde öğrenci alınması ve medrese gelirlerinin azalması vardır.
• İsyancı medrese öğrencilerine karşı güç kullanılması çok sayıda can kaybına ve eğitimli insan sayısının azalmasına yol açmıştır.
"""
        };

        var notIslahatGenel = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — XVII. Yüzyıl Islahatlarının Genel Özellikleri",
            Body = """
• Islahatın diğer adı **reform**dur.
• XVII. yüzyıl ıslahatlarında **Avrupa örnek alınmamıştır**.
• Islahatlarda sorunların köküne inilememiştir.
• Islahatlar kişilere bağlı kalmış ve padişahın ölümünden sonra devamlılık sağlanamamıştır.
• Halkın desteği alınmamıştır.
• Saray, ulema ve asker kendi çıkarları zedelendiği için ıslahatlara karşı çıkmıştır.
• Fatih ve Kanuni dönemlerindeki eski düzene, yani **Kanun-ı Kadim'e dönülmek istenmiştir**.
• Islahatlar çoğu zaman baskı ve şiddet yoluyla benimsetilmeye çalışılmıştır.
"""
        };

        var notTarhuncu = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Tarhuncu Ahmet Paşa",
            Body = """
• Tarhuncu Ahmet Paşa, Osmanlı Devleti'nde **ilk denk bütçeyi** hazırlamıştır.
• Denk bütçe ile gelir ve giderler eşitlenmiştir.
• Saray harcamalarını ve gereksiz giderleri azaltmaya çalışmıştır.
• Saray çevresinin çıkarlarının zedelenmesi nedeniyle görevden uzaklaştırılmış ve idam edilmiştir.
"""
        };

        var notGençOsman = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Genç Osman",
            Body = """
• Genç Osman, Yeniçeri Ocağı'nı kaldırmayı düşünmüştür.
• Bu düşüncenin oluşmasında **Hotin Seferi'nde yeniçerilerin isteksiz davranması** etkili olmuştur.
• Yeni bir ordu kurmayı planlamıştır.
• Başkenti İstanbul'dan başka bir şehre taşımayı düşünmüştür.
• Harem dışı evlilik yaparak Şeyhülislam'ın kızıyla evlenmiştir.
• Yeniçeriler tarafından Yedikule Zindanları'nda öldürülmüştür.
"""
        };

        var notKuyucuMurat = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Kuyucu Murat Paşa",
            Body = """
• Kuyucu Murat Paşa, **I. Ahmet'in sadrazamıdır**.
• Celali İsyanlarını şiddet ve baskı yoluyla bastırmıştır.
• Celali İsyanlarının bastırılmasıyla devlet otoritesinin yeniden kurulmasına katkı sağlamıştır.
"""
        };

        var notDorduncuMurat = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — IV. Murat",
            Body = """
• IV. Murat, merkezi otoriteyi yeniden güçlendirmeye çalışmıştır.
• Saray kadınlarını devlet yönetiminden uzaklaştırmıştır.
• Osmanlı tarihinde **ilk defa bir Şeyhülislam idam ettirmiştir**.
• İlk defa gece sokağa çıkma yasağı uygulamıştır.
• İçki ve tütün kullanımını yasaklamıştır.
• Bu yasakların temel amacı **büyük İstanbul yangınlarını önlemektir**.
• İran üzerine iki Irak Seferi düzenlemiştir.
• IV. Murat'ın lakaplarından biri **Bağdat Fatihi**dir.
• Evliya Çelebi, Katip Çelebi ve Nefi bu dönemin önemli şahsiyetlerindendir.
"""
        };

        var notKociBey = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Koçi Bey ve Layiha",
            Body = """
• Koçi Bey, Osmanlı Devleti'nin sorunlarını ve çözüm önerilerini içeren raporlar hazırlamıştır.
• Bu raporlar **risale veya layiha** olarak adlandırılır.
• Layiha, devletin sorunlarını açıklamanın yanında çözüm önerileri de içerir.
• Koçi Bey, IV. Murat'a ve I. İbrahim'e ayrı ayrı risaleler sunmuştur.
"""
        };

        var notKoprululer = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Köprülüler",
            Body = """
• Köprülü Mehmet Paşa, IV. Mehmet döneminde saraya bazı şartlar öne sürerek sadrazam olmuştur.
• Maliye düzenini düzeltmeye ve Yeniçeri Ocağı'nı disiplin altına almaya çalışmıştır.
• Venedik'in Çanakkale Boğazı ablukasını kaldırmıştır.
• Köprülü Fazıl Ahmet Paşa, Girit Kuşatması'nı başarıyla sonuçlandırmıştır.
• Merzifonlu Kara Mustafa Paşa, II. Viyana Kuşatması'nın başarısız olması üzerine idam edilmiştir.
• Köprülüler, Osmanlı Devleti'ne **duraklama dönemi içinde geçici bir yükselme** yaşatmıştır.
"""
        };

        var notZitvatorokAntlasmasi = new Note
        {
            Title = "Devletler — Zitvatorok Antlaşması",
            Body = """
    • **Zitvatorok Antlaşması** ile Osmanlı padişahı ile Avusturya hükümdarı siyasi bakımdan eşit kabul edilmiştir.
    • Bu antlaşma ile Osmanlı Devleti'nin **Orta Avrupa'daki siyasi üstünlüğü sona ermiştir.**
    """
        };

        return new Topic
        {
            Name = "Osmanlı Devleti Duraklama Dönemi",
            Description = "...",
            Notes =
        {
            notDuraklamaGenel,
            notDuraklamaNedenleri,
            notKafesVeEkberErsed,
            notYeniCeriBozulmasi,
            notMedreseVeBesikUlemasi,
            notTimarBozulmasi,
            notVenedikGirit,
            notRusyaBahcesaray,
            notIranKasriSirin,
            notLehistanHotinBucas,
            notAvusturya,
            notIkinciViyanaVeKarlovca,
            notIstanbulIsyanlari,
            notCelaliIsyanlari,
            notCelaliSonuclari,
            notEyaletVeSuhte,
            notIslahatGenel,
            notTarhuncu,
            notGençOsman,
            notKuyucuMurat,
            notDorduncuMurat,
            notKociBey,
            notKoprululer,notZitvatorokAntlasmasi

        },
            Questions =
        {






















new Question
{
    Note = notIkinciViyanaVeKarlovca,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti batıda ciddi oranda toprak kaybetmiş ve bu gelişme gerileme döneminin başlangıcının önemli göstergelerinden biri olmuştur.\n\nBu bilgiler aşağıdaki antlaşmalardan hangisine aittir?",
    Explanation = "1699 Karlofça Antlaşması ile Osmanlı Devleti batıda ciddi oranda toprak kaybetmiştir. Antlaşma, duraklama döneminin sona erip gerileme döneminin başlamasının önemli göstergelerinden biridir.",
    OrderIndex = 42,
    Choices =
    {
        new Choice { Text = "Karlofça Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},



                new Question
{
    Note = notZitvatorokAntlasmasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı padişahı ile Avusturya hükümdarının siyasi bakımdan eşit kabul edildiği ve Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğünün sona erdiği antlaşma aşağıdakilerden hangisidir?",
    Explanation = "Zitvatorok Antlaşması ile Osmanlı padişahı ve Avusturya hükümdarı siyasi bakımdan eşit kabul edilmiş, Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğü sona ermiştir.",
    OrderIndex = 41,
    Choices =
    {
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Pasarofça Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Yaş Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},
            new Question
            {
                Note = notDuraklamaGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde bozulma ve yozlaşmanın belirginleştiği dönem aşağıdakilerden hangisidir?",
                Explanation = "Ders notunda Osmanlı Devleti'nde bozulma ve yozlaşmanın 17. yüzyıldan itibaren başladığı belirtilmektedir.",
                OrderIndex = 1,
                Choices =
                {
                    new Choice { Text = "17. yüzyıl", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "13. yüzyıl", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "14. yüzyıl", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "15. yüzyıl", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "16. yüzyılın başları", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notDuraklamaGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde duraklama kavramı esas olarak aşağıdakilerden hangisini ifade eder?",
                Explanation = "Duraklama, yalnızca sınırların genişlememesi değil, devletin iç mekanizmalarının bozulması anlamına gelir.",
                OrderIndex = 2,
                Choices =
                {
                    new Choice { Text = "Devletin iç mekanizmalarının bozulmasını", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tüm toprakların kaybedilmesini", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Padişahlığın kaldırılmasını", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Başkent değişikliğini", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Halifeliğin kaybedilmesini", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKafesVeEkberErsed,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Sancağa çıkma sistemini kaldıran Osmanlı padişahı aşağıdakilerden hangisidir?",
                Explanation = "III. Mehmet sancağa çıkma sistemini kaldırmış ve şehzadelerin kafeste yetişmesine zemin hazırlamıştır.",
                OrderIndex = 3,
                Choices =
                {
                    new Choice { Text = "III. Mehmet", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "II. Osman", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "IV. Murat", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "IV. Mehmet", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKafesVeEkberErsed,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Sancaktan yetişerek tahta çıkan son Osmanlı padişahı aşağıdakilerden hangisidir?",
                Explanation = "III. Mehmet, sancağa çıkma uygulamasını kaldırmadan önce sancaktan yetişerek tahta çıkan son padişahtır.",
                OrderIndex = 4,
                Choices =
                {
                    new Choice { Text = "III. Mehmet", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "I. Mustafa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "II. Osman", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "IV. Mehmet", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKafesVeEkberErsed,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Şehzadelerin kafeste yetişmesine dayanan uygulamanın diğer adı aşağıdakilerden hangisidir?",
                Explanation = "Kafes uygulamasının saray içindeki adı Şimşirliktir.",
                OrderIndex = 5,
                Choices =
                {
                    new Choice { Text = "Şimşirlik", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Enderun", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Harem", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Birun", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Divan-ı Hümayun", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKafesVeEkberErsed,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Kafesten yetişerek tahta çıkan ilk Osmanlı padişahı aşağıdakilerden hangisidir?",
                Explanation = "Kafesten yetişerek tahta çıkan ilk Osmanlı padişahı I. Ahmet'tir.",
                OrderIndex = 6,
                Choices =
                {
                    new Choice { Text = "I. Ahmet", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "III. Mehmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "II. Osman", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "IV. Murat", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "I. İbrahim", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKafesVeEkberErsed,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Veraset sisteminde son değişikliği yaparak Ekber ve Erşed sistemini getiren padişah aşağıdakilerden hangisidir?",
                Explanation = "Ekber ve Erşed sistemini I. Ahmet getirmiştir.",
                OrderIndex = 7,
                Choices =
                {
                    new Choice { Text = "I. Ahmet", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "III. Mehmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "II. Osman", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "IV. Murat", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "IV. Mehmet", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notDuraklamaNedenleri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Aşağıdakilerden hangisi Osmanlı Devleti'nin duraklama dönemine girmesinin nedenlerinden biridir?",
                Explanation = "Tımar sisteminin bozulması ve iltizam sisteminin yaygınlaşması duraklamanın nedenleri arasındadır.",
                OrderIndex = 8,
                Choices =
                {
                    new Choice { Text = "Tımar sisteminin bozulması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Anadolu Türk siyasi birliğinin kesin olarak sağlanması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "İstanbul'un fethedilmesi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Halifeliğin Osmanlı Devleti'ne geçmesi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Akdeniz'in Türk gölü haline gelmesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notTimarBozulmasi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Tımar sisteminin temel amaçlarından biri aşağıdakilerden hangisidir?",
                Explanation = "Ders notunda tımar sisteminin en önemli amaçlarından biri toprağın boş kalmasını önlemek olarak açıklanmıştır.",
                OrderIndex = 9,
                Choices =
                {
                    new Choice { Text = "Toprağın boş kalmasını önlemek", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Yalnızca saray masraflarını karşılamak", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Deniz ticaretini geliştirmek", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kapıkulu askerlerinin sayısını artırmak", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kapitülasyonları yaygınlaştırmak", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notVenedikGirit,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1645-1669 yılları arasında 24 yıl süren kuşatma sonunda Osmanlı Devleti'nin fethettiği ada aşağıdakilerden hangisidir?",
                Explanation = "24 yıl süren kuşatmanın sonunda fethedilen ada Girit'tir.",
                OrderIndex = 10,
                Choices =
                {
                    new Choice { Text = "Girit", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kıbrıs", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Rodos", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Malta", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Sakız", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notRusyaBahcesaray,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "İlk Osmanlı-Rus antlaşması aşağıdakilerden hangisidir?",
                Explanation = "1681 Bahçesaray, diğer adıyla Çehrin Antlaşması, ilk Osmanlı-Rus antlaşmasıdır.",
                OrderIndex = 11,
                Choices =
                {
                    new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Vasvar Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIranKasriSirin,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Günümüz Türkiye-İran sınırını büyük ölçüde belirleyen antlaşma aşağıdakilerden hangisidir?",
                Explanation = "1639 Kasr-ı Şirin Antlaşması günümüz Türkiye-İran sınırını büyük ölçüde belirlemiştir.",
                OrderIndex = 12,
                Choices =
                {
                    new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Nasuh Paşa Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Serav Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ferhat Paşa Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIranKasriSirin,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Kasr-ı Şirin Antlaşması sonrasında Bağdat aşağıdaki devletlerden hangisinde kalmıştır?",
                Explanation = "Kasr-ı Şirin Antlaşması ile Bağdat Osmanlı Devleti'nde kalmıştır.",
                OrderIndex = 13,
                Choices =
                {
                    new Choice { Text = "Osmanlı Devleti", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Safevi Devleti", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Rusya", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Avusturya", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Lehistan", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notLehistanHotinBucas,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Yeniçerilerin disiplinsizliği ve isteksizliğinin belirginleşerek II. Osman'ın Yeniçeri Ocağı'nı kaldırmayı düşünmesine neden olan sefer aşağıdakilerden hangisidir?",
                Explanation = "Hotin Seferi'ndeki yeniçeri disiplinsizliği II. Osman'ın ocağı kaldırmayı düşünmesine neden olmuştur.",
                OrderIndex = 14,
                Choices =
                {
                    new Choice { Text = "Hotin Seferi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "II. Viyana Kuşatması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Girit Kuşatması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Irak Seferi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Haçova Meydan Muharebesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notLehistanHotinBucas,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin batıda en geniş sınırlara ulaşmasını sağlayan antlaşma aşağıdakilerden hangisidir?",
                Explanation = "1672 Bucaş Antlaşması ile Osmanlı Devleti batıda en geniş sınırlara ulaşmıştır.",
                OrderIndex = 15,
                Choices =
                {
                    new Choice { Text = "Bucaş Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Vasvar Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAvusturya,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Kepçe-Kazan Savaşı olarak da bilinen meydan muharebesi aşağıdakilerden hangisidir?",
                Explanation = "Haçova Savaşı'nda geri hizmette bulunanların kepçe ve kazanlarla savaşa katılması nedeniyle bu ad kullanılmıştır.",
                OrderIndex = 16,
                Choices =
                {
                    new Choice { Text = "Haçova Meydan Muharebesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Zenta Savaşı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Hotin Savaşı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Varna Savaşı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "II. Kosova Savaşı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAvusturya,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğünün sona ermesine yol açan antlaşma aşağıdakilerden hangisidir?",
                Explanation = "1606 Zitvatorok Antlaşması ile Osmanlı padişahı ve Avusturya hükümdarı siyasi bakımdan eşit kabul edilmiştir.",
                OrderIndex = 17,
                Choices =
                {
                    new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "İstanbul Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciViyanaVeKarlovca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Viyana Kuşatması'nın başarısız olması üzerine idam edilen Osmanlı sadrazamı aşağıdakilerden hangisidir?",
                Explanation = "II. Viyana Kuşatması'nın başarısızlığından sonra Merzifonlu Kara Mustafa Paşa idam edilmiştir.",
                OrderIndex = 18,
                Choices =
                {
                    new Choice { Text = "Merzifonlu Kara Mustafa Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Köprülü Mehmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Köprülü Fazıl Ahmet Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kuyucu Murat Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Tarhuncu Ahmet Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciViyanaVeKarlovca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin batıda ilk kez büyük çaplı toprak kaybettiği antlaşma aşağıdakilerden hangisidir?",
                Explanation = "1699 Karlofça Antlaşması ile Osmanlı Devleti batıda ilk kez büyük çaplı toprak kaybetmiştir.",
                OrderIndex = 19,
                Choices =
                {
                    new Choice { Text = "Karlofça Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Vasvar Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIstanbulIsyanlari,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1656 yılında yaşanan ve yaklaşık 30 devlet adamının çınar ağaçlarına asılmasıyla bilinen olay aşağıdakilerden hangisidir?",
                Explanation = "1656'da yaşanan Çınar Vakası'nın diğer adı Vak'a-i Vakvakiye'dir.",
                OrderIndex = 20,
                Choices =
                {
                    new Choice { Text = "Vak'a-i Vakvakiye", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Vak'a-i Hayriye", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Buçuktepe İsyanı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Şahkulu İsyanı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Patrona Halil İsyanı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notCelaliIsyanlari,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Anadolu'da çıkan Celali İsyanlarına adını veren ilk isyancı aşağıdakilerden hangisidir?",
                Explanation = "Celali İsyanları adını 1519'da isyan eden Şeyh Celal'den almıştır.",
                OrderIndex = 21,
                Choices =
                {
                    new Choice { Text = "Şeyh Celal", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Patrona Halil", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Şahkulu", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Baba Zünnun", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Şeyh Bedrettin", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notCelaliSonuclari,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1603-1610 yılları arasında Celali İsyanları nedeniyle Anadolu'dan büyük şehirlere yaşanan yoğun göç hareketine ne ad verilir?",
                Explanation = "Celali İsyanları nedeniyle 1603-1610 arasında yaşanan büyük göç hareketi Büyük Kaçgun olarak adlandırılır.",
                OrderIndex = 22,
                Choices =
                {
                    new Choice { Text = "Büyük Kaçgun", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Vaka-i Hayriye", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Nizam-ı Cedid", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Lale Devri", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notEyaletVeSuhte,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde medrese öğrencilerine verilen ad aşağıdakilerden hangisidir?",
                Explanation = "Medrese öğrencilerine Suhte veya Softa adı verilmiştir.",
                OrderIndex = 23,
                Choices =
                {
                    new Choice { Text = "Suhte veya Softa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tımarlı sipahi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Cebeci", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Mültezim", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Voyvoda", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIslahatGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "XVII. yüzyıl Osmanlı ıslahatlarının temel özelliklerinden biri aşağıdakilerden hangisidir?",
                Explanation = "XVII. yüzyıl ıslahatlarında Avrupa örnek alınmamış, eski düzene dönülmek istenmiştir.",
                OrderIndex = 24,
                Choices =
                {
                    new Choice { Text = "Avrupa örnek alınmamıştır", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Halkın geniş desteği alınmıştır", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Sorunların kök nedenleri tamamen çözülmüştür", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Islahatlar kalıcı anayasal düzen kurmuştur", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Padişaha bağlı olmayan sürekli kurumlar kurulmuştur", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIslahatGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "XVII. yüzyıl Osmanlı ıslahatlarında geri dönülmek istenen eski düzen anlayışı aşağıdakilerden hangisidir?",
                Explanation = "Bu dönemde Kanun-ı Kadim anlayışıyla özellikle Fatih ve Kanuni dönemlerindeki düzene dönülmek istenmiştir.",
                OrderIndex = 25,
                Choices =
                {
                    new Choice { Text = "Kanun-ı Kadim", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Nizam-ı Cedid", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Tanzimat", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Meşrutiyet", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notTarhuncu,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde ilk denk bütçeyi hazırlayan devlet adamı aşağıdakilerden hangisidir?",
                Explanation = "Tarhuncu Ahmet Paşa gelir ve giderleri eşitleyerek Osmanlı Devleti'nin ilk denk bütçesini hazırlamıştır.",
                OrderIndex = 26,
                Choices =
                {
                    new Choice { Text = "Tarhuncu Ahmet Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kuyucu Murat Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Köprülü Mehmet Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Merzifonlu Kara Mustafa Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Sokullu Mehmet Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notGençOsman,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Yeniçeri Ocağı'nı kaldırmayı ilk kez ciddi biçimde düşünen Osmanlı padişahı aşağıdakilerden hangisidir?",
                Explanation = "Hotin Seferi'ndeki yeniçeri disiplinsizliğinden sonra Genç Osman ocağı kaldırmayı düşünmüştür.",
                OrderIndex = 27,
                Choices =
                {
                    new Choice { Text = "II. Osman", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "IV. Murat", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "IV. Mehmet", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "I. İbrahim", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKuyucuMurat,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Celali İsyanlarını şiddet ve baskı yoluyla bastıran I. Ahmet dönemi sadrazamı aşağıdakilerden hangisidir?",
                Explanation = "Kuyucu Murat Paşa, I. Ahmet'in sadrazamı olarak Celali İsyanlarını bastırmıştır.",
                OrderIndex = 28,
                Choices =
                {
                    new Choice { Text = "Kuyucu Murat Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tarhuncu Ahmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Köprülü Fazıl Ahmet Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Merzifonlu Kara Mustafa Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Tiryaki Hasan Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notDorduncuMurat,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı tarihinde ilk defa bir Şeyhülislamı idam ettiren padişah aşağıdakilerden hangisidir?",
                Explanation = "IV. Murat döneminde Osmanlı tarihinde ilk kez bir Şeyhülislam idam ettirilmiştir.",
                OrderIndex = 29,
                Choices =
                {
                    new Choice { Text = "IV. Murat", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "II. Osman", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "IV. Mehmet", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "III. Mehmet", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notDorduncuMurat,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "IV. Murat'ın gece sokağa çıkmayı, içki ve tütün kullanımını yasaklamasının temel amacı aşağıdakilerden hangisidir?",
                Explanation = "Ders notunda bu yasakların temel amacı büyük İstanbul yangınlarını önlemek olarak açıklanmıştır.",
                OrderIndex = 30,
                Choices =
                {
                    new Choice { Text = "Büyük İstanbul yangınlarını önlemek", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Avrupa ile ticareti geliştirmek", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Tımar sistemini yeniden kurmak", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kapitülasyonları kaldırmak", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Sancağa çıkma sistemini yeniden başlatmak", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKociBey,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin sorunlarını ve çözüm önerilerini içeren raporlara verilen ad aşağıdakilerden hangisidir?",
                Explanation = "Koçi Bey'in hazırladığı ve çözüm önerileri de içeren raporlar risale veya layiha olarak adlandırılır.",
                OrderIndex = 31,
                Choices =
                {
                    new Choice { Text = "Layiha", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ferman", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Berat", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Adaletname", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Mühimme Defteri", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKociBey,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Koçi Bey risalelerini hangi iki Osmanlı padişahına sunmuştur?",
                Explanation = "Koçi Bey, IV. Murat'a ve I. İbrahim'e ayrı ayrı risaleler sunmuştur.",
                OrderIndex = 32,
                Choices =
                {
                    new Choice { Text = "IV. Murat ve I. İbrahim", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Ahmet ve II. Osman", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "III. Mehmet ve I. Ahmet", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "IV. Mehmet ve II. Süleyman", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "II. Osman ve IV. Mehmet", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKoprululer,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Saraya şartlar öne sürerek sadrazam olan Köprülü devlet adamı aşağıdakilerden hangisidir?",
                Explanation = "Köprülü Mehmet Paşa, IV. Mehmet döneminde bazı şartlar öne sürerek sadrazam olmuştur.",
                OrderIndex = 33,
                Choices =
                {
                    new Choice { Text = "Köprülü Mehmet Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Köprülü Fazıl Ahmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Merzifonlu Kara Mustafa Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Tarhuncu Ahmet Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kuyucu Murat Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notKoprululer,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Girit Kuşatması'nı başarıyla sonuçlandıran Köprülü devlet adamı aşağıdakilerden hangisidir?",
                Explanation = "Girit Kuşatması'nı başarıyla sonuçlandıran Köprülü Fazıl Ahmet Paşa'dır.",
                OrderIndex = 34,
                Choices =
                {
                    new Choice { Text = "Köprülü Fazıl Ahmet Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Köprülü Mehmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Merzifonlu Kara Mustafa Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kuyucu Murat Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Tarhuncu Ahmet Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciViyanaVeKarlovca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Karlofça Antlaşması'nın devamı niteliğinde Rusya ile imzalanan antlaşma aşağıdakilerden hangisidir?",
                Explanation = "Karlofça'nın ardından Rusya ile 1700 İstanbul Antlaşması imzalanmıştır.",
                OrderIndex = 35,
                Choices =
                {
                    new Choice { Text = "İstanbul Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciViyanaVeKarlovca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1700 İstanbul Antlaşması ile Rusya'ya bırakılan kale aşağıdakilerden hangisidir?",
                Explanation = "1700 İstanbul Antlaşması ile Azak Kalesi Rusya'ya bırakılmıştır.",
                OrderIndex = 36,
                Choices =
                {
                    new Choice { Text = "Azak Kalesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Hotin Kalesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Kanije Kalesi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Uyvar Kalesi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kandiye Kalesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMedreseVeBesikUlemasi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Alimin oğlunun alim sayılması anlayışına dayanan uygulama aşağıdakilerden hangisidir?",
                Explanation = "Beşik ulemalığı, alimin oğlunun alim sayılması anlayışına dayanır.",
                OrderIndex = 37,
                Choices =
                {
                    new Choice { Text = "Beşik ulemalığı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Devşirme sistemi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Tımar sistemi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "İltizam sistemi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kafes usulü", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notEyaletVeSuhte,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Genç Osman'ın öldürülmesi üzerine isyan eden ve Erzurum Valiliği verilerek isyanı bastırılan kişi aşağıdakilerden hangisidir?",
                Explanation = "Abaza Mehmet Paşa'nın isyanı Erzurum Valiliği verilerek bastırılmıştır.",
                OrderIndex = 38,
                Choices =
                {
                    new Choice { Text = "Abaza Mehmet Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Karayazıcı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Deli Hasan", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Şeyh Celal", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Patrona Halil", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notYeniCeriBozulmasi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Yeniçeri Ocağı'nın kuruluş anlayışını ifade eden söz aşağıdakilerden hangisidir?",
                Explanation = "Kuruluş anlayışında asker devlet için vardır ve bu anlayış ocak devlet içindir sözüyle ifade edilir.",
                OrderIndex = 39,
                Choices =
                {
                    new Choice { Text = "Ocak devlet içindir", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Devlet ocak içindir", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ocak saray içindir", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Devlet yalnızca asker içindir", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ocak ticaret içindir", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notDuraklamaNedenleri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Coğrafi Keşifler sonucunda Osmanlı Devleti'nin ekonomik gelirlerini olumsuz etkileyen gelişme aşağıdakilerden hangisidir?",
                Explanation = "Coğrafi Keşifler sonucunda İpek ve Baharat yolları önem kaybetmiştir.",
                OrderIndex = 40,
                Choices =
                {
                    new Choice { Text = "İpek ve Baharat yollarının önem kaybetmesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Akdeniz ticaretinin tamamen Osmanlı denetimine girmesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Yeni ticaret yollarının Osmanlı topraklarında kurulması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ganimet gelirlerinin sürekli artması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kapitülasyonların tamamen kaldırılması", IsCorrect = false, OrderIndex = 5 }
                }
            }
        }
        };
    }

    private static Topic BuildMimariEserler()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },

        var notYagibasanMedresesi = new Note
        {
            Title = "Anadolu Medreseleri — Yağıbasan Medresesi",
            Body = """
    • **Yağıbasan Medresesi**, Tokat'ın **Niksar** ilçesinde bulunur.
    • **Anadolu'da açılan ilk medresedir.**
    • **Danişmentliler** tarafından yaptırılmıştır.
    """
        };
        var notKarahanliMimariEserleri = new Note
        {
            Title = "Karahanlılar — Mimari Eserler",
            Body = """
    • **Arap Ata Türbesi**, Karahanlılar dönemine ait bir eserdir.
    • **Değaron Camii**, Karahanlılar dönemine ait bir eserdir.
    • **Ayşe Bibi Türbesi**, Karahanlılar dönemine ait bir eserdir.
    • **Talhatan Baba Camii**, Karahanlılar dönemine ait bir eserdir.
    """
        };
        var notAnadoluTasavvufVeEdebiyat = new Note
        {
            Title = "Devletler — Anadolu'da Tasavvuf, Edebiyat ve Bilim İnsanları",
            Body = """
    • **Hazreti Mevlana:** Divan-ı Kebir ve Mesnevi adlı eserleri kaleme almıştır.
    • **Hacı Bektaş-ı Veli:** Makalat adlı eserin sahibidir.
    • **Yunus Emre:** Risaletü'n-Nushiyye adlı eseri yazmıştır. Nushiyye, nasihat anlamına gelir.
    • **Aşık Paşa:** Garipname adlı eseri yazmıştır. Mezarı Kırşehir'dedir.
    • **Muhyiddin Arabi:** Vahdet-i Vücut felsefesinin öncüsüdür.
    • **Hacı Paşa:** Anadolu'nun İbn Sina'sı olarak anılan tıp âlimidir.
    • **Ravendi:** Selçuklu tarihini yazan bir tarihçidir.
    • **Feridüddin Attar:** Mantıku't-Tayr, yani Kuşların Dili adlı eserin yazarıdır. Bu eseri Gülşehri Türkçeye çevirmiştir.
    • **Hoca Dehhani:** Anadolu Selçuklu Devleti'nde divan edebiyatının kurucusu sayılır.
    """
        };
        var notAnadoluTurkBeylikleriMimariEserleri = new Note
        {
            Title = "Anadolu Türk Beylikleri — Mimari Eserler",
            Body = """
    • **Danişmentliler:** **Kayseri Ulu Camii**, **Yağıbasan Medresesi** Anadolu'da yapılan **ilk medresedir** ve Tokat'ın Niksar ilçesinde bulunur.
    • **Saltuklular:** **Kale Camii**, **Tepsi Minare**, **Üç Kümbetler**, **Mama Hatun Türbesi**, **Micingert Kalesi**.
    • **Mengücekliler:** **Divriği Ulu Camii**, **1985**'ten beri **UNESCO** koruması altındadır.
    • **Artuklular:** **Malabadi Köprüsü**, **Hatuniye Medresesi**, **Necmeddin Külliyesi**, **Koçhisar Ulu Camii**, **Semaın Medresesi**.
    """
        };
        var notTurkDestanlari = new Note
        {
            Title = "İslamiyet Öncesi Türk Devletleri — Destanlar",
            Body = """
    • **Göç Destanı:** Uygurlara aittir. Uygurların kutsal bir kayayı Çinlilere vermesiyle başlayan olaylar anlatılır. Kayanın verilmesinden sonra ülkede bereketin ve huzurun bozulması üzerine Uygurlar yurtlarını terk ederek göç eder.
    • **Türeyiş Destanı:** Uygurlara aittir. Uygur hükümdarının kızlarının kutsal bir varlıkla evlendirilmesi ve bu birliktelikten Uygur hükümdar soyunun ortaya çıkması anlatılır.
    • **Ergenekon Destanı:** Göktürklere aittir. Düşmanları tarafından yok edilen Türklerden bir kişinin kurtulması ve Türklerin Ergenekon adı verilen vadide çoğalması anlatılır. Türkler vadiden çıkmak için demir dağı eriterek dışarı çıkar ve yeniden güçlü bir devlet kurar.
    • **Bozkurt Destanı:** Göktürklere aittir. Düşman saldırısından sağ kalan bir Türk çocuğunun dişi bir bozkurt tarafından kurtarılması ve büyütülmesi anlatılır. Bozkurt, Türklerin yeniden çoğalmasında ve soylarının devam etmesinde önemli rol oynar.
    • **Manas Destanı:** Kırgızlara aittir. Kırgız kahramanı **Manas'ın** hayatı, savaşları ve Kırgızları bir araya getirme mücadelesi anlatılır.
    """
        };





        return new Topic
        {
            Name = "Mimari Eserler",
            Description = "...",
            Notes = { notYagibasanMedresesi, notKarahanliMimariEserleri, notAnadoluTurkBeylikleriMimariEserleri, notTurkDestanlari , notAnadoluTasavvufVeEdebiyat },
            Questions = {
            // --- SORU 1 ---










// --- SORU 7 ---
new Question
{
    Note = notTurkDestanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Uygurların kutsal bir kayayı Çinlilere vermesiyle başlayan, kayanın verilmesinden sonra ülkede bereketin ve huzurun bozulması üzerine Uygurların yurtlarını terk ederek göç etmesini anlatan destan aşağıdakilerden hangisidir?",
    Explanation = "Uygurların kutsal kayanın verilmesinden sonra yurtlarını terk ederek göç etmesini anlatan destan Göç Destanı'dır.",
    OrderIndex = 7,
    Choices =
    {
        new Choice { Text = "Göç Destanı", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Türeyiş Destanı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ergenekon Destanı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bozkurt Destanı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Manas Destanı", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 8 ---
new Question
{
    Note = notTurkDestanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Uygur hükümdarının kızlarının kutsal bir varlıkla evlendirilmesi ve bu birliktelikten Uygur hükümdar soyunun ortaya çıkmasını anlatan destan aşağıdakilerden hangisidir?",
    Explanation = "Uygur hükümdar soyunun ortaya çıkışını anlatan destan Türeyiş Destanı'dır.",
    OrderIndex = 8,
    Choices =
    {
        new Choice { Text = "Türeyiş Destanı", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Göç Destanı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ergenekon Destanı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bozkurt Destanı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Manas Destanı", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 9 ---
new Question
{
    Note = notTurkDestanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Düşmanları tarafından yok edilen Türklerden bir kişinin kurtulması, Türklerin Ergenekon vadisinde çoğalması ve demir dağı eriterek vadiden çıkıp yeniden güçlü bir devlet kurmasını anlatan destan aşağıdakilerden hangisidir?",
    Explanation = "Türklerin Ergenekon'dan çıkışını ve yeniden güçlenmesini anlatan destan Ergenekon Destanı'dır.",
    OrderIndex = 9,
    Choices =
    {
        new Choice { Text = "Ergenekon Destanı", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Göç Destanı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Türeyiş Destanı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bozkurt Destanı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Manas Destanı", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 10 ---
new Question
{
    Note = notTurkDestanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Düşman saldırısından sağ kalan bir Türk çocuğunun dişi bir bozkurt tarafından kurtarılması ve büyütülmesi, bozkurdun Türk soyunun yeniden türemesinde rol oynamasını anlatan destan aşağıdakilerden hangisidir?",
    Explanation = "Türk soyunun bozkurt sayesinde yeniden türemesini anlatan destan Bozkurt Destanı'dır.",
    OrderIndex = 10,
    Choices =
    {
        new Choice { Text = "Bozkurt Destanı", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Ergenekon Destanı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Göç Destanı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Türeyiş Destanı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Manas Destanı", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 11 ---
new Question
{
    Note = notTurkDestanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kırgız kahramanı Manas'ın hayatı, savaşları ve Kırgızları bir araya getirme mücadelesini anlatan destan aşağıdakilerden hangisidir?",
    Explanation = "Manas'ın Kırgızları birleştirmesi ve düşmanlara karşı mücadelesini anlatan destan Manas Destanı'dır.",
    OrderIndex = 11,
    Choices =
    {
        new Choice { Text = "Manas Destanı", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Bozkurt Destanı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ergenekon Destanı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Göç Destanı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Türeyiş Destanı", IsCorrect = false, OrderIndex = 5 }
    }
},
// --- SORU 11 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Divan-ı Kebir ve Mesnevi adlı eserleri kaleme alan kişi aşağıdakilerden hangisidir?",
    Explanation = "Divan-ı Kebir ve Mesnevi, Hazreti Mevlana'nın eserleridir.",
    OrderIndex = 20,
    Choices =
    {
        new Choice { Text = "Hazreti Mevlana", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Hacı Bektaş-ı Veli", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Aşık Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Hoca Dehhani", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 12 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Makalat adlı eserin sahibi olan kişi aşağıdakilerden hangisidir?",
    Explanation = "Makalat adlı eserin sahibi Hacı Bektaş-ı Veli'dir.",
    OrderIndex = 12,
    Choices =
    {
        new Choice { Text = "Hacı Bektaş-ı Veli", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Hazreti Mevlana", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Aşık Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Muhyiddin Arabi", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 13 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Risaletü'n-Nushiyye adlı eseri yazan ve eserindeki \"Nushiyye\" kelimesi nasihat anlamına gelen kişi aşağıdakilerden hangisidir?",
    Explanation = "Risaletü'n-Nushiyye adlı eser Yunus Emre'ye aittir.",
    OrderIndex = 13,
    Choices =
    {
        new Choice { Text = "Yunus Emre", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Hazreti Mevlana", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Hacı Bektaş-ı Veli", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Aşık Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ravendi", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 14 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Garipname adlı eseri yazan ve mezarı Kırşehir'de bulunan kişi aşağıdakilerden hangisidir?",
    Explanation = "Garipname adlı eseri Aşık Paşa yazmıştır ve mezarı Kırşehir'dedir.",
    OrderIndex = 14,
    Choices =
    {
        new Choice { Text = "Aşık Paşa", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Hazreti Mevlana", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Hacı Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Hoca Dehhani", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 15 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Vahdet-i Vücut felsefesinin öncüsü olarak kabul edilen kişi aşağıdakilerden hangisidir?",
    Explanation = "Vahdet-i Vücut felsefesinin öncüsü Muhyiddin Arabi'dir.",
    OrderIndex = 15,
    Choices =
    {
        new Choice { Text = "Muhyiddin Arabi", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Hazreti Mevlana", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Hacı Bektaş-ı Veli", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Hacı Paşa", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 16 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "\"Anadolu'nun İbn Sina'sı\" olarak anılan tıp âlimi aşağıdakilerden hangisidir?",
    Explanation = "Hacı Paşa, Anadolu'nun İbn Sina'sı olarak anılan bir tıp âlimidir.",
    OrderIndex = 16,
    Choices =
    {
        new Choice { Text = "Hacı Paşa", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Ravendi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Muhyiddin Arabi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Hoca Dehhani", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Feridüddin Attar", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 17 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Selçuklu tarihini yazan tarihçi aşağıdakilerden hangisidir?",
    Explanation = "Ravendi, Selçuklu tarihini yazan bir tarihçidir.",
    OrderIndex = 17,
    Choices =
    {
        new Choice { Text = "Ravendi", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Hacı Paşa", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Aşık Paşa", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Hoca Dehhani", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 18 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Mantıku't-Tayr, yani \"Kuşların Dili\" adlı eserin yazarı olan ve eseri Gülşehri tarafından Türkçeye çevrilen kişi aşağıdakilerden hangisidir?",
    Explanation = "Mantıku't-Tayr'ın yazarı Feridüddin Attar'dır. Eser Gülşehri tarafından Türkçeye çevrilmiştir.",
    OrderIndex = 18,
    Choices =
    {
        new Choice { Text = "Feridüddin Attar", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Gülşehri", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Hazreti Mevlana", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Aşık Paşa", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 19 ---
new Question
{
    Note = notAnadoluTasavvufVeEdebiyat,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Anadolu Selçuklu Devleti'nde divan edebiyatının kurucusu sayılan kişi aşağıdakilerden hangisidir?",
    Explanation = "Hoca Dehhani, Anadolu Selçuklu Devleti'nde divan edebiyatının kurucusu sayılır.",
    OrderIndex = 19,
    Choices =
    {
        new Choice { Text = "Hoca Dehhani", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Yunus Emre", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Hazreti Mevlana", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Aşık Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ravendi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notYagibasanMedresesi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Tokat'ın Niksar ilçesinde bulunan, Anadolu'da açılan ilk medrese olan ve Danişmentliler tarafından yaptırılan eser aşağıdakilerden hangisidir?",
    Explanation = "Yağıbasan Medresesi, Tokat'ın Niksar ilçesinde bulunan ve Anadolu'da açılan ilk medresedir.",
    OrderIndex = 1,
    Choices =
    {
        new Choice { Text = "Yağıbasan Medresesi", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Koca Hasan Medresesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Gök Medrese", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Cacabey Medresesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Çifte Minareli Medrese", IsCorrect = false, OrderIndex = 5 },
        new Choice { Text = "Karatay Medresesi", IsCorrect = false, OrderIndex = 6 }
    }
},
new Question
{
    Note = notKarahanliMimariEserleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Arap Ata Türbesi, Değaron Camii, Ayşe Bibi Türbesi ve Talhatan Baba Camii'ni yaptıran devlet aşağıdakilerden hangisidir?",
    Explanation = "Arap Ata Türbesi, Değaron Camii, Ayşe Bibi Türbesi ve Talhatan Baba Camii Karahanlılar dönemine ait eserlerdir.",
    OrderIndex = 2,
    Choices =
    {
        new Choice { Text = "Karahanlılar", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Gazneliler", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Büyük Selçuklular", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Harzemşahlar", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Tolunoğulları", IsCorrect = false, OrderIndex = 5 }
    }
},
// --- SORU 3 ---
new Question
{
    Note = notAnadoluTurkBeylikleriMimariEserleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Aşağıdaki eser çiftlerinden hangileri Danişmentlilere aittir?",
    Explanation = "Kayseri Ulu Camii ve Yağıbasan Medresesi Danişmentlilere ait eserlerdir.",
    OrderIndex = 3,
    Choices =
    {
        new Choice { Text = "Kayseri Ulu Camii — Yağıbasan Medresesi", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Kale Camii — Tepsi Minare", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Divriği Ulu Camii — Malabadi Köprüsü", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Hatuniye Medresesi — Necmeddin Külliyesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Mama Hatun Türbesi — Micingert Kalesi", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 4 ---
new Question
{
    Note = notAnadoluTurkBeylikleriMimariEserleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Aşağıdaki eserlerden hangisi Saltuklulara aittir?",
    Explanation = "Kale Camii, Tepsi Minare, Üç Kümbetler, Mama Hatun Türbesi ve Micingert Kalesi Saltuklulara ait eserlerdir.",
    OrderIndex = 4,
    Choices =
    {
        new Choice { Text = "Kale Camii", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Kayseri Ulu Camii", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yağıbasan Medresesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Divriği Ulu Camii", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Malabadi Köprüsü", IsCorrect = false, OrderIndex = 5 },
        new Choice { Text = "Hatuniye Medresesi", IsCorrect = false, OrderIndex = 6 },
        new Choice { Text = "Necmeddin Külliyesi", IsCorrect = false, OrderIndex = 7 },
        new Choice { Text = "Koçhisar Ulu Camii", IsCorrect = false, OrderIndex = 8 },
        new Choice { Text = "Semaın Medresesi", IsCorrect = false, OrderIndex = 9 }
    }
},

// --- SORU 5 ---
new Question
{
    Note = notAnadoluTurkBeylikleriMimariEserleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Aşağıdaki eserlerden hangisi Mengüceklilere aittir?",
    Explanation = "Divriği Ulu Camii Mengüceklilere ait olup 1985'ten beri UNESCO koruması altındadır.",
    OrderIndex = 5,
    Choices =
    {
        new Choice { Text = "Divriği Ulu Camii", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Kayseri Ulu Camii", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yağıbasan Medresesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kale Camii", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Malabadi Köprüsü", IsCorrect = false, OrderIndex = 5 },
        new Choice { Text = "Mama Hatun Türbesi", IsCorrect = false, OrderIndex = 6 }
    }
},

// --- SORU 6 ---
new Question
{
    Note = notAnadoluTurkBeylikleriMimariEserleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Aşağıdaki eserlerden hangisi Artuklulara aittir?",
    Explanation = "Malabadi Köprüsü, Hatuniye Medresesi, Necmeddin Külliyesi, Koçhisar Ulu Camii ve Semaın Medresesi Artuklulara ait eserlerdir.",
    OrderIndex = 6,
    Choices =
    {
        new Choice { Text = "Malabadi Köprüsü", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Hatuniye Medresesi", IsCorrect = true, OrderIndex = 2 },
        new Choice { Text = "Necmeddin Külliyesi", IsCorrect = true, OrderIndex = 3 },
        new Choice { Text = "Koçhisar Ulu Camii", IsCorrect = true, OrderIndex = 4 },
        new Choice { Text = "Semaın Medresesi", IsCorrect = true, OrderIndex = 5 },
        new Choice { Text = "Kayseri Ulu Camii", IsCorrect = false, OrderIndex = 6 },
        new Choice { Text = "Yağıbasan Medresesi", IsCorrect = false, OrderIndex = 7 },
        new Choice { Text = "Kale Camii", IsCorrect = false, OrderIndex = 8 },
        new Choice { Text = "Tepsi Minare", IsCorrect = false, OrderIndex = 9 },
        new Choice { Text = "Üç Kümbetler", IsCorrect = false, OrderIndex = 10 },
        new Choice { Text = "Mama Hatun Türbesi", IsCorrect = false, OrderIndex = 11 },
        new Choice { Text = "Micingert Kalesi", IsCorrect = false, OrderIndex = 12 }
    }
},
            },
        };
    }
    private static Topic BuildDevletler()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        var notTurkIslamBeylikleriBolge = new Note
        {
            Title = "Devletler — Türk İslam Beyliklerinin Faaliyet Bölgeleri",
            Body = """
    • **Dilmaçoğulları:** Bitlis'te faaliyet göstermiştir.
    • **Artuklular:** Mardin, Diyarbakır, Elazığ ve Harput'ta faaliyet göstermiştir.
    • **Mengücekliler:** Divriği ve Erzincan'da faaliyet göstermiştir.
    • **Saltuklular:** Erzurum ve çevresinde faaliyet göstermiştir.
    • **Danişmentliler:** Sivas, Tokat ve Malatya civarında faaliyet göstermiştir.
    • **Çaka Beyliği:** İzmir'de faaliyet göstermiştir.
    • **Çubukoğulları:** Harput'ta faaliyet göstermiştir.
    • **Tanrıvermişoğulları:** Efes'te faaliyet göstermiştir.
    """
        };

        return new Topic
        {
            Name = "Devletler",
            Description = "...",
            Notes = { notTurkIslamBeylikleriBolge },
            Questions = {
            
            // --- SORU 1 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Bitlis bölgesinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Dilmaçoğulları Bitlis bölgesinde faaliyet göstermiştir.",
    OrderIndex = 1,
    Choices =
    {
        new Choice { Text = "Dilmaçoğulları", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Saltuklular", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Danişmentliler", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Mengücekliler", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Çaka Beyliği", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 2 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Mardin, Diyarbakır, Elazığ ve Harput bölgelerinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Artuklular Mardin, Diyarbakır, Elazığ ve Harput bölgelerinde faaliyet göstermiştir.",
    OrderIndex = 2,
    Choices =
    {
        new Choice { Text = "Artuklular", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Dilmaçoğulları", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Mengücekliler", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Danişmentliler", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Tanrıvermişoğulları", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 3 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Divriği ve Erzincan bölgelerinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Mengücekliler Divriği ve Erzincan bölgelerinde faaliyet göstermiştir.",
    OrderIndex = 3,
    Choices =
    {
        new Choice { Text = "Mengücekliler", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Saltuklular", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Artuklular", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Çubukoğulları", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Çaka Beyliği", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 4 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Erzurum ve çevresinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Saltuklular Erzurum ve çevresinde faaliyet göstermiştir.",
    OrderIndex = 4,
    Choices =
    {
        new Choice { Text = "Saltuklular", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Mengücekliler", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Danişmentliler", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Dilmaçoğulları", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Artuklular", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 5 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sivas, Tokat ve Malatya civarında faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Danişmentliler Sivas, Tokat ve Malatya civarında faaliyet göstermiştir.",
    OrderIndex = 5,
    Choices =
    {
        new Choice { Text = "Danişmentliler", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Saltuklular", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Mengücekliler", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Artuklular", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Tanrıvermişoğulları", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 6 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "İzmir bölgesinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Çaka Beyliği İzmir bölgesinde faaliyet göstermiştir.",
    OrderIndex = 6,
    Choices =
    {
        new Choice { Text = "Çaka Beyliği", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Tanrıvermişoğulları", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Çubukoğulları", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Dilmaçoğulları", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Saltuklular", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 7 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Harput bölgesinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Çubukoğulları Harput bölgesinde faaliyet göstermiştir.",
    OrderIndex = 7,
    Choices =
    {
        new Choice { Text = "Çubukoğulları", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Artuklular", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Mengücekliler", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Danişmentliler", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Dilmaçoğulları", IsCorrect = false, OrderIndex = 5 }
    }
},

// --- SORU 8 ---
new Question
{
    Note = notTurkIslamBeylikleriBolge,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Efes bölgesinde faaliyet gösteren Türk İslam beyliği hangisidir?",
    Explanation = "Tanrıvermişoğulları Efes bölgesinde faaliyet göstermiştir.",
    OrderIndex = 8,
    Choices =
    {
        new Choice { Text = "Tanrıvermişoğulları", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Çaka Beyliği", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Artuklular", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Saltuklular", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Mengücekliler", IsCorrect = false, OrderIndex = 5 }
    }
},},
        };
    }
    private static Topic BuildOsmanliDevletiGerilemeDonemi()
    {
        var notGenel = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Genel Çerçeve",
            Body = """
• 18. yüzyılda Avrupa'daki gelişmeler Osmanlı Devleti'ni doğrudan etkilemiştir.
• Avrupa devletlerinin amaca ulaşmak için her türlü tedbire başvurmasına **Makyavelizm** denir.
• Rusya'nın temel politikaları Karadeniz ve Akdeniz'e inmek, Balkanlar ve Kafkasya'ya yayılmak ve Slav birliğini sağlamaktı.
• **Dakya Projesi**, Avusturya ve Rusya'nın Eflak ve Boğdan'da ortak denetim altında bir devlet kurma planıdır.
• **Grek Projesi**, İstanbul merkezli bir Rus devleti kurarak Bizans'ı yeniden canlandırmayı amaçlamıştır.
"""
        };

        var notPrut = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Prut Antlaşması",
            Body = """
• **1711 Prut Antlaşması**, Osmanlı Devleti ile Rusya arasında imzalandı.
• Rusların, İsveç Kralı XII. Şarl'ı takip ederek Osmanlı topraklarına girmesi savaşın nedenidir.
• Azak Kalesi Osmanlı Devleti'ne geri verildi.
• XII. Şarl Osmanlı ülkesinde kalmaya devam etti ve **Demirbaş Şarl** olarak anıldı.
• Prut Antlaşması, Karlofça ve İstanbul Antlaşmaları ile kaybedilen toprakları geri alma umudunu doğurdu.
"""
        };

        var notPasarofca = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Pasarofça Antlaşması",
            Body = """
• Osmanlı Devleti 1715-1718 arasında Avusturya ile mücadele etti.
• Peter Varadin Savaşı'nın ardından **1718 Pasarofça Antlaşması** imzalandı.
• Pasarofça Antlaşması ile Osmanlı Devleti **Batı'nın üstünlüğünü ilk defa kabul etti**.
• Bu antlaşmadan sonra **Lale Devri** başladı.
"""
        };

        var notBelgrad = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Belgrad Savaşı ve Antlaşması",
            Body = """
• **1736-1739 Belgrad Savaşı**, Osmanlı Devleti ile Avusturya ve Rusya arasında yapıldı.
• Osmanlı Devleti savaşı kazandı ve ardından **Belgrad Antlaşması** imzalandı.
• Belgrad Antlaşması'nda **Fransa arabuluculuk yaptı**.
• I. Mahmut, Fransa'nın arabuluculuğu nedeniyle 1740'ta Fransa'ya verilen kapitülasyonları **daimi hale getirdi**.
"""
        };

        var notKucukKaynarca = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Küçük Kaynarca Antlaşması",
            Body = """
• **1768-1774 Osmanlı-Rus Savaşı** sonunda Küçük Kaynarca Antlaşması imzalandı.
• 1770'te Ruslar **Çeşme Baskını** ile Osmanlı donanmasını yaktı.
• Kırım bağımsız hale getirildi ve böylece Osmanlı'dan tamamen Müslüman olan bir bölge ilk defa çıktı.
• Kırım halkı dinî ve kültürel açıdan Osmanlı padişahına, yani halifeye bağlı kalmaya devam etti.
• **Halifelik ilk defa siyasi bir güç olarak bir antlaşmada kullanıldı.**
• Rusya İstanbul'da daimî elçi bulundurma ve istediği yerlerde konsolosluk açma hakkı elde etti.
• Osmanlı Devleti tarihinde ilk defa **Rusya'ya savaş tazminatı ödedi**.
"""
        };

        var notKirim = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Kırım'ın Kaybedilme Süreci",
            Body = """
• **1774 Küçük Kaynarca Antlaşması** ile Kırım bağımsız oldu.
• **1779 Aynalıkavak Tenkihnamesi** ile Şahin Giray'ın Kırım hanı olması kabul edildi.
• **1792 Yaş Antlaşması** ile Kırım'ın Rusya'ya ait olduğu kabul edildi.
• Kırım böylece aşamalı olarak Osmanlı Devleti'nin elinden çıktı.
• Yaş Antlaşması ile Osmanlı Devleti'nin gerileme dönemi sona erdi ve dağılma dönemine girildi.
"""
        };

        var notZistoviVeYas = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Ziştovi ve Yaş Antlaşmaları",
            Body = """
• 1787-1792 arasında Osmanlı Devleti, Kırım'ı geri almak amacıyla Rusya ve Avusturya ile savaştı.
• **1791 Ziştovi Antlaşması** ile Avusturya savaştan çekildi.
• Osmanlı Devleti Rusya ile mücadeleye devam etti ancak başarılı olamadı.
• **1792 Yaş Antlaşması** ile Kırım'ın Rusya'ya ait olduğu kabul edildi.
"""
        };

        var notMisirVeDenge = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Mısır'ın İşgali ve Denge Politikası",
            Body = """
• **1798'de Napolyon**, Osmanlı Devleti'ne ait Mısır'a saldırdı.
• Osmanlı Devleti, Fransa'ya karşı ilk defa **denge politikası** uyguladı.
• Bu politikada İngiltere ve Rusya'dan destek alındı.
• **Cezzar Ahmet Paşa**, Akka'da Napolyon'u Nizam-ı Cedit ordusuyla durdurdu.
• Akka Savunması, **Nizam-ı Cedit ordusunun ilk ve son başarısıdır**.
"""
        };

        var notDuveliMuazzama = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Düvel-i Muazzama",
            Body = """
• Osmanlı Devleti, 18. yüzyıldan I. Dünya Savaşı'na kadar Avrupa'nın büyük devletlerine **Düvel-i Muazzama** adını verdi.
• Düvel-i Muazzama devletleri **İngiltere, Fransa, Rusya, Avusturya ve Prusya**dır.
"""
        };

        var notLaleDevri = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Lale Devri",
            Body = """
• **Lale Devri 1718 Pasarofça Antlaşması ile başladı.**
• Dönemin padişahı **III. Ahmet**, sadrazamı **Nevşehirli Damat İbrahim Paşa**dır.
• Lale Devri'nde askerî alanda ıslahat yapılmadı.
• Dönemin ünlü minyatürcüsü **Levni**, ünlü şairi **Nedim**dir.
• İbrahim Müteferrika ve Sait Efendi'nin getirdiği önemli yenilik **matbaa**dır.
• Matbaada basılan ilk eser **Vankulu Lügati**dir.
• Lale Devri, **Patrona Halil İsyanı** ile sona erdi.
"""
        };

        var notLaleYenilikleri = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Lale Devri Yenilikleri",
            Body = """
• İlk geçici elçilikler Lale Devri'nde açıldı.
• **28 Mehmet Çelebi**, Paris'e gönderilen elçidir ve yazdığı **Sefaretname** ile Batı'ya açılan ilk pencere olarak kabul edilir.
• İlk itfaiye teşkilatı olan **Tulumbacılar Ocağı** kuruldu.
• Lale Devri'nde Yalova'da kâğıt fabrikası açıldı.
• Çiçek aşısının getirildiği **Türkiye Mektupları** adlı eserden öğrenilir.
"""
        };

        var notBirinciMahmut = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — I. Mahmut Islahatları",
            Body = """
• I. Mahmut, **Batı tarzında askerî ıslahat yapan ilk Osmanlı padişahıdır**.
• Batı'dan getirilen ilk teknik uzman **Comte de Bonneval**, Osmanlı'da **Humbaracı Ahmet Paşa** olarak tanındı.
• Humbara Ocağı ıslah edildi.
• **Hendeshane**, Batı tarzında açılan ilk teknik okul olarak kabul edildi.
• Kütüphaneler açıldı ve el yazması eserler toplandı.
"""
        };

        var notUcuncuMustafa = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — III. Mustafa Islahatları",
            Body = """
• III. Mustafa döneminde Koca Ragıp Paşa, Cezayirli Gazi Hasan Paşa ve Baron de Tott önemli görevler üstlendi.
• **1773'te Tersane Hendesehanesi** açıldı.
• Devletin iç borçlanma sistemi olan **Esham** uygulaması kanunlaştırıldı.
• Esham sistemi, ileride **kâğıt paraya geçişin temsili aşaması** oldu.
• Topçu Ocağı, **Sürat Topçuları Ocağı**na dönüştürüldü.
"""
        };

        var notBirinciAbdulhamit = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — I. Abdülhamit Islahatları",
            Body = """
• I. Abdülhamit döneminde Baron de Tott'un ıslahatları devam etti.
• Bilim insanlarının kendi kıyafetleri ve dinleriyle çalışmalarına izin verildi.
• Esham sistemi uygulanmaya başladı.
• Tersane Hendesehanesi geliştirilerek **Mühendishane-i Bahr-i Hümayun** adını aldı.
• **Cülus bahşişi verme geleneği kaldırıldı.**
• **Ulufe alım satımı yasaklandı.**
• İstihkâm ve levazım alanlarında yeni okullar açıldı.
"""
        };

        var notUcuncuSelim = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — III. Selim ve Nizam-ı Cedit",
            Body = """
• III. Selim, ıslahatlarında **Fransa'yı örnek aldı**.
• Yaptığı tüm ıslahatlara **Nizam-ı Cedit**, yani yeni düzen adı verildi.
• Nizam-ı Cedit ordusu kuruldu.
• Ordunun masraflarını karşılamak için **İrad-ı Cedit Hazinesi** kuruldu.
• **Mühendishane-i Berr-i Hümayun** açıldı.
• Fransızca, askerî okullarda okutulan ilk yabancı resmî dil oldu.
• Şeyhülislamın yetkileri kısıtlandı ve yerli malı kullanımı teşvik edildi.
• III. Selim dönemi **Kabakçı Mustafa İsyanı** ile sona erdi.
"""
        };

        var notSelimDiplomasi = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — III. Selim Döneminde Diplomasi",
            Body = """
• İlk daimî elçilik **Londra'da** açıldı.
• İlk daimî elçi **Yusuf Agah Efendi**dir.
• Meşveret meclisleri aktif hale getirildi.
• Devlet adamları ve bilim insanlarından **layihalar** istendi.
• Ebubekir Ratıp Efendi Avusturya'ya gönderildi ve hazırladığı raporlar III. Selim'in ıslahatlarını etkiledi.
"""
        };

        var notDigerOnemli = new Note
        {
            Title = "Osmanlı Devleti Gerileme Dönemi — Diğer Önemli Bilgiler",
            Body = """
• 1756-1763 arasındaki **Yedi Yıl Savaşları**nda İngiltere, Fransa'yı yendi.
• Fransız İhtilali 1789'da gerçekleşti ve ulusçuluk akımının yayılmasına neden oldu.
• **Özi Kalesi**, 1788'de Rusların eline geçti.
• Özi'nin alınmasından sonra gerçekleşen katliam haberi I. Abdülhamit'i derinden etkiledi.
"""
        };
        var notLehistanHotinBucas = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Lehistan, Hotin ve Bucaş",
            Body = """
• 1621 Hotin Seferi sırasında yeniçerilerin isteksiz ve disiplinsiz davranması belirginleşmiştir.
• Hotin Seferi'ndeki yeniçeri disiplinsizliği, II. Osman'ın Yeniçeri Ocağı'nı kaldırmayı düşünmesine neden olmuştur.
• II. Osman, yeni bir ordu kurmayı planlamış ancak bu düşüncesini gerçekleştirememiştir.
• II. Osman, Yeniçeri Ocağı'nı kaldırma düşüncesi nedeniyle isyan eden yeniçeriler tarafından Yedikule Zindanları'nda öldürülmüştür.
• 1672 Bucaş Antlaşması ile Osmanlı Devleti batıda en geniş sınırlara ulaşmıştır.
• Podolya, Bucaş Antlaşması ile Osmanlı Devleti'nin eline geçmiştir.
"""
        };
        var notDortBuyukDonanmaYangini = new Note
        {
            Title = "Osmanlı Tarihi — Dört Büyük Donanma Yangını",
            Body = """
• 1571 İnebahtı Deniz Savaşı'nda Osmanlı donanması neredeyse tamamen yok edilmiştir.
• 1770 Çeşme Vakası veya Çeşme Baskını'nda Rus donanması, İzmir Çeşme açıklarında bulunan Osmanlı donanmasını kundak gemileriyle yakmıştır.
• 1827 Navarin Olayı'nda İngiltere, Fransa ve Rusya'nın müttefik donanmaları Osmanlı ve Mısır gemilerini yakmıştır.
• 1853 Sinop Baskını'nda Rus donanması, Sinop Limanı'nda demirleyen Osmanlı fırkateynlerini ateş altına alarak donanmayı yakmıştır.
• Tarihteki dört büyük Osmanlı donanma yangını İnebahtı, Çeşme, Navarin ve Sinop olaylarıdır.
"""
        };

        return new Topic
        {
            Name = "Osmanlı Devleti Gerileme Dönemi",
            Description = "18. yüzyılda Osmanlı Devleti'nin siyasi gelişmeleri, savaşları, antlaşmaları ve ıslahatları",
            Notes =
        {
            notGenel,
            notPrut,
            notPasarofca,
            notBelgrad,
            notKucukKaynarca,
            notKirim,
            notZistoviVeYas,
            notMisirVeDenge,
            notDuveliMuazzama,
            notLaleDevri,
            notLaleYenilikleri,
            notBirinciMahmut,
            notUcuncuMustafa,
            notBirinciAbdulhamit,
            notUcuncuSelim,
            notSelimDiplomasi,
            notDigerOnemli,notLehistanHotinBucas,notDortBuyukDonanmaYangini,


        },
            Questions =
        {


                new Question
{
    Note = notDortBuyukDonanmaYangini,
    Type = QuestionType.MultipleChoice,
    IsNegative = true,
    Text = "Aşağıdakilerden hangisi tarihteki dört büyük Osmanlı donanma yangınından değildir?",
    Explanation = "Tarihteki dört büyük Osmanlı donanma yangını İnebahtı, Çeşme, Navarin ve Sinop olaylarıdır.",
    OrderIndex = 44,
    Choices =
    {
        new Choice { Text = "Preveze Deniz Savaşı", IsCorrect = false, OrderIndex = 1 },

        new Choice { Text = "İnebahtı Deniz Savaşı", IsCorrect = true, OrderIndex = 2 },
        new Choice { Text = "Çeşme Vakası", IsCorrect = true, OrderIndex = 3 },
        new Choice { Text = "Navarin Olayı", IsCorrect = true, OrderIndex = 4 },
        new Choice { Text = "Sinop Baskını", IsCorrect = true, OrderIndex = 5 }
    }
},

new Question
{
    Note = notDortBuyukDonanmaYangini,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1571 yılında Haçlı donanmasının Osmanlı'nın Akdeniz'deki üstünlüğünü kırmak amacıyla harekete geçtiği ve Osmanlı donanmasının neredeyse tamamen yok edildiği olay aşağıdakilerden hangisidir?",
    Explanation = "1571 İnebahtı Deniz Savaşı'nda Osmanlı donanması neredeyse tamamen yok edilmiştir.",
    OrderIndex = 45,
    Choices =
    {
        new Choice { Text = "İnebahtı Deniz Savaşı", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Çeşme Vakası", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Navarin Olayı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sinop Baskını", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Preveze Deniz Savaşı", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notDortBuyukDonanmaYangini,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1768-1774 Osmanlı-Rus Savaşı sırasında Rus donanmasının İzmir Çeşme açıklarında demirli bulunan Osmanlı gemilerini kundak gemileriyle basarak donanmayı tamamen yaktığı olay aşağıdakilerden hangisidir?",
    Explanation = "1770 yılında gerçekleşen Çeşme Vakası veya Çeşme Baskını'nda Osmanlı donanması büyük bir faciayla tamamen yanmıştır.",
    OrderIndex = 46,
    Choices =
    {
        new Choice { Text = "Çeşme Vakası", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İnebahtı Deniz Savaşı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Navarin Olayı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sinop Baskını", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Preveze Deniz Savaşı", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notDortBuyukDonanmaYangini,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Yunan isyanı sırasında İngiltere, Fransa ve Rusya'nın müttefik donanmalarının Osmanlı ve Mısır gemilerini Navarin'de basarak yaktığı olay aşağıdakilerden hangisidir?",
    Explanation = "1827 Navarin Olayı'nda İngiltere, Fransa ve Rusya'nın müttefik donanmaları Osmanlı ve Mısır gemilerini yakmıştır.",
    OrderIndex = 47,
    Choices =
    {
        new Choice { Text = "Navarin Olayı", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İnebahtı Deniz Savaşı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Çeşme Vakası", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sinop Baskını", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Preveze Deniz Savaşı", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notDortBuyukDonanmaYangini,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kırım Savaşı'nın başlarında Rus donanmasının Sinop Limanı'nda demirleyen Osmanlı fırkateynlerine sürpriz baskın düzenleyerek donanmayı ateş altına aldığı ve yaktığı olay aşağıdakilerden hangisidir?",
    Explanation = "1853 Sinop Baskını'nda Rus donanması, Sinop Limanı'ndaki Osmanlı donanmasını ateş altına alarak büyük ölçüde yok etmiştir.",
    OrderIndex = 48,
    Choices =
    {
        new Choice { Text = "Sinop Baskını", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İnebahtı Deniz Savaşı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Çeşme Vakası", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Navarin Olayı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Preveze Deniz Savaşı", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = notLehistanHotinBucas,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti batıda en geniş sınırlara hangi antlaşma ile ulaşmıştır?",
    Explanation = "1672 Bucaş Antlaşması ile Osmanlı Devleti batıda en geniş sınırlara ulaşmıştır.",
    OrderIndex = 43,
    Choices =
    {
        new Choice { Text = "Bucaş Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},
            new Question
            {
                Note = notGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Avrupa devletlerinin amaca ulaşmak için her türlü tedbire başvurmasına ne ad verilir?",
                Explanation = "Ders notunda bu anlayış Makyavelizm olarak açıklanmıştır.",
                OrderIndex = 1,
                Choices =
                {
                    new Choice { Text = "Makyavelizm", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Panslavizm", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Grek Projesi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Dakya Projesi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Denge politikası", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Avusturya ve Rusya'nın Eflak ve Boğdan'da ortak denetim altında bir devlet kurmayı amaçladığı proje hangisidir?",
                Explanation = "Dakya Projesi, Eflak ve Boğdan'da bir devlet kurmayı amaçlamıştır.",
                OrderIndex = 2,
                Choices =
                {
                    new Choice { Text = "Dakya Projesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Grek Projesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Nizam-ı Cedit", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Esham Sistemi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Lale Devri", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notGenel,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "İstanbul merkezli bir Rus devleti kurarak Bizans'ı yeniden canlandırmayı amaçlayan proje hangisidir?",
                Explanation = "Grek Projesi'nin amacı Bizans'ı yeniden canlandırmaktı.",
                OrderIndex = 3,
                Choices =
                {
                    new Choice { Text = "Grek Projesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Dakya Projesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Panslavizm", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Nizam-ı Cedit", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "İrad-ı Cedit", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notPrut,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1711 Prut Antlaşması'nın Osmanlı Devleti açısından önemli sonuçlarından biri aşağıdakilerden hangisidir?",
                Explanation = "Prut Antlaşması ile Azak Kalesi Osmanlı Devleti'ne geri verilmiştir.",
                OrderIndex = 4,
                Choices =
                {
                    new Choice { Text = "Azak Kalesi'nin geri alınması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kırım'ın bağımsız olması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Batı'nın üstünlüğünün kabul edilmesi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kapitülasyonların daimi hale gelmesi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kırım'ın Rusya'ya bırakılması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notPrut,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı ülkesinde uzun süre kaldığı için Demirbaş Şarl olarak anılan hükümdar kimdir?",
                Explanation = "İsveç Kralı XII. Şarl Osmanlı ülkesinde kaldığı için Demirbaş Şarl olarak anılmıştır.",
                OrderIndex = 5,
                Choices =
                {
                    new Choice { Text = "XII. Şarl", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Petro", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Napolyon", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Potemkin", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "XVI. Louis", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notPasarofca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin Batı'nın üstünlüğünü ilk defa kabul ettiği antlaşma hangisidir?",
                Explanation = "1718 Pasarofça Antlaşması ile Osmanlı Devleti Batı'nın üstünlüğünü kabul etmiştir.",
                OrderIndex = 6,
                Choices =
                {
                    new Choice { Text = "Pasarofça Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Prut Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Belgrad Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Yaş Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notPasarofca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Lale Devri hangi antlaşmanın ardından başlamıştır?",
                Explanation = "Lale Devri 1718 Pasarofça Antlaşması'nın ardından başlamıştır.",
                OrderIndex = 7,
                Choices =
                {
                    new Choice { Text = "Pasarofça Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Prut Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Belgrad Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ziştovi Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Yaş Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBelgrad,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1739 Belgrad Antlaşması'nda Osmanlı Devleti adına arabuluculuk yapan devlet hangisidir?",
                Explanation = "Belgrad Antlaşması'nda Fransa arabuluculuk yapmıştır.",
                OrderIndex = 8,
                Choices =
                {
                    new Choice { Text = "Fransa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "İngiltere", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Rusya", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Avusturya", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Prusya", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBelgrad,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Fransa'ya verilen kapitülasyonları 1740'ta daimi hale getiren Osmanlı padişahı kimdir?",
                Explanation = "I. Mahmut, Fransa'nın Belgrad Antlaşması'ndaki arabuluculuğu nedeniyle kapitülasyonları daimi hale getirmiştir.",
                OrderIndex = 9,
                Choices =
                {
                    new Choice { Text = "I. Mahmut", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "III. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "III. Mustafa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "I. Abdülhamit", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "III. Selim", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKucukKaynarca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1770'te Rusların Osmanlı donanmasını yaktığı olay hangisidir?",
                Explanation = "1768-1774 Osmanlı-Rus Savaşı sırasında Ruslar Çeşme Baskını ile Osmanlı donanmasını yakmıştır.",
                OrderIndex = 10,
                Choices =
                {
                    new Choice { Text = "Çeşme Baskını", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Akka Savunması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Peter Varadin Savaşı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Prut Savaşı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Belgrad Savaşı", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKucukKaynarca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Kırım'ın bağımsız hale gelmesini sağlayan antlaşma hangisidir?",
                Explanation = "1774 Küçük Kaynarca Antlaşması ile Kırım bağımsız hale getirilmiştir.",
                OrderIndex = 11,
                Choices =
                {
                    new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Yaş Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ziştovi Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Pasarofça Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Prut Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKucukKaynarca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Halifeliğin ilk defa siyasi bir güç olarak antlaşma metninde kullanıldığı antlaşma hangisidir?",
                Explanation = "Küçük Kaynarca Antlaşması'nda Kırım halkının dinî açıdan halifeye bağlı kalması kararlaştırılmıştır.",
                OrderIndex = 12,
                Choices =
                {
                    new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Pasarofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Belgrad Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Prut Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ziştovi Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKucukKaynarca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti tarihinde ilk defa savaş tazminatı hangi devlete ödenmiştir?",
                Explanation = "Küçük Kaynarca Antlaşması ile Osmanlı Devleti ilk savaş tazminatını Rusya'ya ödemiştir.",
                OrderIndex = 13,
                Choices =
                {
                    new Choice { Text = "Rusya", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Fransa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "İngiltere", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Avusturya", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Prusya", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKucukKaynarca,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Küçük Kaynarca Antlaşması ile Rusya'nın elde ettiği ve Osmanlı Devleti'nin iç işlerine müdahalesini kolaylaştıran gelişme aşağıdakilerden hangisidir?",
                Explanation = "Rusya'nın istediği yerlerde konsolosluk açabilmesi Osmanlı iç işlerine müdahalesini kolaylaştırmıştır.",
                OrderIndex = 14,
                Choices =
                {
                    new Choice { Text = "İstediği yerlerde konsolosluk açabilmesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kırım'ı doğrudan Osmanlı'ya bağlaması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Lale Devri'ni başlatması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Cülus bahşişini kaldırması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Nizam-ı Cedit ordusunu kurması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKirim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Kırım'ın Rusya'ya ait olduğunun kabul edildiği antlaşma hangisidir?",
                Explanation = "1792 Yaş Antlaşması ile Kırım'ın Rusya'ya ait olduğu kabul edilmiştir.",
                OrderIndex = 15,
                Choices =
                {
                    new Choice { Text = "Yaş Antlaşması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Aynalıkavak Tenkihnamesi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ziştovi Antlaşması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Pasarofça Antlaşması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notKirim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1779 Aynalıkavak Tenkihnamesi ile Kırım'da han olması kabul edilen kişi kimdir?",
                Explanation = "Aynalıkavak Tenkihnamesi ile Şahin Giray'ın Kırım hanı olması kabul edilmiştir.",
                OrderIndex = 16,
                Choices =
                {
                    new Choice { Text = "Şahin Giray", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "XII. Şarl", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Napolyon", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Potemkin", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notZistoviVeYas,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1791 Ziştovi Antlaşması'nın sonucu aşağıdakilerden hangisidir?",
                Explanation = "Ziştovi Antlaşması ile Avusturya savaştan çekilmiştir.",
                OrderIndex = 17,
                Choices =
                {
                    new Choice { Text = "Avusturya'nın savaştan çekilmesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kırım'ın bağımsız olması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Kırım'ın Rusya'ya bırakılması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Batı'nın üstünlüğünün kabul edilmesi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Lale Devri'nin başlaması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notMisirVeDenge,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin ilk defa denge politikası uyguladığı olay hangisidir?",
                Explanation = "1798'de Napolyon'un Mısır'a saldırması üzerine Osmanlı Devleti Fransa'ya karşı denge politikası uygulamıştır.",
                OrderIndex = 18,
                Choices =
                {
                    new Choice { Text = "Napolyon'un Mısır'a saldırması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Prut Savaşı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Peter Varadin Savaşı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Patrona Halil İsyanı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kabakçı Mustafa İsyanı", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notMisirVeDenge,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Napolyon'u Akka'da durduran Osmanlı devlet adamı kimdir?",
                Explanation = "Cezzar Ahmet Paşa, Akka'da Napolyon'u durdurmuştur.",
                OrderIndex = 19,
                Choices =
                {
                    new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Nevşehirli Damat İbrahim Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Humbaracı Ahmet Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Koca Ragıp Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Yusuf Agah Efendi", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notDuveliMuazzama,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin Avrupa'nın büyük devletleri için kullandığı ifade aşağıdakilerden hangisidir?",
                Explanation = "İngiltere, Fransa, Rusya, Avusturya ve Prusya için Düvel-i Muazzama ifadesi kullanılmıştır.",
                OrderIndex = 20,
                Choices =
                {
                    new Choice { Text = "Düvel-i Muazzama", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Nizam-ı Cedit", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Makyavelizm", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Panslavizm", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Esham", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleDevri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Lale Devri'nin padişahı aşağıdakilerden hangisidir?",
                Explanation = "Lale Devri III. Ahmet döneminde yaşanmıştır.",
                OrderIndex = 21,
                Choices =
                {
                    new Choice { Text = "III. Ahmet", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "I. Mahmut", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "III. Mustafa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "I. Abdülhamit", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "III. Selim", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleDevri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Lale Devri'nin ünlü sadrazamı kimdir?",
                Explanation = "Lale Devri'nin önemli devlet adamı ve sadrazamı Nevşehirli Damat İbrahim Paşa'dır.",
                OrderIndex = 22,
                Choices =
                {
                    new Choice { Text = "Nevşehirli Damat İbrahim Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Humbaracı Ahmet Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Koca Ragıp Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Halil Hamit Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleDevri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Lale Devri'ni sona erdiren isyan hangisidir?",
                Explanation = "Lale Devri Patrona Halil İsyanı ile sona ermiştir.",
                OrderIndex = 23,
                Choices =
                {
                    new Choice { Text = "Patrona Halil İsyanı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kabakçı Mustafa İsyanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Celali İsyanları", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Edirne Olayı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Şeyh Celal İsyanı", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleDevri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Lale Devri'nin ünlü minyatürcüsü kimdir?",
                Explanation = "Ders notunda Lale Devri'nin ünlü minyatürcüsü Levni olarak verilmiştir.",
                OrderIndex = 24,
                Choices =
                {
                    new Choice { Text = "Levni", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Nedim", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "28 Mehmet Çelebi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Humbaracı Ahmet", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Baron de Tott", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleDevri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "İbrahim Müteferrika ve Sait Efendi'nin Osmanlı ülkesine getirdiği önemli yenilik hangisidir?",
                Explanation = "İbrahim Müteferrika ve Sait Efendi matbaanın Osmanlı ülkesine getirilmesinde etkili olmuştur.",
                OrderIndex = 25,
                Choices =
                {
                    new Choice { Text = "Matbaa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Telgraf", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Demiryolu", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Nizam-ı Cedit Ordusu", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "İrad-ı Cedit Hazinesi", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleDevri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı matbaasında basılan ilk eser hangisidir?",
                Explanation = "Ders notunda matbaada basılan ilk eser Vankulu Lügati olarak verilmiştir.",
                OrderIndex = 26,
                Choices =
                {
                    new Choice { Text = "Vankulu Lügati", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sefaretname", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Türkiye Mektupları", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Nizam-ı Cedit", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Layihalar", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notLaleYenilikleri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Lale Devri'nde Paris'e gönderilen ve yazdığı eser Batı'ya açılan ilk pencere kabul edilen kişi kimdir?",
                Explanation = "28 Mehmet Çelebi Paris'e gönderilmiş ve Sefaretname yazmıştır.",
                OrderIndex = 27,
                Choices =
                {
                    new Choice { Text = "28 Mehmet Çelebi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Yusuf Agah Efendi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ebubekir Ratıp Efendi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Baron de Tott", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBirinciMahmut,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Batı tarzında askerî ıslahat yapan ilk Osmanlı padişahı kimdir?",
                Explanation = "Ders notunda I. Mahmut, Batı tarzında askerî ıslahat yapan ilk Osmanlı padişahı olarak verilmiştir.",
                OrderIndex = 28,
                Choices =
                {
                    new Choice { Text = "I. Mahmut", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "III. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "III. Mustafa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "I. Abdülhamit", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "III. Selim", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBirinciMahmut,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Comte de Bonneval'in Osmanlı Devleti'nde Müslüman olduktan sonra aldığı ad hangisidir?",
                Explanation = "Comte de Bonneval, Osmanlı hizmetinde Humbaracı Ahmet Paşa olarak tanınmıştır.",
                OrderIndex = 29,
                Choices =
                {
                    new Choice { Text = "Humbaracı Ahmet Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Koca Ragıp Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Yusuf Agah Efendi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ebubekir Ratıp Efendi", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBirinciMahmut,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde Batı tarzında açılan ilk teknik okul hangisidir?",
                Explanation = "Hendeshane, ders notunda Batı tarzında açılan ilk teknik okul olarak verilmiştir.",
                OrderIndex = 30,
                Choices =
                {
                    new Choice { Text = "Hendeshane", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mühendishane-i Bahr-i Hümayun", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Mühendishane-i Berr-i Hümayun", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Matbaa-i Amire", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Zahire Nazırlığı", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notUcuncuMustafa,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin iç borçlanma sistemi olarak uyguladığı yöntem hangisidir?",
                Explanation = "III. Mustafa döneminde iç borçlanma sistemi olan Esham kanunlaştırılmıştır.",
                OrderIndex = 31,
                Choices =
                {
                    new Choice { Text = "Esham", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "İrad-ı Cedit", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Nizam-ı Cedit", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Layihalar", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kapitülasyon", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notUcuncuMustafa,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Esham sisteminin Osmanlı maliye tarihi açısından önemli özelliği aşağıdakilerden hangisidir?",
                Explanation = "Esham sistemi, ileride kâğıt paraya geçişin temsili aşaması olarak kabul edilmiştir.",
                OrderIndex = 32,
                Choices =
                {
                    new Choice { Text = "Kâğıt paraya geçişin temsili aşaması olması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "İlk dış borçlanma olması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Savaş tazminatı sistemi olması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Yeniçeri maaşlarının kaldırılması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kapitülasyonların kaldırılması", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBirinciAbdulhamit,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Cülus bahşişi verme geleneğini kaldıran Osmanlı padişahı kimdir?",
                Explanation = "Ders notunda I. Abdülhamit'in cülus bahşişi verme geleneğini kaldırdığı belirtilmiştir.",
                OrderIndex = 33,
                Choices =
                {
                    new Choice { Text = "I. Abdülhamit", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "III. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "I. Mahmut", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "III. Mustafa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "III. Selim", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notBirinciAbdulhamit,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Ulufe alım satımını yasaklayan Osmanlı padişahı kimdir?",
                Explanation = "I. Abdülhamit döneminde ulufe alım satımı yasaklanmıştır.",
                OrderIndex = 34,
                Choices =
                {
                    new Choice { Text = "I. Abdülhamit", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "III. Ahmet", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "I. Mahmut", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "III. Mustafa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "III. Selim", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notUcuncuSelim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "III. Selim'in yaptığı tüm ıslahatlara verilen genel ad hangisidir?",
                Explanation = "III. Selim dönemindeki tüm ıslahatlar Nizam-ı Cedit adı altında toplanmıştır.",
                OrderIndex = 35,
                Choices =
                {
                    new Choice { Text = "Nizam-ı Cedit", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Esham", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "İrad-ı Cedit", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Lale Devri", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Makyavelizm", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notUcuncuSelim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "III. Selim'in Nizam-ı Cedit ıslahatlarında örnek aldığı devlet hangisidir?",
                Explanation = "III. Selim ıslahatlarında Fransa'yı örnek almıştır.",
                OrderIndex = 36,
                Choices =
                {
                    new Choice { Text = "Fransa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Rusya", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Avusturya", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "İngiltere", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Prusya", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notUcuncuSelim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Nizam-ı Cedit ordusunun masraflarını karşılamak amacıyla kurulan hazine hangisidir?",
                Explanation = "III. Selim, Nizam-ı Cedit ordusunun masrafları için İrad-ı Cedit Hazinesi'ni kurmuştur.",
                OrderIndex = 37,
                Choices =
                {
                    new Choice { Text = "İrad-ı Cedit Hazinesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Esham Hazinesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Hazine-i Hümayun", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Mühendishane Hazinesi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Düvel-i Muazzama Hazinesi", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notUcuncuSelim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "III. Selim dönemini sona erdiren isyan hangisidir?",
                Explanation = "III. Selim dönemi Kabakçı Mustafa İsyanı ile sona ermiştir.",
                OrderIndex = 38,
                Choices =
                {
                    new Choice { Text = "Kabakçı Mustafa İsyanı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Patrona Halil İsyanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Celali İsyanları", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Edirne Olayı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Şeyh Celal İsyanı", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notSelimDiplomasi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin ilk daimî elçiliği hangi şehirde açılmıştır?",
                Explanation = "III. Selim döneminde ilk daimî elçilik Londra'da açılmıştır.",
                OrderIndex = 39,
                Choices =
                {
                    new Choice { Text = "Londra", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Paris", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Viyana", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Moskova", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Berlin", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notSelimDiplomasi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin ilk daimî elçisi kimdir?",
                Explanation = "İlk daimî elçi Yusuf Agah Efendi'dir.",
                OrderIndex = 40,
                Choices =
                {
                    new Choice { Text = "Yusuf Agah Efendi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "28 Mehmet Çelebi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ebubekir Ratıp Efendi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Humbaracı Ahmet Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notSelimDiplomasi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "III. Selim'in ıslahatlarından önce devlet adamları ve bilim insanlarından istediği raporlara ne ad verilir?",
                Explanation = "Bu raporlar layihalar olarak adlandırılmıştır.",
                OrderIndex = 41,
                Choices =
                {
                    new Choice { Text = "Layihalar", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sefaretnameler", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Kapitülasyonlar", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Eshamlar", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Fermanlar", IsCorrect = false, OrderIndex = 5 }
                }
            },
            new Question
            {
                Note = notDigerOnemli,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1789'da gerçekleşerek ulusçuluk akımının yayılmasına neden olan olay hangisidir?",
                Explanation = "Fransız İhtilali ulusçuluk akımının yayılmasına neden olmuştur.",
                OrderIndex = 42,
                Choices =
                {
                    new Choice { Text = "Fransız İhtilali", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Prut Antlaşması", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Pasarofça Antlaşması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Çeşme Baskını", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kabakçı Mustafa İsyanı", IsCorrect = false, OrderIndex = 5 }
                }
            }
        }
        };
    }
    private static Topic BuildOnDokuzOsmanlı()
    {
        // Notes = { not1, not2, ... },
        // Questions = { soru1, soru2, ... },
        var notDengePolitikasi = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Denge Politikası",
            Body = """
• XIX. yüzyılda Osmanlı Devleti, Avrupa'nın büyük devletleri karşısında tek başına mücadele etmekte zorlanmıştır.
• Osmanlı Devleti, İngiltere, Fransa, Rusya ve diğer Avrupa devletlerinin çıkar çatışmalarından yararlanarak varlığını sürdürmeye çalışmıştır.
• Bu dış politika anlayışına Denge Politikası denir.
• Denge Politikasının temel amacı Osmanlı Devleti'nin toprak bütünlüğünü ve siyasi varlığını korumaktır.
"""
        };

        var notOsmanlicilik = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Osmanlıcılık",
            Body = """
• Osmanlıcılık, Osmanlı Devleti'nin dağılmasını önlemek amacıyla ortaya çıkan fikir akımlarından biridir.
• Dil, din ve ırk farkı gözetilmeksizin Osmanlı Devleti sınırları içindeki herkesin Osmanlı vatandaşı kabul edilmesi amaçlanmıştır.
• Temel hedef, bütün Osmanlı toplumlarını ortak bir Osmanlı milleti anlayışı altında birleştirmektir.
"""
        };

        var notSirpIsyanlari = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Sırp İsyanları",
            Body = """
• XIX. yüzyılda Osmanlı Devleti'ne karşı isyan eden ilk azınlık Sırplardır.
• Sırpların isyan sürecinde Rusya önemli destek sağlamıştır.
• Rusya'nın Sırpları desteklemesinde Balkanlarda etkisini artırmak ve sıcak denizlere ulaşma politikası etkili olmuştur.
• 1812 Bükreş Antlaşması ile Sırplar ilk kez imtiyaz elde etmiştir.
• 1829 Edirne Antlaşması ile Sırplar özerklik kazanmıştır.
• 1878 Berlin Antlaşması ile Sırbistan tam bağımsızlığını elde etmiştir.
• Sırpların elde ettiği hakların sıralaması imtiyaz, özerklik ve bağımsızlık şeklindedir.
"""
        };

        var notYunanIsyani = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Yunan İsyanı ve Bağımsızlık",
            Body = """
• Osmanlı Devleti'nden ayrılarak bağımsızlığını kazanan ilk azınlık Yunanlılardır.
• Yunan İsyanı sırasında Osmanlı Devleti, Mısır Valisi Kavalalı Mehmet Ali Paşa'dan yardım istemiştir.
• Kavalalı Mehmet Ali Paşa, yardım karşılığında Girit ve Mora valiliklerini talep etmiştir.
• 1827 Navarin Olayı'nda İngiltere, Fransa ve Rusya'nın müttefik donanmaları Osmanlı ve Mısır donanmalarını yakmıştır.
• 1829 Edirne Antlaşması ile Yunanistan'ın bağımsızlığı kabul edilmiştir.
• Londra Konferansı'nda Yunanistan'ın bağımsız bir krallık olması kararlaştırılmıştır.
"""
        };

        var notKavalaliMehmetAliPasa = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Kavalalı Mehmet Ali Paşa ve Mısır Sorunu",
            Body = """
• Kavalalı Mehmet Ali Paşa, Osmanlı Devleti'nin Mısır valisidir.
• Mora ve Girit isyanlarında kendisine vaat edilen bazı valiliklerin verilmemesi Kavalalı'nın Osmanlı Devleti ile arasının açılmasında etkili olmuştur.
• Kavalalı Mehmet Ali Paşa, daha fazla toprak ve siyasi güç elde etmek istemiştir.
• Osmanlı Devleti'nin merkezi otoritesinin zayıflaması da Kavalalı'nın isyanında etkili olmuştur.
• Osmanlı Devleti ile Kavalalı Mehmet Ali Paşa arasındaki mücadele Mısır Sorunu olarak adlandırılmıştır.
• Mısır Sorunu, Osmanlı Devleti'nin iç meselesiyken büyük Avrupa devletlerinin müdahalesiyle uluslararası bir sorun haline gelmiştir.
"""
        };

        var notKutahyaAntlasmasi = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Kütahya Antlaşması",
            Body = """
• Osmanlı Devleti ile Kavalalı Mehmet Ali Paşa arasındaki mücadelede tarafların birbirine kesin üstünlük sağlayamaması üzerine dış güçler araya girmiştir.
• 1833 Kütahya Antlaşması imzalanmıştır.
• Antlaşma ile Kavalalı Mehmet Ali Paşa'ya Mısır ve Cidde valilikleri verilmiştir.
• Şam ve Adana mukataaları da Kavalalı Mehmet Ali Paşa'nın yönetimine bırakılmıştır.
• Kütahya Antlaşması, Osmanlı Devleti'nin kendi valisiyle yaptığı bir iç mesele antlaşması olarak değerlendirilir.
"""
        };

        var notHunkarIskelesi = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Hünkâr İskelesi Antlaşması",
            Body = """
• Kavalalı Mehmet Ali Paşa'nın yeniden Osmanlı Devleti'ni zor durumda bırakması üzerine Osmanlı Devleti dış destek aramıştır.
• Osmanlı Devleti, geleneksel rakibi Rusya ile 1833 Hünkâr İskelesi Antlaşması'nı imzalamıştır.
• Antlaşma Osmanlı Devleti ile Rusya arasında yapılmıştır.
• Antlaşmanın süresi 8 yıl olarak belirlenmiştir.
• Antlaşma ile Rusya, Boğazlar üzerinde önemli bir avantaj elde etmiştir.
• İngiltere ve Fransa, Rusya'nın Boğazlar üzerinde güç kazanmasından rahatsız olmuştur.
• Boğazlar Sorunu uluslararası bir statüye Hünkâr İskelesi Antlaşması ile değil, daha sonra Londra Boğazlar Sözleşmesi ile kavuşmuştur.
"""
        };

        var notBaltaLimani = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Balta Limanı Ticaret Sözleşmesi",
            Body = """
• 1838 Balta Limanı Ticaret Sözleşmesi Osmanlı Devleti ile İngiltere arasında imzalanmıştır.
• İngiliz tüccarlara Osmanlı topraklarında önemli ticari ve gümrük kolaylıkları tanınmıştır.
• Yabancı tüccarların Osmanlı iç ticaretinde daha avantajlı hale gelmesinin önü açılmıştır.
• Sözleşme, Osmanlı iç pazarında yabancı tüccarların etkisinin artmasına neden olmuştur.
• Osmanlı ekonomisinin dışa bağımlılığını artıran gelişmelerden biri olarak değerlendirilir.
"""
        };

        var notLondraBogazlar = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Londra Boğazlar Sözleşmesi",
            Body = """
• Hünkâr İskelesi Antlaşması'nın süresinin dolmasının ardından Boğazlar Sorunu yeniden gündeme gelmiştir.
• 1841 Londra Boğazlar Sözleşmesi İngiltere, Fransa, Rusya, Avusturya ve Prusya'nın katılımıyla düzenlenmiştir.
• Boğazlar bütün yabancı savaş gemilerine kapatılmıştır.
• Boğazlar Sorunu uluslararası bir statü kazanmıştır.
"""
        };

        var notMilliyetcilikAzınliklar = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — Milliyetçilik ve Azınlık İsyanları",
            Body = """
• Fransız İhtilali sonrasında yayılan milliyetçilik düşüncesi çok uluslu devletleri etkilemiştir.
• Osmanlı Devleti sınırları içindeki bazı azınlıklar bağımsızlık amacıyla isyan etmiştir.
• XIX. yüzyılda Osmanlı Devleti'ne karşı ilk isyan eden azınlık Sırplardır.
• Osmanlı Devleti'nden ayrılarak bağımsızlığını kazanan ilk azınlık ise Yunanlılardır.
• Bu nedenle süreçte ilk ayaklanan millet Sırplar, ilk bağımsız olan millet Yunanlılardır.
"""
        };

        var notMisirSorunuLondra = new Note
        {
            Title = "XIX. Yüzyıl Osmanlı Devleti — 1840 Londra Konferansı ve Mısır",
            Body = """
• Mısır Sorunu'nun uluslararası boyuta ulaşması üzerine büyük Avrupa devletleri sürece müdahale etmiştir.
• 1840 Londra Konferansı ile Mısır Sorunu çözülmeye çalışılmıştır.
• Mısır, Osmanlı Devleti'ne bağlı olmakla birlikte iç işlerinde geniş yetkilere sahip bir yönetim haline gelmiştir.
• Kavalalı Mehmet Ali Paşa'nın ailesine Mısır yönetiminde kalıcı haklar tanınmıştır.
"""
        };
        var notDuraklamaNedenleri = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Duraklamanın Nedenleri",
            Body = """
• Osmanlı Devleti'nin duraklamasında merkezi otoritenin bozulması etkili olmuştur.
• Küçük yaştaki ve deneyimsiz padişahların tahta çıkması devlet yönetimini olumsuz etkilemiştir.
• Saray kadınlarının devlet yönetimine karışması taht mücadelelerini artırmıştır.
• Saray masraflarının artması devlet ekonomisini zorlamıştır.
• Rüşvet ve adam kayırma yaygınlaşmıştır.
• Ganimet gelirleri azalmıştır.
• Savaşların uzaması ve mağlubiyetlerin artması devleti yıpratmıştır.
• Beşik ulemalığı sisteminin uygulanması eğitim alanında liyakat sorununa yol açmıştır.
• Medreselerden pozitif bilimlerin çıkarılması bilimsel gelişmeyi olumsuz etkilemiştir.
• Kapitülasyonların yaygınlaşması ekonomik kayıplara neden olmuştur.
• Sık padişah değişikliği devlet yönetiminde istikrarsızlığa yol açmıştır.
• Coğrafi keşifler sonucunda İpek ve Baharat yollarının önem kaybetmesi Osmanlı ekonomisini olumsuz etkilemiştir.
• Doğal sınırlara ulaşılması fetih hareketlerinin yavaşlamasına neden olmuştur.
• Avrupa'nın bilimsel ve teknolojik gelişimine Osmanlı Devleti ayak uyduramamıştır.
• Tımar sisteminin bozulması ve iltizam sisteminin yaygınlaşması devletin ekonomik ve askerî yapısını olumsuz etkilemiştir.
"""
        };

        var notKafesUsulu = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Kafes Usulü",
            Body = """
• III. Mehmet'ten sonra şehzadelerin sancağa gönderilmesi uygulaması kaldırılmıştır.
• Şehzadeler sarayda Şimşirlik adı verilen bölümlerde yetiştirilmeye başlanmıştır.
• Bu uygulama halk arasında kafes usulü olarak adlandırılmıştır.
• Kafes usulü şehzadelerin devlet yönetimi konusunda deneyim kazanmalarını engellemiştir.
• Kafes usulü sonucunda deneyimsiz ve tecrübesiz padişahlar tahta çıkmıştır.
• Şehzadelerin sürekli ölüm korkusu içinde yetişmesi bazı padişahların psikolojik olarak olumsuz etkilenmesine neden olmuştur.
"""
        };

        var notTimarSistemi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Tımar Sisteminin Önemi",
            Body = """
• Tımar sistemi toprağın boş kalmasını engellemiştir.
• Tımar sistemi sayesinde düzenli vergi toplanmıştır.
• Devlet memurlarının maaşları tımar gelirlerinden karşılanmıştır.
• Tımar sistemi sayesinde devlet hazinesinden para harcamadan asker yetiştirilmiştir.
• Tımarlı sipahiler bulundukları bölgelerin güvenliğine katkı sağlamıştır.
• Tımar sisteminin bozulması üretimin, vergi gelirlerinin ve askerî gücün olumsuz etkilenmesine neden olmuştur.
"""
        };

        var notYeniceriOcagi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Yeniçeri Ocağının Bozulması",
            Body = """
• Devşirme kanununa aykırı şekilde Yeniçeri Ocağına asker alınmıştır.
• Yeniçeriler devlet için asker anlayışını terk ederek devlet asker içindir anlayışını benimsemeye başlamıştır.
• Yeniçeriler ulufe ve cülus bahşişi için sık sık padişah değişikliğine karışmıştır.
• Ulufelerin düşük ayarlı ödenmesi yeniçeri isyanlarının nedenlerinden biri olmuştur.
• Yeniçeri ağaları devlet yönetiminde etkili olmaya başlamıştır.
• Yeniçerilerin evlenmesi ve ticaretle uğraşması askerî disiplinin bozulmasına neden olmuştur.
"""
        };

        var notGiritSeferi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Girit'in Fethi",
            Body = """
• Osmanlı Devleti 1645 yılında Girit'i kuşatmaya başlamıştır.
• Girit'in fethi 24 yıl sürmüş ve ada 1669 yılında Osmanlı Devleti tarafından alınmıştır.
• Girit'in uzun süre kuşatma altında kalması Osmanlı Devleti'nin askerî ve ekonomik açıdan yıpranmasına neden olmuştur.
• Girit'in kuşatılması sırasında Venedik, Boğazları ve çevresini abluka altına almıştır.
"""
        };

        var notOsmanliRusya = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Osmanlı-Rusya İlişkileri",
            Body = """
• 1681 Bahçesaray Antlaşması Osmanlı Devleti ile Rusya arasında imzalanan ilk antlaşmadır.
• Bahçesaray Antlaşması'nın diğer adı Çehrin Antlaşmasıdır.
• Osmanlı Devleti ile Rusya arasındaki mücadelelerde Rusya'nın temel hedeflerinden biri Karadeniz'e ve Boğazlar üzerinden Akdeniz'e ulaşmaktır.
• 1700 İstanbul Antlaşması, Karlofça Antlaşması'nın devamı niteliğindedir.
• İstanbul Antlaşması ile Azak Kalesi Rusya'ya bırakılmıştır.
• Rusya, Azak Kalesi'ni alarak Karadeniz'e inme konusunda önemli bir fırsat elde etmiştir.
"""
        };

        var notOsmanliIran = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Osmanlı-İran İlişkileri",
            Body = """
• XVII. yüzyılda Osmanlı Devleti'nin en çok mücadele ettiği devletlerden biri İran'dır.
• 1590 Ferhat Paşa Antlaşması ile Osmanlı Devleti doğuda en geniş sınırlara ulaşmıştır.
• IV. Murat döneminde İran üzerine iki Irak Seferi düzenlenmiştir.
• 1639 Kasr-ı Şirin Antlaşması Osmanlı Devleti ile İran arasında imzalanmıştır.
• Kasr-ı Şirin Antlaşması ile Bağdat Osmanlı Devleti'nde kalmıştır.
• Kasr-ı Şirin Antlaşması günümüzde Türkiye'nin en eski sınırını büyük ölçüde belirleyen antlaşmadır.
"""
        };

        var notHotinSeferi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Hotin Seferi",
            Body = """
• 1621 yılında Osmanlı Devleti Lehistan üzerine Hotin Seferi düzenlemiştir.
• Hotin Seferi sırasında yeniçerilerin disiplinsiz ve isteksiz davranışları belirginleşmiştir.
• Yeniçerilerin bu tutumu II. Osman'ın Yeniçeri Ocağını kaldırmayı düşünmesine neden olmuştur.
• II. Osman yeni bir ordu kurmayı planlamıştır.
• Yeniçeri Ocağını kaldırma düşüncesi II. Osman'ın yeniçeriler tarafından öldürülmesine giden süreçte etkili olmuştur.
"""
        };

        var notBucasAntlasmasi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Bucaş Antlaşması",
            Body = """
• 1672 yılında Osmanlı Devleti ile Lehistan arasında Bucaş Antlaşması imzalanmıştır.
• Bucaş Antlaşması ile Osmanlı Devleti batıda en geniş sınırlara ulaşmıştır.
• Podolya Osmanlı Devleti'nin eline geçmiştir.
• Podolya, Osmanlı Devleti'nin batıda aldığı son önemli topraklardan biri olarak kabul edilir.
"""
        };

        var notHacovaSavasi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Haçova Meydan Muharebesi",
            Body = """
• 1596 Haçova Meydan Muharebesi Osmanlı Devleti ile Avusturya arasında gerçekleşmiştir.
• Savaş sırasında Osmanlı ordusunun bazı askerleri geri çekilmiştir.
• Hoca Sadeddin Efendi'nin gayreti ve geri hizmette bulunanların mücadeleye katılması savaşın Osmanlı Devleti lehine dönmesini sağlamıştır.
• Geri hizmettekilerin kepçe ve kazan gibi araçlarla savaşa katılması nedeniyle Haçova Meydan Muharebesi Kepçe Kazan Savaşı olarak da adlandırılır.
• Haçova Meydan Muharebesi sonucunda Eğri, Estergon ve Kanije kaleleri Osmanlı Devleti'nin kontrolüne geçmiştir.
"""
        };

        var notZitvatorokAntlasmasi = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Zitvatorok Antlaşması",
            Body = """
• 1606 yılında Osmanlı Devleti ile Avusturya arasında Zitvatorok Antlaşması imzalanmıştır.
• Zitvatorok Antlaşması ile Osmanlı padişahı ile Avusturya hükümdarı siyasi bakımdan eşit kabul edilmiştir.
• Zitvatorok Antlaşması ile Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğü sona ermiştir.
"""
        };

        var notCelaliNedenleri = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Celali İsyanlarının Nedenleri",
            Body = """
• Celali İsyanlarının nedenlerinden biri ekonominin bozulması ve fiyatların artmasıdır.
• Nüfusun hızla artması isyanların nedenlerinden biridir.
• Ağır vergiler köylülerin ekonomik açıdan zor durumda kalmasına neden olmuştur.
• Merkezi otoritenin ve devlet otoritesinin zayıflaması isyanları artırmıştır.
• Tımar sisteminin bozulması ve iltizam sisteminin yaygınlaşması Celali İsyanlarında etkili olmuştur.
• Enflasyonun artması ve alım gücünün düşmesi isyanların nedenlerindendir.
• Uzun savaşlar ve yöneticilerin halka kötü davranması isyanları artırmıştır.
• Haçova Meydan Muharebesi'nden kaçan bazı askerlerin eşkıya olması Celali İsyanlarının nedenlerinden biridir.
"""
        };

        var notCelaliSonuclari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Celali İsyanlarının Sonuçları",
            Body = """
• Celali İsyanları sonucunda tarımsal üretim düşmüş ve vergi gelirleri azalmıştır.
• Köyden kente göç artmıştır.
• 1603-1610 yılları arasında Anadolu'dan büyük şehirlere gerçekleşen yoğun göç hareketine Büyük Kaçgun denir.
• Boşalan köylere eşkıyalar yerleşmiştir.
• Anadolu'da can ve mal güvenliği kalmamıştır.
• Şehirlerde işsizlik ve suç oranları artmıştır.
"""
        };

        var notTimarBozulmaNedenleri = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Tımar Sisteminin Bozulma Nedenleri",
            Body = """
• Tımarların sipahiler dışındaki kişilere verilmesi sistemin bozulmasına neden olmuştur.
• Tımarların özel mülke veya vakfa dönüştürülmesi sistemin bozulmasına yol açmıştır.
• Tımarların rüşvet karşılığında verilmesi sistemin bozulma nedenlerinden biridir.
• Dirliklerin para ile alınıp satılması tımar sistemini olumsuz etkilemiştir.
• Sipahilerin gösterişli yaşama isteği sistemin bozulmasına neden olmuştur.
• Nüfus artışı ve enflasyon tımar sistemini olumsuz etkilemiştir.
• Osmanlı Devleti Avrupa'nın silah teknolojisine uyum sağlayamamıştır.
• Uzun süren savaşlar tımar topraklarının zarar görmesine neden olmuştur.
"""
        };

        var notEyaletIsyanlari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Eyalet İsyanları",
            Body = """
• Eyalet isyanlarının nedenlerinden biri merkezi otoritenin zayıflamasıdır.
• Eyaletlerde devlet otoritesinin zayıflaması isyanları artırmıştır.
• Devlet yöneticilerinin halka kötü davranması eyalet isyanlarının nedenleri arasındadır.
• Eyalet isyanları 1789 Fransız İhtilali'nden önce gerçekleştiği için Fransız İhtilali'nin yaydığı ulusçuluk akımıyla ilgili değildir.
• Genç Osman'ın öldürülmesi üzerine Abaza Mehmet Paşa isyan etmiştir.
• Abaza Mehmet Paşa'ya Erzurum Valiliği verilerek isyan bastırılmıştır.
"""
        };

        var notSuhteIsyanlari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — Suhte İsyanları",
            Body = """
• Osmanlı Devleti'nde medrese öğrencilerine Suhte veya Softa denilmiştir.
• Ulema çocuklarının kayrılması Suhte İsyanlarının nedenlerinden biridir.
• Rüşvet ve iltimasın yaygınlaşması Suhte İsyanlarını artırmıştır.
• Medreselere kapasitenin üzerinde öğrenci alınması isyanların nedenlerinden biridir.
• Nüfus artışı ve enflasyon Suhte İsyanlarında etkili olmuştur.
• Medrese gelirlerinin azalması ve öğrencilerin halktan yardım istemesi isyanlara neden olmuştur.
• İsyancı medrese öğrencilerine karşı güç kullanılması çok sayıda can kaybına yol açmıştır.
• Suhte İsyanları sonucunda eğitimli insan sayısı azalmıştır.
"""
        };

        var notOnYedinciYuzyilIslahatlari = new Note
        {
            Title = "Osmanlı Devleti Duraklama Dönemi — XVII. Yüzyıl Islahatlarının Genel Özellikleri",
            Body = """
• XVII. yüzyıl ıslahatlarında Avrupa örnek alınmamıştır.
• Islahatlarda sorunların köküne inilememiştir.
• Islahatlar kişilere bağlı kalmıştır.
• Halkın desteği alınmadan ıslahat yapılmaya çalışılmıştır.
• Saray, ulema ve asker kendi çıkarları zedelendiği için ıslahatlara karşı çıkmıştır.
• Islahatlarda Fatih Sultan Mehmet ve Kanuni Sultan Süleyman dönemleri örnek alınmıştır.
• Kanun-ı Kadim anlayışıyla eski düzene dönülmek istenmiştir.
• Islahatlar baskı ve şiddet yoluyla benimsetilmeye çalışılmıştır.
"""
        };
        var not31MartHareketOrdusu = new Note
        {
            Title = "19. Yüzyıl Islahatları — 31 Mart İsyanı ve Atatürk",
            Body = """
• **Mustafa Kemal Atatürk, 31 Mart İsyanı'nı bastırmak için İstanbul'a gelen Hareket Ordusu'nda Kolağası (Önyüzbaşı) rütbesiyle görev yapmıştır.**
"""
        };
        return new Topic
        {
            Name = "19.YY Osmanlı",
            Description = "...",
            Notes = { notDengePolitikasi, notOsmanlicilik,notDuraklamaNedenleri, notKafesUsulu, notTimarSistemi, notYeniceriOcagi, notGiritSeferi, notOsmanliRusya, notOsmanliIran, notHotinSeferi, notBucasAntlasmasi, notHacovaSavasi, notZitvatorokAntlasmasi, notCelaliNedenleri, notCelaliSonuclari, notTimarBozulmaNedenleri, notEyaletIsyanlari, notSuhteIsyanlari, notOnYedinciYuzyilIslahatlari, notSirpIsyanlari, notYunanIsyani, notKavalaliMehmetAliPasa, notKutahyaAntlasmasi, notHunkarIskelesi, notBaltaLimani, notLondraBogazlar, notMilliyetcilikAzınliklar, notMisirSorunuLondra
},
            Questions = {new Question
{
    Note = notDengePolitikasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "XIX. yüzyılda Osmanlı Devleti'nin dış politikasında tek başına büyük devletlerle başa çıkamayacağını anlayıp Avrupalı devletlerin çıkar çatışmalarından yararlanarak varlığını sürdürmeye çalıştığı temel politika aşağıdakilerden hangisidir?",
    Explanation = "Osmanlı Devleti, büyük devletlerin çıkar çatışmalarından yararlanarak siyasi varlığını korumaya çalışmıştır. Bu anlayış Denge Politikası olarak adlandırılır.",
    OrderIndex = 1,
    Choices =
    {
        new Choice { Text = "Denge Politikası", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İslamcılık politikası", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Osmanlıcılık politikası", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Turancılık politikası", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Panslavizm politikası", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notOsmanlicilik,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "XIX. yüzyılda Osmanlı Devleti'nin dağılmasını önlemek amacıyla ortaya atılan, dil, din ve ırk farkı gözetmeksizin herkesi Osmanlı vatandaşı sayarak bir millet oluşturmayı amaçlayan fikir akımı aşağıdakilerden hangisidir?",
    Explanation = "Osmanlıcılık fikri, Osmanlı Devleti'ndeki farklı toplulukları ortak Osmanlı vatandaşlığı altında birleştirmeyi amaçlamıştır.",
    OrderIndex = 2,
    Choices =
    {
        new Choice { Text = "Osmanlıcılık", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İslamcılık", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Türkçülük", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Batıcılık", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Adem-i merkeziyetçilik", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notSirpIsyanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "XIX. yüzyılda Osmanlı Devleti'ne karşı isyan eden ilk azınlık aşağıdakilerden hangisidir?",
    Explanation = "XIX. yüzyılda Osmanlı Devleti'ne karşı ilk isyan eden azınlık Sırplardır.",
    OrderIndex = 3,
    Choices =
    {
        new Choice { Text = "Sırplar", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Yunanlılar", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Bulgarlar", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Romenler", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Arnavutlar", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notSirpIsyanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sırpların Osmanlı Devleti'ne karşı başlattığı isyan sürecinde ilk kez imtiyaz elde etmelerini sağlayan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1812 Bükreş Antlaşması ile Sırplar ilk kez imtiyaz elde etmiştir.",
    OrderIndex = 4,
    Choices =
    {
        new Choice { Text = "Bükreş Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Edirne Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Berlin Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Yaş Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notSirpIsyanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sırpların özerklik kazanmasını sağlayan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1829 Edirne Antlaşması ile Sırplar özerklik kazanmıştır.",
    OrderIndex = 5,
    Choices =
    {
        new Choice { Text = "Edirne Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Bükreş Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "İstanbul Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Ziştovi Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Paris Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notSirpIsyanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sırpların tam bağımsızlıklarını elde ettikleri uluslararası antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1878 Berlin Antlaşması ile Sırbistan tam bağımsızlığını elde etmiştir.",
    OrderIndex = 6,
    Choices =
    {
        new Choice { Text = "Berlin Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Ayastefanos Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Londra Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Hünkâr İskelesi Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Balta Limanı Ticaret Sözleşmesi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notSirpIsyanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sırp isyanlarının başlamasında ve Sırpların desteklenmesinde en çok rol oynayan, sıcak denizlere inme politikası güden devlet aşağıdakilerden hangisidir?",
    Explanation = "Rusya, Balkanlarda etkisini artırmak ve sıcak denizlere ulaşmak amacıyla Sırpları desteklemiştir.",
    OrderIndex = 7,
    Choices =
    {
        new Choice { Text = "Rusya", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "İngiltere", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Fransa", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Avusturya", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "İtalya", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notYunanIsyani,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nden ayrılarak bağımsızlığını kazanan ilk azınlık aşağıdakilerden hangisidir?",
    Explanation = "Osmanlı Devleti'nden ayrılarak bağımsızlığını kazanan ilk azınlık Yunanlılardır.",
    OrderIndex = 8,
    Choices =
    {
        new Choice { Text = "Yunanlılar", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Sırbistan", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Romanya", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Karadağ", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Bulgaristan", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notYunanIsyani,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Yunanistan'ın bağımsızlığını kazandığı, Osmanlı Devleti ile Rusya arasında 1829 yılında imzalanan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1829 Edirne Antlaşması ile Yunanistan'ın bağımsızlığı kabul edilmiştir.",
    OrderIndex = 9,
    Choices =
    {
        new Choice { Text = "Edirne Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Hünkâr İskelesi Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Kütahya Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Paris Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Londra Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notYunanIsyani,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Yunan İsyanı sırasında Osmanlı Devleti'nin zor durumda kalması üzerine yardım istenen ve isyanın bastırılması karşılığında Girit ve Mora valiliklerini talep eden Mısır valisi kimdir?",
    Explanation = "Yunan İsyanı sırasında Osmanlı Devleti, Kavalalı Mehmet Ali Paşa'dan yardım istemiştir.",
    OrderIndex = 10,
    Choices =
    {
        new Choice { Text = "Kavalalı Mehmet Ali Paşa", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Alemdar Mustafa Paşa", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Sait Halim Paşa", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Mithat Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Cezzar Ahmet Paşa", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notYunanIsyani,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı ve Mısır donanmasının İngiltere, Fransa ve Rusya'nın müttefik donanması tarafından yakıldığı olay aşağıdakilerden hangisidir?",
    Explanation = "1827 Navarin Olayı'nda İngiltere, Fransa ve Rusya'nın müttefik donanmaları Osmanlı ve Mısır donanmalarını yakmıştır.",
    OrderIndex = 11,
    Choices =
    {
        new Choice { Text = "Navarin Olayı", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Sinop Baskını", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Çeşme Baskını", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "İnebahtı Deniz Savaşı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Episkopi Olayı", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notKutahyaAntlasmasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti ile Mısır Valisi Kavalalı Mehmet Ali Paşa'nın ordularının birbirine kesin üstünlük kuramaması üzerine dış güçlerin araya girmesiyle imzalanan ve Kavalalı'ya Mısır ve Cidde valiliklerinin yanı sıra Şam ve Adana mukataalarının da verildiği iç mesele antlaşması aşağıdakilerden hangisidir?",
    Explanation = "1833 Kütahya Antlaşması ile Kavalalı Mehmet Ali Paşa'ya Mısır, Cidde, Şam ve Adana ile ilgili önemli yönetim hakları verilmiştir.",
    OrderIndex = 12,
    Choices =
    {
        new Choice { Text = "Kütahya Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Hünkâr İskelesi Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Balta Limanı Ticaret Sözleşmesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Londra Boğazlar Sözleşmesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Paris Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notHunkarIskelesi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kavalalı Mehmet Ali Paşa'nın yeniden Osmanlı Devleti'ni zor durumda bırakması üzerine Osmanlı Devleti'nin yardım bulamaması sebebiyle geleneksel rakibi Rusya ile imzaladığı gizli antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1833 Hünkâr İskelesi Antlaşması, Osmanlı Devleti ile Rusya arasında imzalanmıştır.",
    OrderIndex = 13,
    Choices =
    {
        new Choice { Text = "Hünkâr İskelesi Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Londra Boğazlar Sözleşmesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Balta Limanı Ticaret Sözleşmesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Frankfurt Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Zevte Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notHunkarIskelesi,
    Type = QuestionType.MultipleChoice,
    IsNegative = true,
    Text = "Hünkâr İskelesi Antlaşması ile ilgili aşağıda verilen bilgilerden hangisi yanlıştır?",
    Explanation = "Boğazlar Sorunu uluslararası bir statüye Hünkâr İskelesi Antlaşması ile değil, 1841 Londra Boğazlar Sözleşmesi ile kavuşmuştur.",
    OrderIndex = 14,
    Choices =
    {
        new Choice { Text = "Boğazlar sorunu bu antlaşma ile uluslararası bir statü kazanmıştır.", IsCorrect = false, OrderIndex = 1 },

        new Choice { Text = "Osmanlı Devleti ile Rusya arasında imzalanmıştır.", IsCorrect = true, OrderIndex = 2 },
        new Choice { Text = "Süresi 8 yıl olarak belirlenmiştir.", IsCorrect = true, OrderIndex = 3 },
        new Choice { Text = "Bu antlaşma ile Rusya, Boğazlar üzerinde önemli bir imtiyaz elde etmiştir.", IsCorrect = true, OrderIndex = 4 },
        new Choice { Text = "İngiltere ve Fransa bu antlaşmadan oldukça rahatsız olmuştur.", IsCorrect = true, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBaltaLimani,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nin İngiltere'ye ticari ve gümrük kolaylıkları tanıdığı, Osmanlı iç pazarının yabancı tüccarların eline geçmesine zemin hazırlayan 1838 tarihli ekonomik belge aşağıdakilerden hangisidir?",
    Explanation = "1838 Balta Limanı Ticaret Sözleşmesi ile İngiliz tüccarlara önemli ticari kolaylıklar tanınmıştır.",
    OrderIndex = 15,
    Choices =
    {
        new Choice { Text = "Balta Limanı Ticaret Sözleşmesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Londra Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Paris Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Hünkâr İskelesi Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kütahya Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notLondraBogazlar,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Hünkâr İskelesi Antlaşması'nın süresinin dolması üzerine İngiltere, Fransa, Rusya, Avusturya ve Prusya'nın katılımıyla Boğazların bütün yabancı savaş gemilerine kapatıldığı ve uluslararası bir statü kazandığı sözleşme aşağıdakilerden hangisidir?",
    Explanation = "1841 Londra Boğazlar Sözleşmesi ile Boğazlar yabancı savaş gemilerine kapatılmış ve uluslararası bir statü kazanmıştır.",
    OrderIndex = 16,
    Choices =
    {
        new Choice { Text = "Londra Boğazlar Sözleşmesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Paris Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Berlin Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Viyana Kongresi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "San Stefano Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notMilliyetcilikAzınliklar,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "XIX. yüzyılda Osmanlı Devleti'nde milliyetçilik akımının etkisiyle ayaklanan ve bağımsızlık kazanan ilk milletler sırasıyla hangi seçenekte doğru olarak verilmiştir?",
    Explanation = "XIX. yüzyılda Osmanlı Devleti'ne karşı ilk isyan eden azınlık Sırplar, bağımsızlığını kazanan ilk azınlık ise Yunanlılardır.",
    OrderIndex = 17,
    Choices =
    {
        new Choice { Text = "Sırplar - Yunanlılar", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Yunanlılar - Bulgarlar", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Bulgarlar - Sırplar", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Romenler - Sırplar", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Karadağlılar - Yunanlılar", IsCorrect = false, OrderIndex = 5 }
    }
},


new Question
{
    Note = notKavalaliMehmetAliPasa,
    Type = QuestionType.MultipleChoice,
    IsNegative = true,
    Text = "Kavalalı Mehmet Ali Paşa'nın Osmanlı Devleti'ne karşı isyan etmesinde etkili olan temel faktörler arasında aşağıdakilerden hangisi yer almaz?",
    Explanation = "İngiltere'nin Kavalalı Mehmet Ali Paşa'yı Osmanlı tahtına geçirmek istemesi, Kavalalı'nın isyan nedenleri arasında yer almaz.",
    OrderIndex = 19,
    Choices =
    {
        new Choice { Text = "İngiltere'nin Kavalalı Mehmet Ali Paşa'yı Osmanlı tahtına geçirmek istemesi", IsCorrect = false, OrderIndex = 1 },

        new Choice { Text = "Mora ve Girit isyanlarında vaat edilen valiliklerin verilmemesi", IsCorrect = true, OrderIndex = 2 },
        new Choice { Text = "Kavalalı'nın daha fazla toprak ve güç istemesi", IsCorrect = true, OrderIndex = 3 },
        new Choice { Text = "Osmanlı merkezi otoritesinin zayıflamış olması", IsCorrect = true, OrderIndex = 4 },
        new Choice { Text = "Osmanlı Devleti ile Kavalalı arasında yönetim ve yetki konusunda anlaşmazlık yaşanması", IsCorrect = true, OrderIndex = 5 }
    }
},

new Question
{
    Note = notMisirSorunuLondra,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nin XIX. yüzyılda Mısır Sorunu'nu uluslararası bir platformda çözmek ve büyük devletlerin desteğini almak için düzenlenen 1840 Londra Konferansı'nın temel sonucu aşağıdakilerden hangisidir?",
    Explanation = "1840 Londra düzenlemeleriyle Mısır, Osmanlı Devleti'ne bağlı olmakla birlikte iç işlerinde geniş yetkilere sahip bir yönetim haline gelmiştir.",
    OrderIndex = 20,
    Choices =
    {
        new Choice { Text = "Mısır'ın iç işlerinde serbest, dış işlerinde Osmanlı Devleti'ne bağlı bir yönetim haline gelmesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Mısır'ın tamamen Osmanlı Devleti'nden kopması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Kavalalı'nın halife ilan edilmesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Boğazların tamamen Rus kontrolüne girmesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "İngiltere'nin Mısır'ı doğrudan işgal etmesi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notDengePolitikasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "XIX. yüzyılda Osmanlı Devleti'nin Avrupa devletleri karşısında izlediği Denge Politikası'nın temel mantığı aşağıdakilerden hangisidir?",
    Explanation = "Denge Politikası, Avrupa'nın büyük devletleri arasındaki çıkar çatışmalarından yararlanarak Osmanlı Devleti'nin varlığını sürdürmesini amaçlamıştır.",
    OrderIndex = 21,
    Choices =
    {
        new Choice { Text = "Büyük devletlerin Osmanlı toprakları üzerindeki çıkar çatışmalarından faydalanarak varlığı sürdürmek", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Osmanlı ordusunu tamamen Avrupalı subaylara teslim etmek", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tüm azınlıklara sınırsız özerklik vererek devleti federasyona dönüştürmek", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sadece ekonomik imtiyazlar dağıtarak barışı korumak", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Avrupalı devletlerle tek bir çatı altında birleşmek", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notSirpIsyanlari,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Sırp isyanları sonucunda Sırpların elde ettiği hakların kronolojik aşamaları hangi seçenekte doğru olarak sıralanmıştır?",
    Explanation = "Sırplar sırasıyla imtiyaz, özerklik ve bağımsızlık elde etmiştir.",
    OrderIndex = 22,
    Choices =
    {
        new Choice { Text = "İmtiyaz - Özerklik - Bağımsızlık", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Bağımsızlık - Özerklik - İmtiyaz", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Özerklik - Bağımsızlık - İmtiyaz", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "İmtiyaz - Bağımsızlık - Özerklik", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Özerklik - İmtiyaz - Bağımsızlık", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notHunkarIskelesi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nin 1833 Kütahya Antlaşması'ndan sonra Rusya ile Hünkâr İskelesi Antlaşması'nı imzalamasının ardından Avrupalı devletlerin en çok endişe duyduğu husus aşağıdakilerden hangisidir?",
    Explanation = "Avrupalı devletler, Rusya'nın Boğazlar üzerinde güç kazanarak Akdeniz'e inme ve stratejik üstünlük elde etmesinden endişe etmiştir.",
    OrderIndex = 23,
    Choices =
    {
        new Choice { Text = "Rusya'nın Boğazlar üzerinden Akdeniz'e inme ve stratejik üstünlük elde etme ihtimali", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Osmanlı Devleti'nin donanmasını güçlendirmesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Mısır'ın tamamen Fransa'ya bırakılması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "İngiltere'nin ticaret yollarının tamamen kapanması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Osmanlı Devleti'nin rejimini değiştirmesi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notKavalaliMehmetAliPasa,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "XIX. yüzyılda Osmanlı Devleti'nin iç işleriyken dış sorun haline gelen ve uluslararası bir boyuta ulaşan ilk büyük kriz aşağıdakilerden hangisidir?",
    Explanation = "Kavalalı Mehmet Ali Paşa'nın isyanıyla başlayan Mısır Sorunu, büyük Avrupa devletlerinin müdahalesiyle Osmanlı Devleti'nin iç meselesi olmaktan çıkmıştır.",
    OrderIndex = 24,
    Choices =
    {
        new Choice { Text = "Mısır Sorunu", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Boğazlar Sorunu", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Ermeni Meselesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kırım Savaşı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "93 Harbi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBaltaLimani,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1838 Balta Limanı Ticaret Sözleşmesi ile Osmanlı iç pazarında yabancı tüccarların yerli tüccarlara göre avantajlı konuma gelmesine yol açan temel düzenleme aşağıdakilerden hangisidir?",
    Explanation = "Balta Limanı Ticaret Sözleşmesi ile yabancı tüccarların Osmanlı iç ticaretindeki faaliyetleri kolaylaşmış ve ekonomik avantajları artmıştır.",
    OrderIndex = 25,
    Choices =
    {
        new Choice { Text = "Yabancı tüccarlara iç ticarette geniş serbestlik ve avantaj sağlayan düzenlemelerin yapılması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Yabancı tüccarlardan alınan gümrük vergilerinin tamamen kaldırılması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yerli tüccarlardan alınan iç gümrük vergilerinin artırılmasına karşın yabancıların iç ticaretten men edilmesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Osmanlı limanlarının sadece İngiliz gemilerine açılması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Osmanlı parasının değerinin sabitlenmesi", IsCorrect = false, OrderIndex = 5 }
    }
},
new Question
{
    Note = notDuraklamaNedenleri,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nin duraklama dönemine girmesinde etkili olan nedenlerden biri aşağıdakilerden hangisidir?",
    Explanation = "Merkezi otoritenin bozulması Osmanlı Devleti'nin duraklama nedenlerinden biridir.",
    OrderIndex = 26,
    Choices =
    {
        new Choice { Text = "Merkezi otoritenin bozulması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Merkezi otoritenin sürekli güçlenmesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tımar sisteminin daha da yaygınlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Savaşların sürekli kısa sürmesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ganimet gelirlerinin sürekli artması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notKafesUsulu,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Şehzadelerin sarayda Şimşirlik adı verilen bölümlerde yetiştirilmesi uygulaması halk arasında hangi adla bilinmektedir?",
    Explanation = "Şehzadelerin Şimşirlikte yetiştirilmesi uygulaması halk arasında kafes usulü olarak adlandırılmıştır.",
    OrderIndex = 27,
    Choices =
    {
        new Choice { Text = "Kafes usulü", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Ekber ve Erşed sistemi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Devşirme sistemi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Tımar sistemi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "İltizam sistemi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notKafesUsulu,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Kafes usulünün Osmanlı Devleti'nin yönetim yapısında yol açtığı temel sonuç aşağıdakilerden hangisidir?",
    Explanation = "Kafes usulü, şehzadelerin devlet yönetimi deneyimi kazanmasını engellediği için deneyimsiz padişahların tahta çıkmasına neden olmuştur.",
    OrderIndex = 28,
    Choices =
    {
        new Choice { Text = "Deneyimsiz padişahların tahta çıkması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Şehzadelerin daha fazla askerî deneyim kazanması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Sancak sisteminin güçlenmesi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Merkezi otoritenin kesin olarak güçlenmesi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Taht mücadelelerinin tamamen sona ermesi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notTimarSistemi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Aşağıdakilerden hangisi tımar sisteminin Osmanlı Devleti'ne sağladığı yararlardan biridir?",
    Explanation = "Tımar sistemi sayesinde devlet hazinesinden doğrudan para harcamadan asker yetiştirilmiştir.",
    OrderIndex = 29,
    Choices =
    {
        new Choice { Text = "Hazineden para harcamadan asker yetiştirilmesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Yeniçeri sayısının sınırsız biçimde artırılması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Toprakların tamamen boş bırakılması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Vergi toplama sisteminin kaldırılması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Devlet memurlarının maaşlarının tamamen kaldırılması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notYeniceriOcagi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Yeniçeri Ocağındaki bozulmanın göstergelerinden biri olarak askerlerin devlet için asker anlayışını terk ederek benimsediği anlayış aşağıdakilerden hangisidir?",
    Explanation = "Yeniçerilerin devlet için asker anlayışını terk ederek devlet asker içindir anlayışını benimsemesi ocağın bozulmasının önemli nedenlerinden biridir.",
    OrderIndex = 30,
    Choices =
    {
        new Choice { Text = "Devlet asker içindir anlayışı", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Asker devlet içindir anlayışı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Toprak köylü içindir anlayışı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Devlet tımar içindir anlayışı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Saray halk içindir anlayışı", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notGiritSeferi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1645 yılında kuşatılmaya başlanıp 24 yıl süren bir mücadelenin ardından 1669 yılında Osmanlı Devleti tarafından fethedilen ada aşağıdakilerden hangisidir?",
    Explanation = "Girit 1645 yılında kuşatılmış ve 24 yıl süren mücadelenin ardından 1669 yılında fethedilmiştir.",
    OrderIndex = 31,
    Choices =
    {
        new Choice { Text = "Girit", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kıbrıs", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Rodos", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Sakız", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Midilli", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notOsmanliRusya,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti ile Rusya arasında imzalanan ilk antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1681 Bahçesaray Antlaşması Osmanlı Devleti ile Rusya arasında imzalanan ilk antlaşmadır.",
    OrderIndex = 32,
    Choices =
    {
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Küçük Kaynarca Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notOsmanliRusya,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Bahçesaray Antlaşması'nın diğer adı aşağıdakilerden hangisidir?",
    Explanation = "1681 yılında imzalanan Bahçesaray Antlaşması Çehrin Antlaşması olarak da adlandırılır.",
    OrderIndex = 33,
    Choices =
    {
        new Choice { Text = "Çehrin Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Serav Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Nasuh Paşa Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ferhat Paşa Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notOsmanliIran,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nin doğuda en geniş sınırlara ulaşmasını sağlayan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1590 Ferhat Paşa Antlaşması ile Osmanlı Devleti doğuda en geniş sınırlara ulaşmıştır.",
    OrderIndex = 34,
    Choices =
    {
        new Choice { Text = "Ferhat Paşa Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notOsmanliIran,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "IV. Murat'ın İran üzerine iki kez sefer düzenlemesinin ardından 1639 yılında imzalanan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "IV. Murat'ın İran seferlerinin ardından Osmanlı Devleti ile İran arasında 1639 Kasr-ı Şirin Antlaşması imzalanmıştır.",
    OrderIndex = 35,
    Choices =
    {
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Ferhat Paşa Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notOsmanliIran,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Günümüzde Türkiye'nin en eski sınırını büyük ölçüde belirleyen antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1639 Kasr-ı Şirin Antlaşması günümüzde Türkiye'nin en eski sınırını büyük ölçüde belirlemiştir.",
    OrderIndex = 36,
    Choices =
    {
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notHotinSeferi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Yeniçerilerin disiplinsiz ve isteksiz davranışlarının belirginleşmesi üzerine II. Osman'ın Yeniçeri Ocağını kaldırmayı düşünmesine neden olan sefer aşağıdakilerden hangisidir?",
    Explanation = "Hotin Seferi sırasında yeniçerilerin disiplinsiz ve isteksiz davranışları II. Osman'ın Yeniçeri Ocağını kaldırmayı düşünmesine neden olmuştur.",
    OrderIndex = 37,
    Choices =
    {
        new Choice { Text = "Hotin Seferi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Zigetvar Seferi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Mohaç Seferi", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kıbrıs Seferi", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Irak Seferi", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notBucasAntlasmasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı Devleti'nin batıda en geniş sınırlara ulaşmasını sağlayan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1672 Bucaş Antlaşması ile Osmanlı Devleti batıda en geniş sınırlara ulaşmıştır.",
    OrderIndex = 38,
    Choices =
    {
        new Choice { Text = "Bucaş Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Karlofça Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notHacovaSavasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "1596 yılında gerçekleşen, geri hizmette bulunanların kepçe ve kazan gibi araçlarla savaşa katılması nedeniyle Kepçe Kazan Savaşı olarak da adlandırılan savaş aşağıdakilerden hangisidir?",
    Explanation = "Haçova Meydan Muharebesi sırasında geri hizmette bulunanların mücadeleye katılması nedeniyle savaş Kepçe Kazan Savaşı olarak da adlandırılmıştır.",
    OrderIndex = 39,
    Choices =
    {
        new Choice { Text = "Haçova Meydan Muharebesi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Mohaç Meydan Muharebesi", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Kösedağ Savaşı", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Miryokefalon Savaşı", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Otlukbeli Savaşı", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notHacovaSavasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Haçova Meydan Muharebesi'nde Osmanlı ordusunun yeniden toparlanarak savaşı kazanmasında önemli rol oynayan kişi aşağıdakilerden hangisidir?",
    Explanation = "Hoca Sadeddin Efendi'nin gayreti ve geri hizmette bulunanların savaşa katılması Haçova'da Osmanlı ordusunun yeniden toparlanmasını sağlamıştır.",
    OrderIndex = 40,
    Choices =
    {
        new Choice { Text = "Hoca Sadeddin Efendi", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Kuyucu Murat Paşa", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tarhuncu Ahmet Paşa", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Köprülü Mehmet Paşa", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Merzifonlu Kara Mustafa Paşa", IsCorrect = false, OrderIndex = 5 }
    }
},

new Question
{
    Note = notZitvatorokAntlasmasi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Osmanlı padişahı ile Avusturya hükümdarının siyasi bakımdan eşit kabul edilmesine ve Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğünün sona ermesine neden olan antlaşma aşağıdakilerden hangisidir?",
    Explanation = "1606 Zitvatorok Antlaşması ile Osmanlı padişahı ile Avusturya hükümdarı siyasi bakımdan eşit kabul edilmiş ve Osmanlı Devleti'nin Orta Avrupa'daki siyasi üstünlüğü sona ermiştir.",
    OrderIndex = 41,
    Choices =
    {
        new Choice { Text = "Zitvatorok Antlaşması", IsCorrect = true, OrderIndex = 1 },

        new Choice { Text = "Bucaş Antlaşması", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Kasr-ı Şirin Antlaşması", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Bahçesaray Antlaşması", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Ferhat Paşa Antlaşması", IsCorrect = false, OrderIndex = 5 }
    }
},

            },
        };
    }
    private static Topic BuildOnDokuzuncuYuzyilIslahatlari()
    {
        var notSenedIttifak = new Note
        {
            Title = "19. Yüzyıl Islahatları — Sened-i İttifak",
            Body = """
• **II. Mahmut döneminde**, Alemdar Mustafa Paşa'nın etkisiyle imzalanmıştır.
• Osmanlı Devleti'nde **ilk defa padişah yetkilerinin sınırlandırıldığı belge** olarak kabul edilir.
• **İlk demokratikleşme hareketi** sayılır ve Magna Carta'ya benzetilir.
• Ayanların varlığını resmî olarak tanımıştır.
"""
        };
        var notMahmutYonetim = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Mahmut Yönetim Islahatları",
            Body = """
• **Divan teşkilatı kaldırılmış**, yerine nazırlıklar kurulmuştur.
• **Memurlara maaş bağlanmış**, memurlar dahiliye ve hariciye olarak ayrılmıştır.
• Memurların yargı ve terfi işlemleri için **Meclis-i Vâlâ-yı Ahkâm-ı Adliye** kurulmuştur.
• İlk resmî gazete **Takvim-i Vekayi** çıkarılmıştır.
• Islahatları düzenlemek amacıyla **Dâr-ı Şûrâ-yı Bâbıâli** kurulmuştur.
"""
        };
        var notMahmutAskeri = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Mahmut Askerî Islahatları",
            Body = """
• Nizam-ı Cedid'in yerine **Sekban-ı Cedid** kurulmuştur.
• Ardından **Eşkinci Ocağı** kurulmuştur.
• **1826'da Yeniçeri Ocağı kaldırılmıştır.**
• Yeniçeri Ocağı'nın kaldırılmasına **Vak'a-i Hayriye** denir.
• Yerine **Asâkir-i Mansûre-i Muhammediye** kurulmuştur.
• **Mekteb-i Harbiye ve Mekteb-i Tıbbiye** açılmıştır.
• Köy ve kasabaların güvenliği için **Redif birlikleri** kurulmuştur.
• Seraskerlik, günümüzdeki **Genelkurmay Başkanlığı**na karşılık gelir.
"""
        };
        var notMahmutNufus = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Mahmut Nüfus ve Güvenlik",
            Body = """
• Askerlik çağına gelenleri belirlemek ve vergi almak amacıyla **ilk kez nüfus sayımı** yapılmıştır.
• Bu sayımda **kadınlar sayılmamıştır**.
• Erkekler askere uygun olanları belirlemek, mal ve hayvanlar ise vergi amacıyla sayılmıştır.
• Askerlik şubeleri niteliğinde **Dâr-ı Şûrâ-yı Askerî** kurulmuştur.
"""
        };
        var notMahmutEgitim = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Mahmut Eğitim ve Sağlık",
            Body = """
• **İlköğretim zorunlu** hâle getirilmiştir.
• Avrupa'ya **eğitim amacıyla öğrenci gönderilmiştir**.
• Osmanlı Devleti'nde **karantina uygulaması** başlatılmıştır.
• Mehterhane kaldırılmış, yerine **Mızıka-yı Hümayun** kurulmuştur.
"""
        };
        var notTanzimat = new Note
        {
            Title = "19. Yüzyıl Islahatları — Tanzimat Fermanı",
            Body = """
• **1839'da**, Sultan Abdülmecid döneminde ilan edilmiştir.
• Gülhane Parkı'nda okunduğu için **Gülhane Hatt-ı Hümayunu** adıyla da bilinir.
• Fermanın hazırlanmasında **Mustafa Reşit Paşa** etkili olmuştur.
• Can, mal ve namus güvenliği ile adaletli vergi ve askerlik konularında düzenlemeler getirmiştir.
• Temel amaçlardan biri Osmanlı Devleti'nin dağılmasını önlemek ve ulusçuluğun etkisini azaltmaktır.
"""
        };
        var notIslahatFermani = new Note
        {
            Title = "19. Yüzyıl Islahatları — Islahat Fermanı",
            Body = """
• **1856'da**, Sultan Abdülmecid döneminde ilan edilmiştir.
• Tanzimat Fermanı ile birlikte Osmanlı Devleti'nin dağılmasını önleme ve ulusçuluğun etkisini azaltma amacı taşır.
• Özellikle **gayrimüslimlere verilen hakları genişletmiştir**.
• Tanzimat Fermanı'ndan farklı olarak **Müslüman ve gayrimüslim ayrımıyla ilgili düzenlemelere daha geniş yer vermiştir.
"""
        };
        var notAbdulaziz = new Note
        {
            Title = "19. Yüzyıl Islahatları — Sultan Abdülaziz Dönemi",
            Body = """
• **1863'te Memleket Sandıkları** kurulmuştur.
• Memleket Sandıkları, çiftçiye kredi sağlama amacı taşımış ve Ziraat Bankasının temelini oluşturmuştur.
• Bu dönemde eğitim, ulaşım ve yönetim alanlarında yenilikler sürdürülmüştür.
"""
        };
        var notBirinciMesrutiyet = new Note
        {
            Title = "19. Yüzyıl Islahatları — I. Meşrutiyet ve Kanun-i Esasi",
            Body = """
• **1876'da**, II. Abdülhamid döneminde I. Meşrutiyet ilan edilmiştir.
• **Kanun-i Esasi**, Osmanlı Devleti ve Türk tarihinin ilk yazılı anayasasıdır.
• Yasama yetkisi **Ayan ve Mebusan Meclislerine** verilmiştir.
• Ayan Meclisi üyelerini padişah atar, Mebusan Meclisi üyelerini halk seçer.
• Hükümet yaptığı işlerden **padişaha karşı sorumludur**.
• Padişahın meclisi kapatma ve kanunları sınırsız veto etme yetkisi bulunmuştur.
"""
        };
        var notCirağan = new Note
        {
            Title = "19. Yüzyıl Islahatları — Çırağan Vakası",
            Body = """
• **1878'de Ali Suavi önderliğinde** II. Abdülhamid tahttan indirilmek istenmiştir.
• Yerine **V. Murat'ın** geçirilmesi amaçlanmıştır.
• **Beşiktaş Muhafızı Hasan Paşa**, Ali Suavi'yi öldürerek girişimi engellemiştir.
• Bu olay **Çırağan Vakası** olarak bilinir.
"""
        };
        var notAbdulhamidEgitim = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Abdülhamid Eğitim Islahatları",
            Body = """
• II. Abdülhamid'e eğitime verdiği önem nedeniyle **Maarifperver** unvanı verilmiştir.
• Lisan, maliye, dişçi, baytar, mimari, hukuk, ziraat ve çeşitli meslek okulları açılmıştır.
• **Sanayi-i Nefise Mektebi**, günümüzde Güzel Sanatlar alanıyla ilişkilendirilen önemli kurumlardandır.
• Sanayi-i Nefise Mektebinin kurucusu **Osman Hamdi Bey**'dir.
"""
        };
        var notOsmanHamdi = new Note
        {
            Title = "19. Yüzyıl Islahatları — Osman Hamdi Bey ve Müzecilik",
            Body = """
• **Osman Hamdi Bey**, Osmanlı Devleti'nin ilk müzecisi ve ilk arkeoloğu olarak anlatılmıştır.
• **Asar-ı Atika** adıyla ilk müze açılmıştır.
• Osman Hamdi Bey, **Sanayi-i Nefise Mektebinin kurucusudur**.
"""
        };
        var notAbdulhamidUlasim = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Abdülhamid Ulaşım ve İstihbarat",
            Body = """
• II. Abdülhamid döneminde **büyük bir telgraf ağı** kurulmuş ve Telgraf Mektebi açılmıştır.
• Almanya ile yakınlaşılmış, demiryolu projelerinde Almanlar etkili olmuştur.
• **Berlin-Bağdat Demiryolu** projesi önem kazanmıştır.
• **Hicaz Demiryolu'nun son durağı Medine'dir**.
• Haber alma amacıyla **Hafiye ve Jurnal teşkilatları** kullanılmıştır.
• Yönetim merkezi **Yıldız Sarayı'na** taşınmıştır.
"""
        };
        var notAbdulhamidSosyal = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Abdülhamid Sosyal Kurumları",
            Body = """
• **Kız meslek liseleri** açılmıştır.
• Hamidiye Etfal, çocukların korunması ve sağlık hizmetleriyle ilişkilendirilmiştir.
• **1903'te Darülhayr-ı Ali**, Ermeni saldırıları sonucunda yetim kalan çocukları korumak ve yetiştirmek amacıyla açılmıştır.
"""
        };
        var notIkinciMesrutiyet = new Note
        {
            Title = "19. Yüzyıl Islahatları — II. Meşrutiyet",
            Body = """
• **1908'de**, II. Abdülhamid tarafından II. Meşrutiyet ilan edilmiştir.
• İttihat ve Terakki, II. Meşrutiyetin ilanı için baskı yapan önemli oluşumlardandır.
• Seçimler yeniden yapılmış, **Ayan ve Mebusan Meclisleri tekrar oluşturulmuştur**.
• II. Meşrutiyet bir süre **Hürriyet Bayramı** olarak kutlanmıştır.
"""
        };
        var notOtuzBirMart = new Note
        {
            Title = "19. Yüzyıl Islahatları — 31 Mart Vakası",
            Body = """
• **31 Mart Vakası**, meşrutiyet yönetimine karşı çıkan bir ayaklanmadır.
• Osmanlı tarihinde **rejimi değiştirmeye yönelik çıkan ilk ve tek isyan** olarak anlatılmıştır.
• Meşrutiyetten yeniden **monarşiye dönmek** amaçlanmıştır.
• İsyanı bastırmak için Selanik'ten İstanbul'a gelen orduya **Hareket Ordusu** adı verilmiştir.
• Hareket Ordusunun komutanı **Mahmut Şevket Paşa**dır.
• Mustafa Kemal, Hareket Ordusunda **kurmay yüzbaşı** olarak görev almış; ordunun planını çizmiş ve bildirgesini kaleme almıştır.
"""
        };
        var not1909Degisiklik = new Note
        {
            Title = "19. Yüzyıl Islahatları — 1909 Kanun-i Esasi Değişiklikleri",
            Body = """
• Hükümetin sorumluluğu **padişahtan meclise doğru kaydırılmıştır**.
• Kanun teklifi verme yetkisi **mebuslara da verilmiştir**.
• Padişahın kanunları sınırsız veto yetkisi sınırlandırılmıştır.
• Padişahın meclisi kapatma yetkisi sınırlandırılmıştır.
• **Sürgün ve angarya cezası kaldırılmıştır**.
• Siyasi parti ve dernek kurma hakkı tanınmıştır.
"""
        };
        var notMuharrem = new Note
        {
            Title = "19. Yüzyıl Islahatları — Muharrem Kararnamesi ve Düyun-u Umumiye",
            Body = """
• Osmanlı Devleti dış borçların faizlerini ödeyemeyince **1881'de Muharrem Kararnamesi** ilan edilmiştir.
• Alacaklı devletlerin borçları tahsil etmesi için **Düyun-u Umumiye İdaresi** kurulmuştur.
• Bu kurum, Osmanlı maliyesi üzerinde yabancı denetiminin artmasına neden olmuştur.
"""
        };
        var notFikirAkimlari = new Note
        {
            Title = "19. Yüzyıl Islahatları — Fikir Akımları",
            Body = """
• Osmanlıcılık; Tanzimat, Islahat, I. Meşrutiyet ve II. Meşrutiyet dönemlerinde devletin bütün unsurlarını bir arada tutma amacı taşımıştır.
• **İttihad-ı Anasır**, unsurların meclis çatısı altında temsil edilmesi anlayışıyla ilişkilendirilmiştir.
• II. Meşrutiyet döneminde **Türkçülük** fikir akımı güç kazanmıştır.
• **İslamcılık**, II. Abdülhamid döneminde resmî dış politika hâline getirilmiştir.
• Balkan uluslarının Osmanlı'dan ayrılması Osmanlıcılığın sona ermesine ve İslamcılığa ilk büyük darbenin vurulmasına yol açmıştır.
"""
        };
        var not31MartHareketOrdusu = new Note
        {
            Title = "19. Yüzyıl Islahatları — 31 Mart İsyanı ve Atatürk",
            Body = """
• **Mustafa Kemal Atatürk, 31 Mart İsyanı'nı bastırmak için İstanbul'a gelen Hareket Ordusu'nda Kolağası (Önyüzbaşı) rütbesiyle görev yapmıştır.**
"""
        };

        var notTakvimIVekayi = new Note
        {
            Title = "19. Yüzyıl Islahatları — Takvim-i Vekayi",
            Body = """
• **Takvim-i Vekayi, Osmanlı Devleti'nin ilk resmî gazetesidir.**
• **II. Mahmut döneminde çıkarılmıştır.**
"""
        };
        var notMeclisValayiAhkamiAdliye = new Note
        {
            Title = "19. Yüzyıl Islahatları — Meclis-i Vâlâ-yı Ahkâm-ı Adliye",
            Body = """
• **II. Mahmut döneminde memurların yargı ve terfi işlemleri için Meclis-i Vâlâ-yı Ahkâm-ı Adliye kurulmuştur.**
• **Bu kurum daha sonra Meclis-i Ahkâm-ı Adliye adını almış, ilerleyen dönemde Yargıtaya dönüşmüştür.**
"""
        }; var notMuharremKararnamesi = new Note
        {
            Title = "19. Yüzyıl Islahatları — Muharrem Kararnamesi",
            Body = """
• **1881 yılında yayımlanan Muharrem Kararnamesi ile Osmanlı Devleti, borçlarını ödeyemediğini ve iflas ettiğini açıklamıştır.**
• Bu gelişmenin ardından alacaklı devletlerin girişimiyle **Düyun-u Umumiye İdaresi kurulmuştur.**
• **Düyun-u Umumiye İdaresi, Osmanlı Devleti'nin bazı mali gelirlerine el koymuştur.**
• Bu kurum, Osmanlı maliyesine müdahale ettiği için **devlet içinde devlet** olarak nitelendirilmiştir.
"""
        };


        return new Topic
        {
            Name = "19. Yüzyıl Islahatları",
            Description = "II. Mahmut, Tanzimat, Islahat Fermanı, Meşrutiyetler ve II. Abdülhamid dönemi yenilikleri",
            Notes = { notSenedIttifak, notMahmutYonetim, not31MartHareketOrdusu, notTakvimIVekayi, notMuharremKararnamesi,notMahmutAskeri, notMeclisValayiAhkamiAdliye,notMahmutNufus, notMahmutEgitim, notTanzimat, notIslahatFermani, notAbdulaziz, notBirinciMesrutiyet, notCirağan, notAbdulhamidEgitim, notOsmanHamdi, notAbdulhamidUlasim, notAbdulhamidSosyal, notIkinciMesrutiyet, notOtuzBirMart, not1909Degisiklik, notMuharrem, notFikirAkimlari },
            Questions =
            {





















                new Question
{
    Note = notMuharremKararnamesi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Muharrem Kararnamesi nedir?",
    Explanation = "1881 yılında yayımlanan Muharrem Kararnamesi ile Osmanlı Devleti, borçlarını ödeyemediğini ve iflas ettiğini açıklamıştır. Bu gelişmenin ardından Düyun-u Umumiye İdaresi kurulmuştur.",
    OrderIndex = 56,
    Choices =
    {
        new Choice { Text = "Osmanlı Devleti'nin borçlarını ödeyemediğini ve iflas ettiğini açıkladığı kararnamedir.", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Osmanlı Devleti'nde I. Meşrutiyet'i ilan eden kararnamedir.", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yeniçeri Ocağı'nın kaldırılmasını sağlayan kararnamedir.", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Tanzimat Fermanı'nın uygulanmasını düzenleyen kararnamedir.", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Osmanlı Devleti'nin ilk dış borcunu almasını sağlayan kararnamedir.", IsCorrect = false, OrderIndex = 5 }
    }
},


                new Question
{
    Note = notMeclisValayiAhkamiAdliye,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Meclis-i Vâlâ-yı Ahkâm-ı Adliye ile ilgili aşağıdakilerden hangisi doğrudur?",
    Explanation = "Meclis-i Vâlâ-yı Ahkâm-ı Adliye, II. Mahmut döneminde memurların yargı ve terfi işlemleri için kurulmuştur.",
    OrderIndex = 55,
    Choices =
    {
        new Choice { Text = "Memurların yargı ve terfi işlemleri için kurulmuştur.", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Osmanlı Devleti'nin ilk resmî gazetesini çıkarmak için kurulmuştur.", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yeniçeri Ocağı'nın yönetimini sağlamak için kurulmuştur.", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "31 Mart İsyanı'nı bastırmak amacıyla kurulmuştur.", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Tanzimat Fermanı'nı hazırlamak amacıyla kurulmuştur.", IsCorrect = false, OrderIndex = 5 }
    }
},

                new Question
{
    Note = notTakvimIVekayi,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Takvim-i Vekayi ile ilgili aşağıdakilerden hangisi doğrudur?",
    Explanation = "Takvim-i Vekayi, II. Mahmut döneminde çıkarılan ilk resmî Osmanlı gazetesidir.",
    OrderIndex = 54,
    Choices =
    {
        new Choice { Text = "Osmanlı Devleti'nin ilk resmî gazetesidir.", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Osmanlı Devleti'nin ilk özel gazetesidir.", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Tanzimat Fermanı'nı ilan eden gazetedir.", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "II. Abdülhamid döneminde ilk kez yayımlanmıştır.", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Meşrutiyet yönetimine karşı çıkan bir gazete olarak kurulmuştur.", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = not31MartHareketOrdusu,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Mahmut Şevket Paşa ile ilgili aşağıdakilerden hangisi doğrudur?",
    Explanation = "Mahmut Şevket Paşa, 31 Mart İsyanı'nı bastırmak için İstanbul'a gelen Hareket Ordusu'nun komutanıdır.",
    OrderIndex = 53,
    Choices =
    {
        new Choice { Text = "31 Mart İsyanı'nı bastırmak için İstanbul'a gelen Hareket Ordusu'nun komutanıdır.", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "II. Abdülhamid'i tahttan indirerek yerine geçen Osmanlı padişahıdır.", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "31 Mart İsyanı'nı çıkaran grubun lideridir.", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Meşrutiyeti kaldırarak mutlak monarşiyi yeniden kurmuştur.", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Hareket Ordusu'na karşı İstanbul'u savunan kuvvetlerin komutanıdır.", IsCorrect = false, OrderIndex = 5 }
    }
},
                new Question
{
    Note = not31MartHareketOrdusu,
    Type = QuestionType.MultipleChoice,
    IsNegative = false,
    Text = "Mustafa Kemal Atatürk, 31 Mart İsyanı'nı bastırmak için İstanbul'a gelen Hareket Ordusu'nda hangi rütbeyle görev yapmıştır?",
    Explanation = "Mustafa Kemal Atatürk, Hareket Ordusu'nda Kolağası (Önyüzbaşı) rütbesiyle görev yapmıştır.",
    OrderIndex = 52,
    Choices =
    {
        new Choice { Text = "Kurmay Yüzbaşı", IsCorrect = true, OrderIndex = 1 },
        new Choice { Text = "Binbaşı", IsCorrect = false, OrderIndex = 2 },
        new Choice { Text = "Yarbay", IsCorrect = false, OrderIndex = 3 },
        new Choice { Text = "Albay", IsCorrect = false, OrderIndex = 4 },
        new Choice { Text = "Mirliva", IsCorrect = false, OrderIndex = 5 }
    }
},
            new Question
            {
                Note = notSenedIttifak,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde ilk defa padişah yetkilerinin sınırlandırıldığı belge aşağıdakilerden hangisidir?",
                Explanation = "Sened-i İttifak bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 1,
                Choices =
                {
                    new Choice { Text = "Sened-i İttifak", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tanzimat Fermanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Islahat Fermanı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kanun-i Esasi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notSenedIttifak,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Sened-i İttifak ile ilgili aşağıdaki ifadelerden hangisi doğrudur?",
                Explanation = "İlk demokratikleşme hareketi sayılır ve Magna Carta'ya benzetilir. bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 2,
                Choices =
                {
                    new Choice { Text = "İlk demokratikleşme hareketi sayılır ve Magna Carta'ya benzetilir.", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Yeniçeri Ocağı'nı kaldırmıştır.", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "İlk yazılı anayasa niteliğindedir.", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "II. Meşrutiyeti ilan etmiştir.", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Düyun-u Umumiye'yi kurmuştur.", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutYonetim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Mahmut döneminde Divan teşkilatının kaldırılmasından sonra oluşturulan yeni yönetim birimleri aşağıdakilerden hangisidir?",
                Explanation = "Nazırlıklar bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 3,
                Choices =
                {
                    new Choice { Text = "Nazırlıklar", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tımarlar", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Milletler", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kapitülasyonlar", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ayanlıklar", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutYonetim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nin ilk resmî gazetesi aşağıdakilerden hangisidir?",
                Explanation = "Takvim-i Vekayi bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 4,
                Choices =
                {
                    new Choice { Text = "Takvim-i Vekayi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ceride-i Havadis", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Tercüman-ı Ahval", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Tasvir-i Efkar", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Vaka-i Mısriye", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutYonetim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Memurların yargı ve terfi işlemleri için kurulan kurum aşağıdakilerden hangisidir?",
                Explanation = "Meclis-i Vâlâ-yı Ahkâm-ı Adliye bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 5,
                Choices =
                {
                    new Choice { Text = "Meclis-i Vâlâ-yı Ahkâm-ı Adliye", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ayan Meclisi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Mebusan Meclisi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Düyun-u Umumiye", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Meclis-i Mebusan-ı Milli", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutAskeri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Nizam-ı Cedid'in yerine II. Mahmut döneminde kurulan askerî teşkilat aşağıdakilerden hangisidir?",
                Explanation = "Sekban-ı Cedid bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 6,
                Choices =
                {
                    new Choice { Text = "Sekban-ı Cedid", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Asâkir-i Mansûre-i Muhammediye", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Nizam-ı Cedid", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Redif Birlikleri", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Hareket Ordusu", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutAskeri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1826 yılında kaldırılan askerî ocak aşağıdakilerden hangisidir?",
                Explanation = "Yeniçeri Ocağı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 7,
                Choices =
                {
                    new Choice { Text = "Yeniçeri Ocağı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sekban-ı Cedid", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Eşkinci Ocağı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Redif Birlikleri", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Mızıka-yı Hümayun", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutAskeri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Yeniçeri Ocağı'nın kaldırılması Osmanlı tarihinde hangi adla anılır?",
                Explanation = "Vak'a-i Hayriye bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 8,
                Choices =
                {
                    new Choice { Text = "Vak'a-i Hayriye", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Çırağan Vakası", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "31 Mart Vakası", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutAskeri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Yeniçeri Ocağı kaldırıldıktan sonra yerine kurulan yeni ordu aşağıdakilerden hangisidir?",
                Explanation = "Asâkir-i Mansûre-i Muhammediye bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 9,
                Choices =
                {
                    new Choice { Text = "Asâkir-i Mansûre-i Muhammediye", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sekban-ı Cedid", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Eşkinci Ocağı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Nizam-ı Cedid", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Hareket Ordusu", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutAskeri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Köy ve kasabaların güvenliğini sağlamak amacıyla kurulan birlikler aşağıdakilerden hangisidir?",
                Explanation = "Redif birlikleri bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 10,
                Choices =
                {
                    new Choice { Text = "Redif birlikleri", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ayan birlikleri", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Hareket birlikleri", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Tımarlı birlikler", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Hafiye birlikleri", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutAskeri,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Seraskerlik günümüzdeki hangi kuruma karşılık gelir?",
                Explanation = "Genelkurmay Başkanlığı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 11,
                Choices =
                {
                    new Choice { Text = "Genelkurmay Başkanlığı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Danıştay", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Yargıtay", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "İçişleri Bakanlığı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Maliye Bakanlığı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutNufus,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Mahmut döneminde yapılan ilk nüfus sayımının temel amaçlarından biri aşağıdakilerden hangisidir?",
                Explanation = "Askerlik çağına gelenleri belirlemek ve vergi almak bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 12,
                Choices =
                {
                    new Choice { Text = "Askerlik çağına gelenleri belirlemek ve vergi almak", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Yalnızca kadın nüfusunu belirlemek", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Seçim çevrelerini oluşturmak", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Gayrimüslimlere milletvekili seçmek", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Siyasi partileri kayıt altına almak", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutNufus,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Mahmut dönemindeki ilk nüfus sayımıyla ilgili aşağıdaki bilgilerden hangisi doğrudur?",
                Explanation = "Kadınlar sayılmamıştır. bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 13,
                Choices =
                {
                    new Choice { Text = "Kadınlar sayılmamıştır.", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sadece kadınlar sayılmıştır.", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Yalnızca saray görevlileri sayılmıştır.", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Hiçbir mal ve hayvan sayılmamıştır.", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Sayım sadece İstanbul'da yapılmıştır.", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutEgitim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde ilköğretimi zorunlu hâle getiren padişah aşağıdakilerden hangisidir?",
                Explanation = "II. Mahmut bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 14,
                Choices =
                {
                    new Choice { Text = "II. Mahmut", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Abdülmecid", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Abdülaziz", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "V. Murat", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "II. Abdülhamid", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMahmutEgitim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Mehterhanenin kaldırılmasından sonra kurulan kurum aşağıdakilerden hangisidir?",
                Explanation = "Mızıka-yı Hümayun bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 15,
                Choices =
                {
                    new Choice { Text = "Mızıka-yı Hümayun", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mekteb-i Tıbbiye", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Hareket Ordusu", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ayan Meclisi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Düyun-u Umumiye", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notTanzimat,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1839 yılında Gülhane Parkı'nda ilan edilen belge aşağıdakilerden hangisidir?",
                Explanation = "Tanzimat Fermanı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 16,
                Choices =
                {
                    new Choice { Text = "Tanzimat Fermanı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Islahat Fermanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Kanun-i Esasi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notTanzimat,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Tanzimat Fermanı'nın hazırlanmasında etkili olan devlet adamı aşağıdakilerden hangisidir?",
                Explanation = "Mustafa Reşit Paşa bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 17,
                Choices =
                {
                    new Choice { Text = "Mustafa Reşit Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mahmut Şevket Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Alemdar Mustafa Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ali Suavi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Osman Hamdi Bey", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notTanzimat,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Tanzimat Fermanı'nın diğer adı aşağıdakilerden hangisidir?",
                Explanation = "Gülhane Hatt-ı Hümayunu bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 18,
                Choices =
                {
                    new Choice { Text = "Gülhane Hatt-ı Hümayunu", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Kanun-i Esasi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Islahat Fermanı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIslahatFermani,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1856 yılında ilan edilen ve özellikle gayrimüslimlere verilen hakları genişleten belge aşağıdakilerden hangisidir?",
                Explanation = "Islahat Fermanı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 19,
                Choices =
                {
                    new Choice { Text = "Islahat Fermanı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tanzimat Fermanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kanun-i Esasi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulaziz,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1863 yılında çiftçiye kredi sağlama amacıyla kurulan ve Ziraat Bankasının temeli kabul edilen kurum aşağıdakilerden hangisidir?",
                Explanation = "Memleket Sandıkları bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 20,
                Choices =
                {
                    new Choice { Text = "Memleket Sandıkları", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Düyun-u Umumiye", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Darülhayr-ı Ali", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Mızıka-yı Hümayun", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ayan Meclisi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notBirinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti ve Türk tarihinin ilk yazılı anayasası aşağıdakilerden hangisidir?",
                Explanation = "Kanun-i Esasi bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 21,
                Choices =
                {
                    new Choice { Text = "Kanun-i Esasi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tanzimat Fermanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Islahat Fermanı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notBirinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1876 yılında I. Meşrutiyeti ilan eden padişah aşağıdakilerden hangisidir?",
                Explanation = "II. Abdülhamid bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 22,
                Choices =
                {
                    new Choice { Text = "II. Abdülhamid", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "II. Mahmut", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Abdülmecid", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Abdülaziz", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "V. Murat", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notBirinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "I. Meşrutiyet döneminde üyelerini halkın seçtiği meclis aşağıdakilerden hangisidir?",
                Explanation = "Mebusan Meclisi bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 23,
                Choices =
                {
                    new Choice { Text = "Mebusan Meclisi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ayan Meclisi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Düyun-u Umumiye", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Meclis-i Vâlâ", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Dâr-ı Şûrâ-yı Askerî", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notBirinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "I. Meşrutiyet döneminde üyelerini padişahın atadığı meclis aşağıdakilerden hangisidir?",
                Explanation = "Ayan Meclisi bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 24,
                Choices =
                {
                    new Choice { Text = "Ayan Meclisi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mebusan Meclisi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Hareket Ordusu", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Meclis-i Vâlâ", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Seraskerlik", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notBirinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1876 Kanun-i Esasi'ne göre hükümet yaptığı işlerden kime karşı sorumludur?",
                Explanation = "Padişaha karşı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 25,
                Choices =
                {
                    new Choice { Text = "Padişaha karşı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mebusan Meclisine karşı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ayan Meclisine karşı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Halka karşı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Düyun-u Umumiye'ye karşı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notCirağan,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1878'de Ali Suavi önderliğinde II. Abdülhamid'i tahttan indirip V. Murat'ı yeniden tahta çıkarma girişimi hangi olaydır?",
                Explanation = "Çırağan Vakası bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 26,
                Choices =
                {
                    new Choice { Text = "Çırağan Vakası", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "31 Mart Vakası", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Vak'a-i Hayriye", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kabakçı Mustafa İsyanı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notCirağan,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Çırağan Vakası'nı Ali Suavi'yi öldürerek engelleyen kişi aşağıdakilerden hangisidir?",
                Explanation = "Beşiktaş Muhafızı Hasan Paşa bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 27,
                Choices =
                {
                    new Choice { Text = "Beşiktaş Muhafızı Hasan Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mahmut Şevket Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Mustafa Reşit Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Alemdar Mustafa Paşa", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Osman Hamdi Bey", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulhamidEgitim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Eğitime verdiği önem nedeniyle Maarifperver unvanıyla anılan padişah aşağıdakilerden hangisidir?",
                Explanation = "II. Abdülhamid bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 28,
                Choices =
                {
                    new Choice { Text = "II. Abdülhamid", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "II. Mahmut", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Abdülmecid", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Abdülaziz", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "V. Murat", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notOsmanHamdi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Sanayi-i Nefise Mektebinin kurucusu olan Osmanlı Devleti'nin ilk müzecisi ve arkeoloğu aşağıdakilerden hangisidir?",
                Explanation = "Osman Hamdi Bey bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 29,
                Choices =
                {
                    new Choice { Text = "Osman Hamdi Bey", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Şeker Ahmet Paşa", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Mustafa Reşit Paşa", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Ali Suavi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Mahmut Şevket Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notOsmanHamdi,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı Devleti'nde ilk defa açılan müzenin adı aşağıdakilerden hangisidir?",
                Explanation = "Asar-ı Atika bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 30,
                Choices =
                {
                    new Choice { Text = "Asar-ı Atika", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Takvim-i Vekayi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Mızıka-yı Hümayun", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Memleket Sandıkları", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Darülhayr-ı Ali", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulhamidUlasim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Abdülhamid döneminde önem kazanan ve Almanya ile yakınlaşmayla bağlantılı demiryolu projesi aşağıdakilerden hangisidir?",
                Explanation = "Berlin-Bağdat Demiryolu bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 31,
                Choices =
                {
                    new Choice { Text = "Berlin-Bağdat Demiryolu", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Hicaz-Balkan Demiryolu", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "İstanbul-Moskova Demiryolu", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kahire-Paris Demiryolu", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ankara-İzmir Demiryolu", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulhamidUlasim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Hicaz Demiryolu'nun son durağı aşağıdakilerden hangisidir?",
                Explanation = "Medine bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 32,
                Choices =
                {
                    new Choice { Text = "Medine", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mekke", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Kudüs", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Şam", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Bağdat", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulhamidUlasim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Abdülhamid döneminde haber alma ve takip amacıyla kullanılan teşkilatlar aşağıdakilerden hangisidir?",
                Explanation = "Hafiye ve Jurnal teşkilatları bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 33,
                Choices =
                {
                    new Choice { Text = "Hafiye ve Jurnal teşkilatları", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ayan ve Mebusan teşkilatları", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Tımar ve İltizam teşkilatları", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Sekban ve Eşkinci teşkilatları", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Redif ve Seraskerlik teşkilatları", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulhamidUlasim,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Abdülhamid döneminde yönetim merkezi nereye taşınmıştır?",
                Explanation = "Yıldız Sarayı'na bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 34,
                Choices =
                {
                    new Choice { Text = "Yıldız Sarayı'na", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Topkapı Sarayı'na", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Dolmabahçe Sarayı'na", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Beylerbeyi Sarayı'na", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Çırağan Sarayı'na", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notAbdulhamidSosyal,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1903'te Ermeni saldırıları sonucunda yetim kalan çocukları korumak ve yetiştirmek amacıyla açılan kurum aşağıdakilerden hangisidir?",
                Explanation = "Darülhayr-ı Ali bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 35,
                Choices =
                {
                    new Choice { Text = "Darülhayr-ı Ali", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Dârüşşafaka", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Memleket Sandıkları", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Düyun-u Umumiye", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Mızıka-yı Hümayun", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Meşrutiyet hangi yılda ilan edilmiştir?",
                Explanation = "1908 bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 36,
                Choices =
                {
                    new Choice { Text = "1908", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "1839", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "1856", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "1876", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "1881", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Meşrutiyetin ilanı için II. Abdülhamid'e baskı yapan önemli siyasi oluşum aşağıdakilerden hangisidir?",
                Explanation = "İttihat ve Terakki bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 37,
                Choices =
                {
                    new Choice { Text = "İttihat ve Terakki", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Düyun-u Umumiye", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Memleket Sandıkları", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Redif birlikleri", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Meşrutiyetin ilanından sonra tekrar oluşturulan meclisler aşağıdakilerden hangisidir?",
                Explanation = "Ayan ve Mebusan Meclisleri bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 38,
                Choices =
                {
                    new Choice { Text = "Ayan ve Mebusan Meclisleri", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Divan ve Meşveret Meclisi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Seraskerlik ve Redif Meclisi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Düyun-u Umumiye ve Ayanlık", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Nazırlıklar ve Tımarlar", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notIkinciMesrutiyet,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Meşrutiyet bir süre hangi adla kutlanmıştır?",
                Explanation = "Hürriyet Bayramı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 39,
                Choices =
                {
                    new Choice { Text = "Hürriyet Bayramı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Zafer Bayramı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Meşrutiyet Bayramı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Islahat Bayramı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kanun Bayramı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notOtuzBirMart,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Osmanlı tarihinde rejimi değiştirmeye yönelik çıkan ilk ve tek isyan olarak anlatılan olay aşağıdakilerden hangisidir?",
                Explanation = "31 Mart Vakası bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 40,
                Choices =
                {
                    new Choice { Text = "31 Mart Vakası", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Çırağan Vakası", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Vak'a-i Hayriye", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kabakçı Mustafa İsyanı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Patrona Halil İsyanı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notOtuzBirMart,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "31 Mart Vakası'nı bastırmak için Selanik'ten İstanbul'a gelen ordunun adı aşağıdakilerden hangisidir?",
                Explanation = "Hareket Ordusu bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 41,
                Choices =
                {
                    new Choice { Text = "Hareket Ordusu", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Asâkir-i Mansûre", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Nizam-ı Cedid", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Sekban-ı Cedid", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Redif Ordusu", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notOtuzBirMart,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Hareket Ordusunun komutanı aşağıdakilerden hangisidir?",
                Explanation = "Mahmut Şevket Paşa bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 42,
                Choices =
                {
                    new Choice { Text = "Mahmut Şevket Paşa", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Mustafa Kemal", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ali Suavi", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Osman Hamdi Bey", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Mustafa Reşit Paşa", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notOtuzBirMart,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Mustafa Kemal'in tarih sahnesine ilk kez çıkışı hangi olayla ilişkilendirilmiştir?",
                Explanation = "31 Mart Vakası'nı bastırmak üzere gelen Hareket Ordusunda kurmay yüzbaşı olarak görev alması bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 43,
                Choices =
                {
                    new Choice { Text = "31 Mart Vakası'nı bastırmak üzere gelen Hareket Ordusunda kurmay yüzbaşı olarak görev alması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tanzimat Fermanını ilan etmesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Sened-i İttifakı imzalaması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Yeniçeri Ocağını kaldırması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Kanun-i Esasi'yi hazırlaması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = not1909Degisiklik,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1909 Kanun-i Esasi değişikliklerinden biri aşağıdakilerden hangisidir?",
                Explanation = "Sürgün ve angarya cezasının kaldırılması bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 44,
                Choices =
                {
                    new Choice { Text = "Sürgün ve angarya cezasının kaldırılması", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Padişahın sınırsız veto yetkisinin güçlendirilmesi", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Meclisin tamamen kaldırılması", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kanun teklifinin yalnız hükümete bırakılması", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Siyasi parti kurmanın yasaklanması", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = not1909Degisiklik,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1909 değişiklikleriyle hükümetin sorumluluğu hangi yönde değiştirilmiştir?",
                Explanation = "Padişahtan meclise doğru kaydırılmıştır. bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 45,
                Choices =
                {
                    new Choice { Text = "Padişahtan meclise doğru kaydırılmıştır.", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Meclisten padişaha doğru kaydırılmıştır.", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Halktan ayanlara devredilmiştir.", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Padişahtan Düyun-u Umumiye'ye verilmiştir.", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Seraskerlikten nazırlıklara aktarılmıştır.", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = not1909Degisiklik,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1909 değişiklikleriyle siyasi hayat açısından tanınan hak aşağıdakilerden hangisidir?",
                Explanation = "Siyasi parti ve dernek kurma hakkı bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 46,
                Choices =
                {
                    new Choice { Text = "Siyasi parti ve dernek kurma hakkı", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Ayanlığı miras bırakma hakkı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Yeniçeri Ocağını yeniden kurma hakkı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Padişahın sınırsız veto hakkı", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Tımarları özel mülke dönüştürme hakkı", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMuharrem,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "1881'de Osmanlı Devleti'nin dış borçların faizlerini ödeyememesi üzerine ilan edilen kararname aşağıdakilerden hangisidir?",
                Explanation = "Muharrem Kararnamesi bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 47,
                Choices =
                {
                    new Choice { Text = "Muharrem Kararnamesi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Tanzimat Fermanı", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Islahat Fermanı", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Kanun-i Esasi", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Sened-i İttifak", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notMuharrem,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Alacaklı devletlerin Osmanlı borçlarını tahsil etmesi için kurulan kurum aşağıdakilerden hangisidir?",
                Explanation = "Düyun-u Umumiye İdaresi bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 48,
                Choices =
                {
                    new Choice { Text = "Düyun-u Umumiye İdaresi", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Meclis-i Mebusan", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Memleket Sandıkları", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Mızıka-yı Hümayun", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Dâr-ı Şûrâ-yı Askerî", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notFikirAkimlari,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "II. Abdülhamid döneminde resmî dış politika hâline getirilen fikir akımı aşağıdakilerden hangisidir?",
                Explanation = "İslamcılık bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 49,
                Choices =
                {
                    new Choice { Text = "İslamcılık", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Osmanlıcılık", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Türkçülük", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Batıcılık", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Ayanlık", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notFikirAkimlari,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "İttihad-ı Anasır anlayışı aşağıdakilerden hangisiyle ilişkilidir?",
                Explanation = "Osmanlı unsurlarının meclis çatısı altında temsil edilmesiyle bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 50,
                Choices =
                {
                    new Choice { Text = "Osmanlı unsurlarının meclis çatısı altında temsil edilmesiyle", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "Yeniçerilerin yeniden kurulmasıyla", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Ayanların vergi toplamasıyla", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Dış borçların tahsiliyle", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Demiryolu yapımının Almanlara verilmesiyle", IsCorrect = false, OrderIndex = 5 }
                }
            },

            new Question
            {
                Note = notFikirAkimlari,
                Type = QuestionType.MultipleChoice,
                IsNegative = false,
                Text = "Balkan uluslarının Osmanlı Devleti'nden ayrılması sonucunda tamamen geçerliliğini kaybeden fikir akımı aşağıdakilerden hangisidir?",
                Explanation = "Osmanlıcılık bilgisi ders notunda verilen bilgiler arasındadır.",
                OrderIndex = 51,
                Choices =
                {
                    new Choice { Text = "Osmanlıcılık", IsCorrect = true, OrderIndex = 1 },
                    new Choice { Text = "İslamcılık", IsCorrect = false, OrderIndex = 2 },
                    new Choice { Text = "Türkçülük", IsCorrect = false, OrderIndex = 3 },
                    new Choice { Text = "Batıcılık", IsCorrect = false, OrderIndex = 4 },
                    new Choice { Text = "Merkeziyetçilik", IsCorrect = false, OrderIndex = 5 }
                }
            }
            },
        };
    }


}

/* Seed yazarken örnek şablon:

Choices bir HAVUZDUR — buraya istediğin kadar doğru ve yanlış şık yazabilirsin.
Kullanıcı hepsini görmez: soru her açıldığında havuzdaki doğrulardan rastgele 1,
yanlışlardan rastgele 4 tanesi çekilir ve karıştırılarak 5 şık olarak sunulur.
Bu yüzden havuzda en az 1 doğru ve tercihen 4+ yanlış bulunmalı; yanlışları
öğrencinin kafasını karıştıracak çeldiriciler olarak notlardan seçin.
OrderIndex şıklarda sıralama için kullanılmaz (sıra rastgeledir), sadece düzen amaçlıdır.
Question.OrderIndex ise Konu içinde soru başına benzersiz olmalıdır — SyncTopicAsync
soruları buna göre eşleştirir (bkz. yukarıdaki Validate ve SyncTopicAsync).

var note = new Note
{
    Title = "Notun başlığı",
    Body = "Markdown gövde..."
};

var topic = new Topic
{
    Name = "Konu adı",
    Description = "Kısa açıklama",
    Notes = { note },
    Questions =
    {
        new Question
        {
            Note = note,                       // ilgili not bağı
            Type = QuestionType.MultipleChoice,
            Text = "Aşağıdakilerden hangisi doğrudur?",
            Explanation = "Kısa açıklama",
            OrderIndex = 1,
            Choices =
            {
                // Doğru havuzu — her açılışta biri seçilir
                new Choice { Text = "Doğru ifade 1", IsCorrect = true,  OrderIndex = 1 },
                new Choice { Text = "Doğru ifade 2", IsCorrect = true,  OrderIndex = 2 },

                // Yanlış havuzu (çeldiriciler) — her açılışta 4 tanesi seçilir
                new Choice { Text = "Çeldirici 1", IsCorrect = false, OrderIndex = 3 },
                new Choice { Text = "Çeldirici 2", IsCorrect = false, OrderIndex = 4 },
                new Choice { Text = "Çeldirici 3", IsCorrect = false, OrderIndex = 5 },
                new Choice { Text = "Çeldirici 4", IsCorrect = false, OrderIndex = 6 },
                new Choice { Text = "Çeldirici 5", IsCorrect = false, OrderIndex = 7 },
            }
        }
    }
};
topics.Add(topic);

*/
