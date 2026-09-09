using EventRecognizer.Api.Data;
using EventRecognizer.Api.Models;
using EventRecognizer.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventRecognizer.Api.Tests;

public class EventServiceTests
{
    private static EventService CreateService(
        AppDbContext db,
        Func<List<InstagramPost>, string, List<PostAnalysisResult>> analyze)
        => new(new FakeDeepSeekService(analyze), db, NullLogger<EventService>.Instance);

    [Fact]
    public async Task RecognizeEventsAsync_PersistsOnlyEventPosts_AndReturnsAllPosts()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost>
        {
            TestData.CreatePost("p1", "cartel de concierto"),
            TestData.CreatePost("p2", "foto random")
        };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Concierto X", "2026-09-19T22:00:00", "Sábado 19", "Resumen X"),
            TestData.NonEventResult()
        });

        var response = await service.RecognizeEventsAsync(posts, "key");

        Assert.Equal(2, response.TotalPosts);
        Assert.Equal(1, response.EventsFound);
        Assert.Equal(2, response.Events.Count);

        var eventDto = response.Events[0];
        Assert.True(eventDto.IsEvent);
        Assert.Equal("Concierto X", eventDto.Title);
        Assert.Equal("p1", eventDto.PostId);
        Assert.StartsWith("EVT-", eventDto.EventUniqueId);
        // Parsed the same way the service does, so the assertion is timezone-independent.
        Assert.Equal(DateTime.Parse("2026-09-19T22:00:00").ToUniversalTime(), eventDto.EventDate);
        Assert.Equal("Sábado 19", eventDto.EventDateDescription);
        Assert.Equal("Resumen X", eventDto.Summary);
        Assert.Equal(posts[0].Caption, eventDto.Caption);
        Assert.Equal(posts[0].Url, eventDto.Url);
        Assert.Equal(posts[0].ImageUrl, eventDto.ImageUrl);
        Assert.Equal(posts[0].Datetime, eventDto.PostDatetime);

        var nonEvent = response.Events[1];
        Assert.False(nonEvent.IsEvent);
        Assert.Null(nonEvent.EventUniqueId);
        Assert.Null(nonEvent.Title);
        Assert.Null(nonEvent.EventDate);
        Assert.Equal("p2", nonEvent.PostId);

        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithDuplicatePostIdInBatch_ProcessesItOnlyOnce()
    {
        using var fixture = new TestDatabase();
        var posts = Enumerable.Range(0, 5)
            .Select(i => TestData.CreatePost("p1", $"repetición {i}"))
            .ToList();
        var service = CreateService(fixture.Db, (_, _) =>
            Enumerable.Range(0, posts.Count).Select(_ => TestData.EventResult()).ToList());

        var response = await service.RecognizeEventsAsync(posts, "key");

        Assert.Equal(posts.Count, response.TotalPosts);
        Assert.Equal(1, response.EventsFound);
        Assert.Single(response.Events);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithPostAlreadyInDb_SkipsInsertAndReturnsDbRecord()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(new EventRecord
        {
            EventUniqueId = "EVT-EXISTING-000000000001",
            Title = "Evento ya guardado",
            Summary = "Resumen existente",
            Account = "test.account",
            PostId = "p1",
            Caption = "cartel original",
            Url = "https://instagram.com/p/p1",
            CreatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Título nuevo del LLM")
        });

        var response = await service.RecognizeEventsAsync(
            new List<InstagramPost> { TestData.CreatePost("p1") }, "key");

        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
        Assert.Equal(1, response.EventsFound);
        // The response must come from the DB record, not from the new LLM analysis.
        Assert.Equal("EVT-EXISTING-000000000001", response.Events[0].EventUniqueId);
        Assert.Equal("Evento ya guardado", response.Events[0].Title);
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithSaveConflict_DetachesAndRetriesSuccessfully()
    {
        using var fixture = new TestDatabase(options => new ThrowingOnceDbContext(options));
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Evento")
        });

        var response = await service.RecognizeEventsAsync(
            new List<InstagramPost> { TestData.CreatePost("p1") }, "key");

        // First save throws (simulated unique-key race), the retry succeeds.
        Assert.Equal(2, ((ThrowingOnceDbContext)fixture.Db).SaveCalls);
        Assert.Equal(1, response.EventsFound);
        Assert.Equal("Evento", response.Events[0].Title);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithMissingAnalysisFields_AppliesDefaults()
    {
        using var fixture = new TestDatabase();
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            new PostAnalysisResult
            {
                IsEvent = true,
                Title = null,
                EventDate = "not-a-date",
                EventDateDescription = null,
                Summary = null
            }
        });

        var response = await service.RecognizeEventsAsync(
            new List<InstagramPost> { TestData.CreatePost("p1") }, "key");

        var eventDto = response.Events[0];
        Assert.Equal("Sin título", eventDto.Title);
        Assert.Equal("Sin resumen", eventDto.Summary);
        Assert.Null(eventDto.EventDate);
        Assert.Null(eventDto.EventDateDescription);
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithVeryLongCaption_TruncatesToMaxLength()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost>
        {
            TestData.CreatePost("p1", new string('a', 5000))
        };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult()
        });

        var response = await service.RecognizeEventsAsync(posts, "key");

        Assert.Equal(4000, response.Events[0].Caption!.Length);
        Assert.Equal(4000, (await fixture.Db.EventRecords.SingleAsync()).Caption.Length);
    }

    [Fact]
    public async Task GetEventByUniqueIdAsync_ReturnsEvent_WhenFound()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.Add(new EventRecord
        {
            EventUniqueId = "EVT-1",
            Title = "Evento uno",
            Summary = "Resumen uno",
            Account = "test.account",
            PostId = "p1"
        });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>());

        var result = await service.GetEventByUniqueIdAsync("EVT-1");

        Assert.NotNull(result);
        Assert.Equal("EVT-1", result.EventUniqueId);
        Assert.Equal("Evento uno", result.Title);
        Assert.Equal("p1", result.PostId);
    }

    [Fact]
    public async Task GetEventByUniqueIdAsync_ReturnsNull_WhenNotFound()
    {
        using var fixture = new TestDatabase();
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>());

        var result = await service.GetEventByUniqueIdAsync("EVT-missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllEventsAsync_WithNoEvents_ReturnsEmptyList()
    {
        using var fixture = new TestDatabase();
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>());

        var result = await service.GetAllEventsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllEventsAsync_ReturnsAllEventsWithFullDetails()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            new EventRecord
            {
                EventUniqueId = "EVT-1",
                Title = "Evento simple",
                EventDate = new DateTime(2026, 9, 19, 20, 0, 0, DateTimeKind.Utc),
                EventDateDescription = "Sábado 19",
                Summary = "Resumen simple",
                Account = "test.account",
                PostId = "p1",
                Caption = "cartel",
                Url = "https://instagram.com/p/p1",
                ImageUrl = "https://cdn.example.com/p1.jpg",
                CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc)
            },
            new EventRecord
            {
                EventUniqueId = "EVT-2",
                Title = "Evento semanal",
                Summary = "Resumen semanal",
                Account = "test.account",
                PostId = "p2",
                IsRecurrent = true,
                RecurrenceType = "weekly",
                RecurrenceDaysOfWeek = "1,2,3,4",
                RecurrenceStartDate = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc)
            });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>());

        var result = await service.GetAllEventsAsync();

        Assert.Equal(2, result.Count);

        var simple = result.Single(e => e.EventUniqueId == "EVT-1");
        Assert.Equal("Evento simple", simple.Title);
        Assert.Equal("Sábado 19", simple.EventDateDescription);
        Assert.Equal("Resumen simple", simple.Summary);
        Assert.Equal("p1", simple.PostId);
        Assert.Equal("cartel", simple.Caption);
        Assert.Equal("https://instagram.com/p/p1", simple.Url);
        Assert.Equal("https://cdn.example.com/p1.jpg", simple.ImageUrl);

        var weekly = result.Single(e => e.EventUniqueId == "EVT-2");
        Assert.True(weekly.IsRecurrent);
        Assert.Equal("weekly", weekly.RecurrenceType);
        Assert.Equal("1,2,3,4", weekly.RecurrenceDaysOfWeek);
        Assert.NotNull(weekly.RecurrenceStartDate);
    }

    [Fact]
    public async Task GetAllEventsAsync_OrdersByEffectiveDateThenCreatedAt()
    {
        using var fixture = new TestDatabase();
        fixture.Db.EventRecords.AddRange(
            new EventRecord
            {
                EventUniqueId = "EVT-date-sep10",
                Title = "Con fecha",
                EventDate = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
                Account = "test.account",
                PostId = "p1",
                CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc)
            },
            new EventRecord
            {
                // No EventDate, but recurrence start earlier: must sort first (coalesce).
                EventUniqueId = "EVT-recur-sep05",
                Title = "Solo inicio recurrencia",
                Account = "test.account",
                PostId = "p2",
                RecurrenceStartDate = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc)
            },
            new EventRecord
            {
                // Same date as the first, later CreatedAt: sorts after it.
                EventUniqueId = "EVT-date-sep10-late",
                Title = "Con fecha (creado después)",
                EventDate = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
                Account = "test.account",
                PostId = "p3",
                CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)
            },
            new EventRecord
            {
                // No computable date at all: sorts last.
                EventUniqueId = "EVT-no-date",
                Title = "Sin fecha",
                Account = "test.account",
                PostId = "p4",
                CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc)
            });
        await fixture.Db.SaveChangesAsync();

        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>());

        var result = await service.GetAllEventsAsync();

        Assert.Equal(
            new[] { "EVT-recur-sep05", "EVT-date-sep10", "EVT-date-sep10-late", "EVT-no-date" },
            result.Select(e => e.EventUniqueId).ToArray());
    }

    private static DateRange Range(string? from, string? to)
        => new(from == null ? null : DateTime.Parse(from),
               to == null ? null : DateTime.Parse(to));

    [Fact]
    public async Task RecognizeEventsAsync_WithRange_PassesRangeToDeepSeekService()
    {
        using var fixture = new TestDatabase();
        var fake = new FakeDeepSeekService((_, _) => new List<PostAnalysisResult>
        {
            TestData.NonEventResult()
        });
        var service = new EventService(fake, fixture.Db, NullLogger<EventService>.Instance);
        var range = Range("2026-09-01", "2026-09-30");

        await service.RecognizeEventsAsync(
            new List<InstagramPost> { TestData.CreatePost("p1") }, "key", range);

        Assert.Equal(range, fake.LastDateRange);
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithRange_ExcludesEventOutsideRange()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Evento de octubre", "2026-10-01T20:00:00")
        });

        var response = await service.RecognizeEventsAsync(posts, "key", Range("2026-09-01", "2026-09-30"));

        // Out-of-range events follow the flow as if no event had been found.
        Assert.Equal(1, response.TotalPosts);
        Assert.Equal(0, response.EventsFound);
        Assert.False(response.Events[0].IsEvent);
        Assert.Equal(0, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithRange_IncludesEventInsideRange()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.EventResult("Evento de septiembre", "2026-09-19T22:00:00")
        });

        var response = await service.RecognizeEventsAsync(posts, "key", Range("2026-09-01", "2026-09-30"));

        Assert.Equal(1, response.EventsFound);
        Assert.True(response.Events[0].IsEvent);
        Assert.Equal("Evento de septiembre", response.Events[0].Title);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithRange_IncludesRecurrentEventWithinRange()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };
        // "De lunes 14 a jueves 17 de septiembre".
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.WeeklyResult(new List<int> { 1, 2, 3, 4 }, "2026-09-14", "2026-09-17")
        });

        var response = await service.RecognizeEventsAsync(posts, "key", Range("2026-09-10", "2026-09-20"));

        var eventDto = response.Events[0];
        Assert.True(eventDto.IsEvent);
        Assert.True(eventDto.IsRecurrent);
        Assert.Equal("weekly", eventDto.RecurrenceType);
        Assert.Equal("1,2,3,4", eventDto.RecurrenceDaysOfWeek);
        Assert.NotNull(eventDto.RecurrenceStartDate);
        Assert.NotNull(eventDto.RecurrenceEndDate);

        var stored = await fixture.Db.EventRecords.SingleAsync();
        Assert.True(stored.IsRecurrent);
        Assert.Equal("weekly", stored.RecurrenceType);
        Assert.Equal("1,2,3,4", stored.RecurrenceDaysOfWeek);
        Assert.NotNull(stored.RecurrenceStartDate);
        Assert.NotNull(stored.RecurrenceEndDate);
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithRange_ExcludesRecurrenceOutsideRange()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.WeeklyResult(new List<int> { 1, 2, 3, 4 }, "2026-09-14", "2026-09-17")
        });

        var response = await service.RecognizeEventsAsync(posts, "key", Range("2026-10-01", "2026-10-31"));

        Assert.Equal(0, response.EventsFound);
        Assert.False(response.Events[0].IsEvent);
        Assert.Equal(0, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithRange_OpenEndedRecurrence_MatchesLaterRange()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };
        // "Todos los jueves desde el 10 de septiembre" — range in October (contains Thursdays).
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.WeeklyResult(new List<int> { 4 }, "2026-09-10")
        });

        var response = await service.RecognizeEventsAsync(posts, "key", Range("2026-10-01", "2026-10-31"));

        Assert.Equal(1, response.EventsFound);
        Assert.True(response.Events[0].IsEvent);
        Assert.True(response.Events[0].IsRecurrent);
        Assert.Equal(1, await fixture.Db.EventRecords.CountAsync());
    }

    [Fact]
    public async Task RecognizeEventsAsync_WithoutRange_PersistsRecurrenceColumns()
    {
        using var fixture = new TestDatabase();
        var posts = new List<InstagramPost> { TestData.CreatePost("p1") };
        var service = CreateService(fixture.Db, (_, _) => new List<PostAnalysisResult>
        {
            TestData.DailyRangeResult("2026-05-23", "2026-05-30", "Feria de Córdoba")
        });

        var response = await service.RecognizeEventsAsync(posts, "key");

        var eventDto = response.Events[0];
        Assert.True(eventDto.IsEvent);
        Assert.True(eventDto.IsRecurrent);
        Assert.Equal("daily", eventDto.RecurrenceType);
        Assert.Null(eventDto.RecurrenceDaysOfWeek);

        var stored = await fixture.Db.EventRecords.SingleAsync();
        Assert.True(stored.IsRecurrent);
        Assert.Equal("daily", stored.RecurrenceType);
        Assert.Null(stored.RecurrenceDaysOfWeek);
        Assert.NotNull(stored.RecurrenceStartDate);
        Assert.NotNull(stored.RecurrenceEndDate);
    }

    /// <summary>
    /// In-memory SQLite database for a single test. The DB lives on the open connection,
    /// so the connection must stay open for the whole test and is disposed here.
    /// </summary>
    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;

        public AppDbContext Db { get; }

        public TestDatabase(Func<DbContextOptions<AppDbContext>, AppDbContext>? contextFactory = null)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;

            Db = contextFactory?.Invoke(options) ?? new AppDbContext(options);
            Db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    /// <summary>
    /// AppDbContext whose first SaveChangesAsync throws, simulating the race where a
    /// concurrent request inserts the same PostId between the duplicate check and the save.
    /// </summary>
    private sealed class ThrowingOnceDbContext : AppDbContext
    {
        public int SaveCalls { get; private set; }

        public ThrowingOnceDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (SaveCalls == 1)
                throw new DbUpdateException("Simulated unique constraint violation.", (Exception?)null);
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
