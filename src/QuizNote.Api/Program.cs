using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using QuizNote.Api.Services;
using QuizNote.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<QuizNoteDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
                 ?? throw new InvalidOperationException("Jwt yapılandırması eksik.");
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddScoped<TokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // Sub claim'i otomatik olarak NameIdentifier'a çevrilmesin; token'daki
        // ClaimTypes.NameIdentifier değeri olduğu gibi kalsın.
        opt.MapInboundClaims = false;
        opt.TokenValidationParameters.NameClaimType = ClaimTypes.NameIdentifier;
    });
builder.Services.AddAuthorization();

const string CorsPolicy = "web";
builder.Services.AddCors(opt => opt.AddPolicy(CorsPolicy, p => p
    .WithOrigins(
        "http://localhost:5173",
        "http://localhost:5174",
        "http://localhost:4173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "QuizNote API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT token'ı 'Bearer' olmadan yapıştırın."
    });
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", doc)] = new List<string>()
    });
});

var app = builder.Build();

// Açılışta migration'ları uygula (ve seed varsa yaz).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuizNoteDbContext>();
    await DbSeeder.MigrateAndSeedAsync(db);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);
app.UseAuthentication();

// [Authorize] taşımayan uçlarda kimlik otomatik çözülmez; token gelmişse
// burada çözülür. Seviye takibi giriş yapan kullanıcı için bu sayede işler.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true &&
        context.Request.Headers.ContainsKey("Authorization"))
    {
        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (result.Succeeded) context.User = result.Principal;
    }

    await next();
});

app.UseAuthorization();
app.MapControllers();

app.Run();
