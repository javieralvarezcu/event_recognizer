# Event Recognizer API

API para reconocer automáticamente carteles de eventos en publicaciones de Instagram utilizando el modelo de lenguaje [DeepSeek](https://deepseek.com/).

## Índice

- [Descripción general](#descripción-general)
- [Stack tecnológico](#stack-tecnológico)
- [Arquitectura](#arquitectura)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Endpoints de la API](#endpoints-de-la-api)
- [Esquema de base de datos](#esquema-de-base-de-datos)
- [Requisitos](#requisitos)
- [Configuración local (desarrollo)](#configuración-local-desarrollo)
- [Ejecución con Docker](#ejecución-con-docker)
- [Despliegue en producción](#despliegue-en-producción)
- [Variables de entorno](#variables-de-entorno)
- [Prompt del LLM y lógica de detección](#prompt-del-llm-y-lógica-de-detección)
- [Seguridad](#seguridad)
- [Migraciones de base de datos](#migraciones-de-base-de-datos)

---

## Descripción general

Event Recognizer es una API REST que analiza publicaciones de Instagram y detecta cuáles son carteles o anuncios de eventos (conciertos, fiestas, festivales, obras de teatro, exposiciones, etc.).

El flujo de reconocimiento:

1. El cliente envía un lote de posts de Instagram a `POST /api/events/recognize` junto con su API key de DeepSeek y, opcionalmente, un rango de fechas (`dateFrom`/`dateTo`).
2. El servidor formatea los posts y los envía a DeepSeek con un prompt en español (incluye el rango solicitado como contexto y exige resolver fechas aproximadas o de eventos con nombre propio).
3. DeepSeek analiza cada post y devuelve un JSON estructurado indicando si es evento, título, fecha, resumen y patrón de recurrencia (semanal o rango de días).
4. Si se solicitó un rango de fechas, el servidor evalúa de forma determinista si cada evento — con fecha concreta o recurrente — ocurre dentro de ese rango; los que no, se tratan como si no fueran eventos.
5. Los eventos válidos se persisten en SQL Server (con deduplicación por `PostId`).
6. Se devuelven **todos** los posts al cliente — tanto los clasificados como evento como los que no — con la información estructurada correspondiente.

Los eventos persistidos pueden consultarse posteriormente mediante `GET /api/events/{eventUniqueId}`.

---

## Stack tecnológico

| Componente | Tecnología |
|---|---|
| Runtime | [.NET 8.0](https://dotnet.microsoft.com/) (ASP.NET Core Web API) |
| Lenguaje | C# 12 |
| ORM | Entity Framework Core 8.0 |
| Base de datos | SQL Server 2022 (Express) |
| LLM | DeepSeek Chat API (`deepseek-chat`) |
| Tests | xUnit + SQLite en memoria |
| Documentación | Swagger / OpenAPI (Swashbuckle) |
| Contenedores | Docker + Docker Compose |
| Túnel (prod) | Cloudflare Tunnel |

### Paquetes NuGet

| Paquete | Versión | Uso |
|---|---|---|
| `Microsoft.AspNetCore.OpenApi` | 8.0.29 | Generación de especificación OpenAPI |
| `Microsoft.EntityFrameworkCore.SqlServer` | 8.0.0 | Proveedor EF Core para SQL Server |
| `Microsoft.EntityFrameworkCore.Design` | 8.0.0 | Herramientas de migraciones en tiempo de diseño |
| `Swashbuckle.AspNetCore` | 6.5.0 | Swagger UI |

---

## Arquitectura

```
Cliente HTTP
    │
    │  POST /api/events/recognize   (header: X-DeepSeek-API-Key)
    │  GET  /api/events/{eventUniqueId}
    ▼
EventsController
    │
    ▼
EventService (orquestador)
    ├── DeepSeekService  ────  api.deepseek.com/v1/chat/completions
    └── AppDbContext     ────  SQL Server (tabla EventRecords)
```

- **EventsController** — endpoints REST, validación del header `X-DeepSeek-API-Key`, manejo de errores HTTP.
- **EventService** — lógica de negocio: llama al LLM, construye `EventRecord`, deduplica por `PostId`, persiste y mapea respuestas.
- **DeepSeekService** — comunicación HTTP con la API de DeepSeek, construcción de prompts, envío de requests y deserialización de la respuesta JSON estructurada.
- **AppDbContext** — acceso a base de datos con EF Core.

---

## Estructura del proyecto

```
event_recognizer/
├── .env                                  # Variables de entorno (secretos)
├── .gitignore
├── docker-compose.yml                    # Servicios: api + sqlserver
├── docker-compose.prod.yml               # Override producción: cloudflared
├── README.md
├── EventRecognizer.Api/
    ├── Dockerfile
    ├── .dockerignore
    ├── EventRecognizer.Api.csproj
    ├── Program.cs                        # Bootstrap de la aplicación
    ├── appsettings.json                  # Configuración base
    ├── appsettings.Development.json      # Override para desarrollo
    ├── Properties/
    │   └── launchSettings.json
    ├── Controllers/
    │   └── EventsController.cs           # Endpoints REST
    ├── Data/
    │   ├── AppDbContext.cs               # DbContext de EF Core
    │   └── AppDbContextFactory.cs        # Factory para CLI de migraciones
    ├── Dtos/
    │   └── RecognitionResponses.cs       # DTOs de respuesta de la API
    ├── Migrations/
    │   └── 20260902013644_InitialCreate.cs
    ├── Models/
    │   ├── DateRange.cs                  # Rango de fechas opcional (from/to)
    │   ├── DeepSeekModels.cs             # DTOs para la API de DeepSeek
    │   ├── EventRecord.cs                # Entidad de base de datos
    │   └── InstagramPost.cs              # DTO de entrada (post de Instagram)
    └── Services/
        ├── IDeepSeekService.cs
        ├── DeepSeekService.cs            # Cliente HTTP para DeepSeek
        ├── IEventService.cs
        ├── EventService.cs               # Orquestador de reconocimiento
        └── RecurrenceEvaluator.cs        # Evalúa si un evento/recurrencia cae en un rango
└── EventRecognizer.Api.Tests/           # Proyecto de tests (xUnit + SQLite en memoria)
    ├── TestDoubles.cs                    # Fakes y builders compartidos
    ├── DeepSeekServiceTests.cs
    ├── EventServiceTests.cs
    ├── EventsControllerTests.cs
    └── RecurrenceEvaluatorTests.cs
```

---

## Endpoints de la API

### `POST /api/events/recognize`

Analiza un lote de publicaciones de Instagram, detecta cuáles son carteles de eventos y persiste los eventos encontrados. Devuelve todos los posts procesados (evento y no-evento).

**Headers requeridos:**

| Header | Descripción |
|---|---|
| `X-DeepSeek-API-Key` | Token de API de DeepSeek (proporcionado por el cliente) |
| `Content-Type` | `application/json` |

**Query params opcionales (rango de fechas):**

| Parámetro | Tipo | Descripción |
|---|---|---|
| `dateFrom` | `datetime` | Inicio del rango de fechas de eventos válidos (inclusive). Si se omite, el rango queda abierto por el inicio. |
| `dateTo` | `datetime` | Fin del rango de fechas de eventos válidos (inclusive). Si se omite, el rango queda abierto por el fin. |

Cuando se indica un rango, un evento solo se considera válido si ocurre dentro de él:

- **Fecha concreta**: el día del evento debe caer en el rango.
- **Evento recurrente** (`todos los jueves`, `de lunes a viernes`): se incluye si alguna ocurrencia del patrón cae en el rango. Ej: un evento "de lunes 14 a jueves 17 de septiembre" es válido para un rango del 10 al 20 de septiembre, pero no para octubre.
- **Evento de varios días** (ferias, festivales): se incluye si el evento y el rango se solapan en cualquier día.
- Un evento que no cae en el rango **no se persiste** y se devuelve como no-evento (mismo flujo que si no se hubiera encontrado evento).

Ejemplos:

```
POST /api/events/recognize?dateFrom=2026-09-01&dateTo=2026-09-30
POST /api/events/recognize?dateFrom=2026-09-01            # solo límite inferior
POST /api/events/recognize?dateTo=2026-09-30              # solo límite superior
POST /api/events/recognize                                # sin rango: no se filtra
```

**Request body** (`application/json`):

```json
[
  {
    "account": "lacasadelaplaya",
    "post_id": "ABC123xyz",
    "caption": "Este sábado 19 de septiembre...",
    "datetime": "2026-07-28T14:30:00",
    "url": "https://www.instagram.com/p/ABC123xyz/",
    "image_url": "https://instagram.fmad3-1.fna.fbcdn.net/..."
  }
]
```

**Response 200:**

```json
{
  "totalPosts": 3,
  "eventsFound": 1,
  "events": [
    {
      "isEvent": true,
      "eventUniqueId": "EVT-20260728-A1B2C3D4",
      "title": "Fiesta de Verano",
      "eventDate": "2026-09-19T22:00:00",
      "eventDateDescription": "Sábado 19 de septiembre a las 22:00",
      "summary": "Fiesta de verano en la playa con DJ invitados...",
      "isRecurrent": false,
      "recurrenceType": null,
      "recurrenceDaysOfWeek": null,
      "recurrenceStartDate": null,
      "recurrenceEndDate": null,
      "account": "lacasadelaplaya",
      "postId": "ABC123xyz",
      "caption": "Este sábado 19 de septiembre...",
      "postDatetime": "2026-07-28T14:30:00",
      "url": "https://www.instagram.com/p/ABC123xyz/",
      "imageUrl": "https://...",
      "createdAt": "2026-07-28T15:00:00Z"
    },
    {
      "isEvent": false,
      "account": "cafe_madrid",
      "postId": "XYZ789abc",
      "caption": "Qué bien lo pasamos ayer...",
      "postDatetime": "2026-07-27T10:00:00",
      "url": "https://www.instagram.com/p/XYZ789abc/",
      "imageUrl": null,
      "createdAt": "2026-07-28T15:00:00Z"
    }
  ]
}
```

Ejemplo de evento recurrente ("todos los jueves desde el 10 de septiembre"):

```json
{
  "isEvent": true,
  "eventUniqueId": "EVT-20260728-B2C3D4E5",
  "title": "Techno Thursdays",
  "eventDate": "2026-09-10T23:00:00",
  "eventDateDescription": "Todos los jueves desde el 10 de septiembre de 2026",
  "summary": "Noche de techno todos los jueves.",
  "isRecurrent": true,
  "recurrenceType": "weekly",
  "recurrenceDaysOfWeek": "4",
  "recurrenceStartDate": "2026-09-10T00:00:00",
  "recurrenceEndDate": null,
  "account": "club_xyz",
  "postId": "DEF456uvw",
  "caption": "TECHNO THURSDAYS · Todos los jueves...",
  "postDatetime": "2026-09-01T12:00:00",
  "url": "https://www.instagram.com/p/DEF456uvw/",
  "imageUrl": null,
  "createdAt": "2026-09-01T13:00:00Z"
}
```

**Códigos de error:**

| Código | Significado |
|---|---|
| `400` | Body vacío o sin posts, o rango inválido (`dateFrom` posterior a `dateTo`) |
| `401` | Falta el header `X-DeepSeek-API-Key` |
| `500` | Error al parsear la respuesta del LLM |
| `502` | Error de comunicación con la API de DeepSeek |

---

### `GET /api/events/{eventUniqueId}`

Recupera un evento previamente reconocido por su identificador único.

**Parámetros de ruta:**

| Parámetro | Descripción |
|---|---|
| `eventUniqueId` | ID único del evento (formato: `EVT-{fecha}-{hash}`) |

**Response 200:**

```json
{
  "eventUniqueId": "EVT-20260728-A1B2C3D4",
  "title": "Fiesta de Verano",
  "eventDate": "2026-09-19T22:00:00",
  "eventDateDescription": "Sábado 19 de septiembre a las 22:00",
  "summary": "Fiesta de verano en la playa...",
  "isRecurrent": false,
  "recurrenceType": null,
  "recurrenceDaysOfWeek": null,
  "recurrenceStartDate": null,
  "recurrenceEndDate": null,
  "account": "lacasadelaplaya",
  "postId": "ABC123xyz",
  "caption": "Este sábado 19 de septiembre...",
  "postDatetime": "2026-07-28T14:30:00",
  "url": "https://www.instagram.com/p/ABC123xyz/",
  "imageUrl": "https://...",
  "createdAt": "2026-07-28T15:00:00Z"
}
```

**Response 404:**

```json
{
  "error": "Event not found",
  "detail": "No event found with unique ID 'EVT-20260728-A1B2C3D4'."
}
```

---

## Esquema de base de datos

### Tabla `EventRecords`

| Columna | Tipo | Restricciones | Descripción |
|---|---|---|---|
| `Id` | `int` | PK, IDENTITY | Clave primaria autoincremental |
| `EventUniqueId` | `nvarchar(50)` | NOT NULL, UNIQUE | ID generado: `EVT-{yyyyMMdd}-{SHA256[12]}` |
| `Title` | `nvarchar(300)` | NOT NULL | Título del evento extraído por el LLM |
| `EventDate` | `datetime2` | NULL | Fecha y hora del evento (ISO 8601) |
| `EventDateDescription` | `nvarchar(500)` | NULL | Descripción textual si la fecha no es concreta |
| `Summary` | `nvarchar(2000)` | NOT NULL | Resumen del evento en español (máx. 2 frases) |
| `IsRecurrent` | `bit` | NOT NULL | Si el evento se repite (patrón semanal o evento de varios días) |
| `RecurrenceType` | `nvarchar(20)` | NULL | `weekly` (se repite por días de la semana) o `daily` (cada día dentro de un rango) |
| `RecurrenceDaysOfWeek` | `nvarchar(50)` | NULL | Días de repetición separados por comas: 1=lunes ... 7=domingo. Ej: `1,2,3,4` |
| `RecurrenceStartDate` | `datetime2` | NULL | Primer día de la recurrencia o del rango de días |
| `RecurrenceEndDate` | `datetime2` | NULL | Último día de la recurrencia o del rango de días. NULL si es indefinida |
| `Account` | `nvarchar(200)` | NOT NULL, INDEX | Usuario de Instagram |
| `PostId` | `nvarchar(100)` | NOT NULL, UNIQUE | ID del post de Instagram |
| `Caption` | `nvarchar(4000)` | NOT NULL | Texto del caption del post |
| `PostDatetime` | `datetime2` | NULL | Fecha de publicación del post |
| `Url` | `nvarchar(500)` | NOT NULL | URL del post de Instagram |
| `ImageUrl` | `nvarchar(1000)` | NULL | URL de la imagen del post |
| `CreatedAt` | `datetime2` | NOT NULL, INDEX | Fecha de creación del registro en el sistema |

**Índices:**

| Índice | Columnas | Tipo |
|---|---|---|
| `PK_EventRecords` | `Id` | Clave primaria |
| `IX_EventRecords_EventUniqueId` | `EventUniqueId` | Único |
| `IX_EventRecords_PostId` | `PostId` | Único |
| `IX_EventRecords_Account` | `Account` | No único |
| `IX_EventRecords_EventDate` | `EventDate` | No único |
| `IX_EventRecords_CreatedAt` | `CreatedAt` | No único |

---

## Requisitos

### Desarrollo local

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Docker](https://docs.docker.com/get-docker/) + Docker Compose (para la base de datos)
- Una [API key de DeepSeek](https://platform.deepseek.com/)

### Solo Docker (recomendado para despliegue)

- Docker + Docker Compose
- API key de DeepSeek (proporcionada por el cliente en cada request)

---

## Configuración local (desarrollo)

### 1. Clonar el repositorio

```bash
git clone <url-del-repo>
cd event_recognizer
```

### 2. Configurar variables de entorno

Copia el archivo `.env` de ejemplo y edita los valores:

```bash
cp .env.example .env   # si existe .env.example, o edita .env directamente
```

Contenido mínimo del `.env`:

```env
SA_PASSWORD=TuPasswordSegura123!
```

### 3. Levantar la base de datos con Docker

```bash
docker compose up -d sqlserver
```

Esto levanta SQL Server 2022 Express en `localhost:1434`.

### 4. Configurar la cadena de conexión

Crea `EventRecognizer.Api/appsettings.Development.json` si no existe:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "EventRecognizer": "Debug"
    }
  },
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost,1434;Database=EventRecognizerDb;User Id=sa;Password=TuPasswordSegura123!;TrustServerCertificate=true;MultipleActiveResultSets=true"
  }
}
```

### 5. Aplicar migraciones (opcional — se aplican automáticamente al iniciar)

```bash
dotnet ef database update --project EventRecognizer.Api
```

### 6. Ejecutar la API

```bash
dotnet run --project EventRecognizer.Api
```

La API estará disponible en `http://localhost:5214`. Swagger UI se abre automáticamente en `http://localhost:5214/swagger`.

---

## Ejecución con Docker

Para levantar el stack completo (API + base de datos):

```bash
# Asegúrate de que el .env tenga SA_PASSWORD configurado
docker compose up -d
```

La API queda expuesta en `http://localhost:8081`.

### Servicios

| Servicio | Puerto host | Puerto contenedor | Descripción |
|---|---|---|---|
| `api` | 8081 | 8080 | API REST (.NET 8) |
| `sqlserver` | 1434 | 1433 | SQL Server 2022 Express |

### Datos persistentes

Los datos de SQL Server se almacenan en el volumen `sqlserver-data`. Para eliminar los datos:

```bash
docker compose down -v
```

---

## Despliegue en producción

El proyecto incluye un override de Docker Compose para producción que añade un túnel de Cloudflare para exponer la API de forma segura sin abrir puertos en el firewall.

### Requisitos adicionales

- Una cuenta de Cloudflare con un túnel configurado.
- El token del túnel en la variable `CLOUDFLARED_TUNNEL_TOKEN`.

### Configuración del `.env`

```env
SA_PASSWORD=PasswordMuySegura123!
CLOUDFLARED_TUNNEL_TOKEN=eyJhIjoi...
```

### Despliegue

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d
```

Esto añade el contenedor `cloudflared` que crea el túnel hacia Cloudflare automáticamente.

---

## Variables de entorno

| Variable | Requerida | Contexto | Descripción |
|---|---|---|---|
| `ConnectionStrings__DefaultConnection` | Sí | API | Cadena de conexión a SQL Server. Puede configurarse en `appsettings.json` o como variable de entorno. |
| `SA_PASSWORD` | Sí | Docker | Contraseña del usuario `sa` de SQL Server. |
| `CLOUDFLARED_TUNNEL_TOKEN` | Solo prod | Docker | Token del túnel de Cloudflare para exponer la API. |
| `ASPNETCORE_ENVIRONMENT` | No | API | Entorno de ejecución (`Development`, `Production`). Por defecto `Production`. |
| `X-DeepSeek-API-Key` | Sí | Cliente | API key de DeepSeek que el cliente envía por header en cada request. **No** se configura en el servidor. |

### Prioridad de la cadena de conexión

`Program.cs` resuelve la cadena de conexión en este orden:

1. `ConnectionStrings:DefaultConnection` en `appsettings.json` (o `appsettings.{Environment}.json`)
2. Variable de entorno `ConnectionStrings__DefaultConnection`
3. Si no se encuentra en ninguna fuente, la aplicación lanza una excepción y no inicia.

---

## Prompt del LLM y lógica de detección

El sistema utiliza un prompt de sistema en español que instruye a DeepSeek a clasificar posts con reglas específicas:

### Reglas de detección de eventos

- ✅ El post **debe** anunciar un evento futuro con fecha (concreta o recurrente).
- ✅ Posts tipo "todos los jueves" o "sábados de verano" → **sí** son eventos (fecha recurrente).
- ❌ Posts de agradecimiento post-evento → **no** son carteles de evento.
- ❌ Resúmenes/recaps de eventos pasados → **no** son carteles de evento.
- ❌ Posts tipo "soon" o "próximamente" sin detalles → **no** son carteles de evento.
- ❌ Anuncios de merchandising/pre-order → **no** son carteles de evento.

### Resolución de fechas aproximadas

- Si el cartel menciona un evento con nombre propio conocido ("Feria de Córdoba", "San Isidro", "WOMAD"…), el prompt **obliga** al LLM a buscar en su conocimiento la fecha o rango de fechas real de la edición anunciada y rellenar las fechas SÍ O SÍ, aunque el cartel no las diga explícitamente.
- Las fechas relativas ("este sábado", "el próximo viernes") se resuelven usando la fecha de publicación del post y, cuando se envía, el rango de fechas solicitado por el cliente.
- El rango solicitado se incluye en el prompt del usuario como contexto, indicando explícitamente al modelo que **no filtre** por él: el filtro final lo aplica el servidor de forma determinista.

### Eventos recurrentes

El prompt pide al LLM devolver el patrón de recurrencia:

| Campo | Significado |
|---|---|
| `is_recurrent` | Si el evento se repite en el tiempo |
| `recurrence_type` | `weekly` (días de la semana) o `daily` (cada día dentro de un rango, p. ej. ferias) |
| `recurrence_days_of_week` | Días de repetición: 1=lunes ... 7=domingo. "De lunes a jueves" → `[1,2,3,4]` |
| `recurrence_start_date` | Primer día de la recurrencia o del rango |
| `recurrence_end_date` | Último día, o `null` si es indefinida ("todos los jueves") |

El servidor evalúa si el evento cae en el rango solicitado con `RecurrenceEvaluator`:
- Evento con fecha concreta → el día debe estar en el rango.
- Recurrencia semanal → se comprueba si algún día del patrón cae dentro de la intersección entre la ventana de recurrencia y el rango solicitado.
- Evento de varios días (feria, festival) → basta con que ventana y rango se solapen.
- Evento sin ninguna fecha verificable → no se considera válido para el rango.

### Respuesta esperada del LLM

```json
{
  "results": [
    {
      "is_event": true,
      "title": "Título del evento",
      "event_date": "2026-09-19T22:00:00",
      "event_date_description": "Sábado 19 de septiembre",
      "summary": "Fiesta de verano con DJ en la playa",
      "is_recurrent": false,
      "recurrence_type": null,
      "recurrence_days_of_week": null,
      "recurrence_start_date": null,
      "recurrence_end_date": null
    }
  ]
}
```

### Parámetros de la llamada a DeepSeek

| Parámetro | Valor |
|---|---|
| Modelo | `deepseek-chat` |
| `temperature` | 0.3 |
| `max_tokens` | 4096 |
| `response_format` | `json_object` |
| Timeout HTTP | 120 segundos |

### Generación de `EventUniqueId`

El ID único de evento se genera combinando `PostId`, `Account` y `Title`, aplicando SHA256 y tomando los primeros 12 caracteres del hash:

```
EVT-{yyyyMMdd}-{SHA256(PostId + Account + Title)[0..12]}
```

Ejemplo: `EVT-20260728-A1B2C3D4E5F6`

---

## Seguridad

### Autenticación

La API **no** implementa autenticación de usuarios (JWT, OAuth, etc.). En su lugar:

- El endpoint `POST /api/events/recognize` requiere que el **cliente** proporcione su propia API key de DeepSeek mediante el header `X-DeepSeek-API-Key`. La clave viaja del cliente a DeepSeek; el servidor no la almacena.
- El endpoint `GET /api/events/{eventUniqueId}` es público.

### Secretos en el repositorio

El archivo `.env` está listado en `.gitignore`, pero el `.env` actual del repositorio contiene secretos reales. Para corregir esto:

```bash
# Eliminar el .env del seguimiento de git sin borrar el archivo local
git rm --cached .env
git commit -m "fix: remove .env from git tracking"

# Crear un .env.example sin valores reales como plantilla
```

### Base de datos

- SQL Server se expone en `localhost:1434` en desarrollo. En producción, con Cloudflare Tunnel, solo la API es accesible — la base de datos permanece interna en la red de Docker.
- La contraseña de `sa` se configura mediante variable de entorno (`SA_PASSWORD`).

---

## Migraciones de base de datos

Las migraciones se aplican automáticamente al iniciar la aplicación mediante `db.Database.Migrate()` en `Program.cs`. No es necesario ejecutar comandos manualmente en despliegues.

### Crear una nueva migración (desarrollo)

```bash
dotnet ef migrations add NombreDeLaMigracion \
    --project EventRecognizer.Api \
    --startup-project EventRecognizer.Api
```

### Aplicar migraciones manualmente

```bash
dotnet ef database update \
    --project EventRecognizer.Api \
    --startup-project EventRecognizer.Api
```

### Historial de migraciones

| Migración | Descripción |
|---|---|
| `20260902013644_InitialCreate` | Creación de la tabla `EventRecords` con los índices y las columnas de recurrencia (`IsRecurrent`, `RecurrenceType`, `RecurrenceDaysOfWeek`, `RecurrenceStartDate`, `RecurrenceEndDate`). |

> ⚠️ **La migración inicial se regeneró** al añadir las columnas de recurrencia. Las bases de datos existentes **deben recrearse**: borra la base de datos (o el volumen `sqlserver-data` con `docker compose down -v`) y deja que la aplicación la cree de nuevo al arrancar.

---

## Tests

El repositorio incluye el proyecto `EventRecognizer.Api.Tests` (xUnit) con tests unitarios para `RecurrenceEvaluator`, `DeepSeekService` (con `HttpMessageHandler` simulado), `EventService` (con SQLite en memoria) y `EventsController`.

```bash
dotnet test
```

---

## Licencia

_Proyecto privado. Todos los derechos reservados._
