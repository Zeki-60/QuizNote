using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizNote.Api.Services;
using QuizNote.Application.Dtos;
using QuizNote.Application.Entities;
using QuizNote.Application.Services;
using QuizNote.Persistence;

namespace QuizNote.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(QuizNoteDbContext db, TokenService tokens) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest(new { message = "Geçerli bir e-posta adresi girin." });
        if (req.Password.Length < 6)
            return BadRequest(new { message = "Parola en az 6 karakter olmalı." });
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest(new { message = "Görünen ad boş olamaz." });

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Conflict(new { message = "Bu e-posta zaten kayıtlı." });

        var user = new User
        {
            Email = email,
            DisplayName = req.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(req.Password)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Ok(new AuthResponse(tokens.CreateToken(user), user.Id, user.Email, user.DisplayName));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "E-posta veya parola hatalı." });

        return Ok(new AuthResponse(tokens.CreateToken(user), user.Id, user.Email, user.DisplayName));
    }
}
