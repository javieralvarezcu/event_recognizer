using EventRecognizer.Api.Data;
using EventRecognizer.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? throw new InvalidOperationException(
        "Connection string not found. Set ConnectionStrings__DefaultConnection environment variable " +
        "or add a ConnectionStrings.DefaultConnection entry in appsettings.Development.json.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

// --- HTTP Client for DeepSeek (API key is set per-request from the client header) ---
builder.Services.AddHttpClient("DeepSeek", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

// --- App Services ---
builder.Services.AddScoped<IDeepSeekService, DeepSeekService>();
builder.Services.AddScoped<IEventService, EventService>();

// --- Controllers & Swagger ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Event Recognizer API",
        Version = "v1",
        Description = """
            API para reconocer carteles de eventos en posts de Instagram usando LLM (DeepSeek).

            **Importante:** El endpoint `POST /api/events/recognize` requiere el header `X-DeepSeek-API-Key` con un token válido de API de DeepSeek.
            """
    });

    // Permitir meter la API Key desde Swagger UI
    c.AddSecurityDefinition("DeepSeekApiKey", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-DeepSeek-API-Key",
        Description = "Token de API de DeepSeek"
    });

    c.AddSecurityRequirement(new()
    {
        {
            new()
            {
                Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "DeepSeekApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Apply EF Core migrations on startup (auto-creates DB if needed)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// --- Middleware ---
app.UseSwagger();
app.UseSwaggerUI();

// --- Static panel (calendar UI) at the app root ---
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();
