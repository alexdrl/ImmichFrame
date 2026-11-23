using System.Net;
using System.Text;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace ImmichFrame.Core.Tests.Services;

/// <summary>
/// Runs TodoistTaskService against a fake HTTP handler and a fixed clock. "Today" is
/// 2026-11-22 at 12:00 in a UTC+1 zone.
/// </summary>
[TestFixture]
public class TodoistTaskServiceTests
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    private Mock<IServerBehaviorSettings> _settings = null!;
    private FixedTimeProvider _time = null!;
    private RecordingHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _settings = new Mock<IServerBehaviorSettings>();
        _settings.SetupGet(s => s.TodoistApiToken).Returns("secret-token");
        _settings.SetupGet(s => s.TodoistProjectId).Returns("6Jf8VQXxpwv56VQ7");
        _settings.SetupGet(s => s.TodoistShowOverdue).Returns(true);
        _settings.SetupGet(s => s.TodoistMaxTasks).Returns(8);
        _settings.SetupGet(s => s.TodoistRefreshMinutes).Returns(5);
        _time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 22, 11, 0, 0, TimeSpan.Zero), Zone);
    }

    private TodoistTaskService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _handler = new RecordingHandler(respond);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(TodoistTaskService.HttpClientName)).Returns(() => new HttpClient(_handler));
        return new TodoistTaskService(_settings.Object, factory.Object, NullLogger<TodoistTaskService>.Instance, _time);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Todoist(string tasksJson, string projectName = "Casa") =>
        request => request.RequestUri!.AbsolutePath.StartsWith("/api/v1/projects/")
            ? Json($$"""{"id":"6Jf8VQXxpwv56VQ7","name":"{{projectName}}"}""")
            : Json($$"""{"results":[{{tasksJson}}],"next_cursor":null}""");

    private static string TaskJson(string id, string content, string? due, int priority = 1, int childOrder = 1,
        bool recurring = false, string labels = "", bool isChecked = false) =>
        $$"""
        {"id":"{{id}}","content":"{{content}}","priority":{{priority}},"child_order":{{childOrder}},
         "checked":{{(isChecked ? "true" : "false")}},"is_deleted":false,"labels":[{{labels}}],
         "due":{{(due == null ? "null" : $$"""{"date":"{{due}}","is_recurring":{{(recurring ? "true" : "false")}},"string":"x","lang":"en","timezone":null}""")}}}
        """;

    [TestCase(null, "6Jf8VQXxpwv56VQ7")]
    [TestCase("secret-token", null)]
    [TestCase(" ", "6Jf8VQXxpwv56VQ7")]
    public async Task NotConfigured_IsDisabled_AndSendsNoRequest(string? token, string? projectId)
    {
        _settings.SetupGet(s => s.TodoistApiToken).Returns(token);
        _settings.SetupGet(s => s.TodoistProjectId).Returns(projectId);
        var service = CreateService(_ => throw new AssertionException("No request expected"));

        var result = await service.GetTasks();

        Assert.That(result.Enabled, Is.False);
        Assert.That(_handler.Requests, Is.Empty);
    }

    [Test]
    public async Task KeepsTodayAndOverdue_InOrder_AndDropsTheRest()
    {
        var service = CreateService(Todoist(string.Join(",",
            TaskJson("future", "Next week", "2026-11-29"),
            TaskJson("nodue", "Someday", null),
            TaskJson("done", "Done today", "2026-11-22", isChecked: true),
            TaskJson("today-p4", "Water the plants", "2026-11-22", priority: 1),
            TaskJson("today-p1", "Pay the water bill", "2026-11-22", priority: 4),
            TaskJson("today-18", "Take out the bins", "2026-11-22T18:00:00", recurring: true),
            TaskJson("today-10", "Buy nappies", "2026-11-22T10:00:00", labels: "\"shopping\""),
            TaskJson("overdue-1", "Call the doctor", "2026-11-21"),
            TaskJson("overdue-3", "Book the ITV", "2026-11-19", priority: 3))));

        var result = await service.GetTasks();

        Assert.Multiple(() =>
        {
            Assert.That(result.Enabled, Is.True);
            Assert.That(result.ProjectName, Is.EqualTo("Casa"));
            Assert.That(result.Tasks.Select(t => t.Id), Is.EqualTo(new[]
            {
                "overdue-3", "overdue-1", "today-10", "today-18", "today-p1", "today-p4"
            }));
            Assert.That(result.HiddenCount, Is.EqualTo(0));

            var overdue = result.Tasks[0];
            Assert.That(overdue.IsOverdue, Is.True);
            Assert.That(overdue.DaysOverdue, Is.EqualTo(3));
            Assert.That(overdue.Priority, Is.EqualTo(2), "API priority 3 is P2 in the app");

            var timed = result.Tasks[2];
            Assert.That(timed.IsOverdue, Is.False);
            Assert.That(timed.DaysOverdue, Is.EqualTo(0));
            Assert.That(timed.DueDate, Is.EqualTo("2026-11-22"));
            Assert.That(timed.DueTime, Is.EqualTo("10:00"));
            Assert.That(timed.Labels, Is.EqualTo(new[] { "shopping" }));

            Assert.That(result.Tasks[3].IsRecurring, Is.True);
            Assert.That(result.Tasks[4].Priority, Is.EqualTo(1), "API priority 4 is P1 in the app");
            Assert.That(result.Tasks[5].DueTime, Is.Null);
        });
    }

    [Test]
    public async Task ShowOverdueOff_DropsOverdueTasks()
    {
        _settings.SetupGet(s => s.TodoistShowOverdue).Returns(false);
        var service = CreateService(Todoist(string.Join(",",
            TaskJson("overdue", "Call the doctor", "2026-11-21"),
            TaskJson("today", "Buy nappies", "2026-11-22"))));

        var result = await service.GetTasks();

        Assert.That(result.Tasks.Select(t => t.Id), Is.EqualTo(new[] { "today" }));
    }

    [Test]
    public async Task MaxTasks_CutsTheList_AndCountsTheRest()
    {
        _settings.SetupGet(s => s.TodoistMaxTasks).Returns(2);
        var service = CreateService(Todoist(string.Join(",",
            TaskJson("a", "A", "2026-11-20"),
            TaskJson("b", "B", "2026-11-22"),
            TaskJson("c", "C", "2026-11-22"),
            TaskJson("d", "D", "2026-11-22"))));

        var result = await service.GetTasks();

        Assert.That(result.Tasks.Select(t => t.Id), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(result.HiddenCount, Is.EqualTo(2));
    }

    [Test]
    public async Task UtcDueTime_IsShownInTheServerTimeZone()
    {
        // 23:30 UTC on the 21st is 00:30 on the 22nd at UTC+1: due today, not overdue.
        var service = CreateService(Todoist(TaskJson("utc", "Late call", "2026-11-21T23:30:00Z")));

        var task = (await service.GetTasks()).Tasks.Single();

        Assert.That(task.DueDate, Is.EqualTo("2026-11-22"));
        Assert.That(task.DueTime, Is.EqualTo("00:30"));
        Assert.That(task.IsOverdue, Is.False);
    }

    [Test]
    public async Task MarkdownLinks_AreShownAsTheirText()
    {
        var service = CreateService(Todoist(TaskJson("md", "Read [the manual](https://example.com/manual)", "2026-11-22")));

        var task = (await service.GetTasks()).Tasks.Single();

        Assert.That(task.Content, Is.EqualTo("Read the manual"));
    }

    [Test]
    public async Task SendsTheTokenAndProjectId_AndFollowsTheCursor()
    {
        var pages = new Queue<string>(new[]
        {
            $$"""{"results":[{{TaskJson("p1", "Page one", "2026-11-22")}}],"next_cursor":"abc=="}""",
            $$"""{"results":[{{TaskJson("p2", "Page two", "2026-11-22")}}],"next_cursor":null}""",
        });
        var authHeaders = new List<string?>();
        var service = CreateService(request =>
        {
            authHeaders.Add(request.Headers.Authorization?.ToString());
            return request.RequestUri!.AbsolutePath.StartsWith("/api/v1/projects/")
                ? Json("""{"name":"Casa"}""")
                : Json(pages.Dequeue());
        });

        var result = await service.GetTasks();

        Assert.Multiple(() =>
        {
            Assert.That(result.Tasks.Select(t => t.Id), Is.EqualTo(new[] { "p1", "p2" }));
            Assert.That(_handler.Requests[0].ToString(),
                Is.EqualTo("https://api.todoist.com/api/v1/tasks?project_id=6Jf8VQXxpwv56VQ7&limit=200"));
            Assert.That(_handler.Requests[1].Query, Does.Contain("cursor=abc%3D%3D"));
            Assert.That(_handler.Requests[2].AbsolutePath, Is.EqualTo("/api/v1/projects/6Jf8VQXxpwv56VQ7"));
            Assert.That(authHeaders, Has.All.EqualTo("Bearer secret-token"));
        });
    }

    [Test]
    public async Task KeepsTheResult_UntilTheRefreshInterval()
    {
        var service = CreateService(Todoist(TaskJson("a", "A", "2026-11-22")));

        await service.GetTasks();
        _time.Advance(TimeSpan.FromMinutes(4));
        await service.GetTasks();
        Assert.That(_handler.Requests, Has.Count.EqualTo(2), "tasks + project, once");

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.GetTasks();
        Assert.That(_handler.Requests, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task ChangedProjectId_ReadsAgainAtOnce()
    {
        var service = CreateService(Todoist(TaskJson("a", "A", "2026-11-22")));
        await service.GetTasks();

        _settings.SetupGet(s => s.TodoistProjectId).Returns("otherProject");
        await service.GetTasks();

        Assert.That(_handler.Requests[2].Query, Does.Contain("project_id=otherProject"));
    }

    [Test]
    public async Task ApiError_AfterAGoodRead_ServesTheLastTasks()
    {
        var fail = false;
        var good = Todoist(TaskJson("a", "A", "2026-11-22"));
        var service = CreateService(request => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : good(request));
        await service.GetTasks();

        fail = true;
        _time.Advance(TimeSpan.FromMinutes(10));
        var result = await service.GetTasks();

        Assert.That(result.Tasks.Select(t => t.Id), Is.EqualTo(new[] { "a" }));
        Assert.That(result.ProjectName, Is.EqualTo("Casa"));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task ApiError_WithoutAnEarlierRead_ReturnsNoTasks(HttpStatusCode status)
    {
        var service = CreateService(_ => new HttpResponseMessage(status));

        var result = await service.GetTasks();

        Assert.That(result.Enabled, Is.True);
        Assert.That(result.Tasks, Is.Empty);
        Assert.That(result.ProjectName, Is.Null);
    }

    [Test]
    public async Task ProjectNameError_StillShowsTheTasks()
    {
        var tasks = Todoist(TaskJson("a", "A", "2026-11-22"));
        var service = CreateService(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v1/projects/")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : tasks(request));

        var result = await service.GetTasks();

        Assert.That(result.Tasks, Has.Count.EqualTo(1));
        Assert.That(result.ProjectName, Is.Null);
    }

    [TestCase(90, 90)]
    [TestCase(0, 0)]
    [TestCase(-5, 0)]
    [TestCase(150, 100)]
    public async Task Opacity_IsPassedToTheClient_InsideZeroToHundred(int setting, int expected)
    {
        _settings.SetupGet(s => s.TodoistOpacity).Returns(setting);
        var service = CreateService(Todoist(TaskJson("a", "A", "2026-11-22")));

        var result = await service.GetTasks();

        Assert.That(result.Opacity, Is.EqualTo(expected));
    }

    [Test]
    public async Task HideWhenEmpty_IsPassedToTheClient()
    {
        _settings.SetupGet(s => s.TodoistHideWhenEmpty).Returns(true);
        var service = CreateService(Todoist(""));

        var result = await service.GetTasks();

        Assert.That(result.HideWhenEmpty, Is.True);
        Assert.That(result.Tasks, Is.Empty);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override TimeZoneInfo LocalTimeZone => zone;

        public void Advance(TimeSpan by) => _utcNow += by;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
