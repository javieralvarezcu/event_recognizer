# Event Recognizer

API (.NET 8 + EF Core + SQL Server) que reconoce carteles de eventos en posts de
Instagram usando DeepSeek, con panel web de calendario. Ver `README.md` para la
documentación completa (endpoints, esquema de BD, despliegue Docker).

## Cómo trabajar en este repo

- Los tests viven en `EventRecognizer.Api.Tests` (xUnit + SQLite in-memory, sin BD real). Antes de commitear: `dotnet test EventRecognizer.sln`.
- Migraciones EF: `dotnet ef migrations add <Name> --project EventRecognizer.Api`. Se aplican solas al arrancar (`db.Database.Migrate()` en `Program.cs`).
- Commits en inglés, estilo lowercase imperativo (historial del repo), con `Co-Authored-By: Claude <noreply@anthropic.com>`. **Nunca commitear ni pushear por iniciativa propia**: solo cuando Javier lo pida explícitamente (preparar los cambios y esperar su visto bueno). Cuando lo pida, se pushea directo a `main`.
- El usuario habla español: explicaciones y resúmenes en español.

## Arquitectura de coste de tokens (decisiones de sept 2026 — no deshacer sin motivo)

1. **Tabla `Posts` (registro por URL)**: todo post que llega por los endpoints de
   reconocimiento se registra por URL con su veredicto (`IsEvent` + `AnalysisJson`).
   URLs ya analizadas — eventos y no-eventos — **nunca vuelven al LLM**. Filas con
   `AnalysisJson = NULL` se re-analizan en la siguiente petición. (Sustituyó a una
   caché por hash de contenido; la tabla `PostAnalysisCaches` fue eliminada.)
2. **Análisis en dos fases** (`DeepSeekService`): filtro barato
   `analyze_posts_filter` (solo is_event, 60 posts/chunk) y extracción completa
   `analyze_posts_extract` solo para eventos, en chunks de **20** (30 desbordaba el
   límite de 4096 tokens de salida con datos reales; las respuestas truncadas se
   facturan igualmente). Si un chunk agota los 3 intentos se parte en mitades
   (split-and-retry); nunca re-mandar el mismo chunk gigante.
3. **Turno global de análisis** (`PostRegistryService`, singleton): las peticiones
   concurrentes serializan la fase de análisis con un semáforo; la primera paga el
   LLM y las demás re-consultan el registro. **No reintroducir un registro in-flight
   por hash**: la versión anterior provocaba deadlocks entre runs concurrentes.
4. **Semáforo HTTP global**: máximo 2 peticiones simultáneas a DeepSeek (rate limits
   bajos); backoff de reintentos 2s/4s.
5. **Auditoría `DeepSeekCallLogs`**: una fila por intento HTTP al LLM (prompts,
   respuesta, tokens, tiempos, errores; la API key nunca se guarda). Es la
   herramienta de diagnóstico de coste.

## Pipeline de producción (fuera del repo)

- API de producción: `https://eventrecognizer.santanitaxx.com` (Docker + SQL Server).
- n8n (`https://n8n.davru.link/`, workflow "muxo jaleo calendar") orquesta el
  scraping: cron mar/vie 00:00 → Bright Data dataset (async) → espera 180s → descarga
  snapshot → mapea ~4 posts de ~22 perfiles → `POST /api/events/recognize` → genera
  calendario → Nextcloud + email. Los secretos (DeepSeek, Bright Data) están en
  texto plano dentro del workflow; pendiente moverlos a Credentials de n8n y rotar.
- Las tormentas de llamadas históricas fueron **ejecuciones manuales repetidas** del
  workflow durante pruebas, no retries automáticos.
