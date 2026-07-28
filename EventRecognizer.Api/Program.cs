using EventRecognizer.Api.Data;
using EventRecognizer.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

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
});

var app = builder.Build();

// Auto-create database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// --- Middleware ---
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
