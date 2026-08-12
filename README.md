# QuizNote

Soru–cevap web uygulaması. Her sorunun bağlı olduğu bir **not parçası** vardır;
soruyu çözerken "İlgili notu göster" ile o not sağdan açılan panelde okunabilir.

## Yapı

| Proje | Yol | Açıklama |
|---|---|---|
| Backend | `QuizNote/` | .NET 10, 3 katman: Api / Application / Persistence |
| Frontend | `QuizNote.Web/` | Vite + React + TypeScript |
| Veritabanı | `docker-compose.yml` | PostgreSQL 17, Docker container |

## Çalıştırma

```bash
# 1) Veritabanı
cd QuizNote
docker compose up -d

# 2) Backend  -> http://localhost:5080  (Swagger: /swagger)
cd src/QuizNote.Api
dotnet run --no-launch-profile

# 3) Frontend -> http://localhost:5174
cd QuizNote.Web
npm install
npm run dev
```

Backend açılışta migration'ları otomatik uygular.

## DBeaver bağlantısı

| Alan | Değer |
|---|---|
| Host | `localhost` |
| Port | `5435` |
| Database | `quiznote` |
| Kullanıcı | `quiznote` |
| Parola | `quiznote123` |

> Port 5435 seçildi; 5432/5433/5434 makinedeki diğer container'larda dolu.

## Veri modeli

```
Topic ──┬── Note ──── Question ──┬── Choice      (çoktan seçmeli / doğru-yanlış)
        └── Question             └── MatchPair   (eşleştirme)

User ──── QuizAttempt ──── AttemptAnswer
```

Her `Question` zorunlu olarak bir `Note`'a bağlıdır — projenin çekirdek fikri bu bağdır.

## Soru ekleme

Sorular seed data ile eklenir: `src/QuizNote.Persistence/DbSeeder.cs` içindeki
`BuildSeedData()` metodu. Dosyanın altında hazır bir şablon yorumu var.
Seed yalnızca tablolar boşken çalışır. Gerekirse DBeaver'dan da SQL ile eklenebilir.

API'de yazma (POST/PUT/DELETE) ucu bilerek yoktur; uygulama içeriği sadece okur.

## API uçları

| Uç | Yetki | Açıklama |
|---|---|---|
| `POST /api/auth/register` | — | Kayıt |
| `POST /api/auth/login` | — | Giriş, JWT döner |
| `GET /api/topics` | — | Konu listesi |
| `GET /api/topics/{id}/questions` | — | Konunun soruları (doğru cevap **dönmez**) |
| `GET /api/questions/{id}/note` | — | Sorunun ilgili notu |
| `POST /api/answers` | opsiyonel | Cevabı değerlendirir; sonuç + notu döner |
| `POST /api/topics/{id}/attempts` | JWT | Deneme başlatır |
| `POST /api/attempts/{id}/complete` | JWT | Denemeyi bitirir |
| `GET /api/me/attempts` | JWT | Geçmiş denemeler |

Giriş yapmadan da sorular çözülebilir; skor kaydı için JWT gerekir.
