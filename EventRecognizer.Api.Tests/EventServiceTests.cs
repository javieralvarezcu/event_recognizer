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
