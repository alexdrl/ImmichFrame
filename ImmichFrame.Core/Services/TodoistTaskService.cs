using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Services;

/// <summary>
/// Reads the active tasks of one Todoist project with the Todoist API v1 and keeps the ones due
/// today or before. The API token stays on the server: the client gets only the task list.
/// </summary>
/// <remarks>
/// The tasks are read by project id (GET /tasks?project_id=), not with the /tasks/filter endpoint:
/// a filter query names the project by its name, so a rename would break it.
/// </remarks>
public class TodoistTaskService : ITodoistService
{
    /// <summary>The named HttpClient. Program.cs sets its timeout and turns off redirects.</summary>
    public const string HttpClientName = "Todoist";

    internal const string BaseUrl = "https://api.todoist.com/api/v1/";

    // 200 is the API maximum page size, so this reads up to 2000 tasks.
    private const int PageSize = 200;
    private const int MaxPages = 10;
    private const int MaxTasksLimit = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)\s]+\)", RegexOptions.Compiled);

    private readonly IServerBehaviorSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TodoistTaskService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Snapshot? _snapshot;

    public TodoistTaskService(
        IServerBehaviorSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<TodoistTaskService> logger,
        TimeProvider? timeProvider = null)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TodoistTasks> GetTasks(CancellationToken cancellationToken = default)
    {
        var token = _settings.TodoistApiToken?.Trim();
        var projectId = _settings.TodoistProjectId?.Trim();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(projectId))
        {
            return new TodoistTasks { Enabled = false };
        }

        var source = new Source(token, projectId);
        var snapshot = await GetSnapshot(source, cancellationToken);
        return Select(snapshot);
    }

    private async Task<Snapshot?> GetSnapshot(Source source, CancellationToken cancellationToken)
    {
        if (IsCurrent(_snapshot, source))
        {
            return _snapshot;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another request can have refreshed it while this one waited.
            var previous = _snapshot;
            if (IsCurrent(previous, source))
            {
                return previous;
            }

            try
            {
                var tasks = await LoadTasks(source, cancellationToken);
                var projectName = await LoadProjectName(source, cancellationToken);
                _snapshot = new Snapshot(source, _timeProvider.GetUtcNow(), tasks, projectName);
                return _snapshot;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTodoistFailure(ex))
            {
                if (previous != null && previous.Source.Equals(source))
                {
                    // Keep the last good list. Its age is not reset, so the next request tries again.
                    _logger.LogWarning("Todoist request failed ({Reason}); showing the tasks read at {FetchedAt:u}",
                        Describe(ex), previous.FetchedAt);
                    return previous;
                }

                _logger.LogWarning("Todoist request failed ({Reason}); no tasks to show", Describe(ex));
                return null;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsCurrent(Snapshot? snapshot, Source source)
    {
        if (snapshot == null || !snapshot.Source.Equals(source))
        {
            return false;
        }

        var refresh = TimeSpan.FromMinutes(Math.Clamp(_settings.TodoistRefreshMinutes, 1, 1440));
        return _timeProvider.GetUtcNow() - snapshot.FetchedAt < refresh;
    }

    private async Task<List<ApiTask>> LoadTasks(Source source, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var tasks = new List<ApiTask>();
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{BaseUrl}tasks?project_id={Uri.EscapeDataString(source.ProjectId)}&limit={PageSize}";
            if (cursor != null)
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            var body = await Get<ApiPage>(client, url, source.Token, cancellationToken);
            tasks.AddRange(body.Results);

            cursor = body.NextCursor;
            if (string.IsNullOrEmpty(cursor))
            {
                return tasks;
            }
        }

        _logger.LogWarning("Todoist project {ProjectId} has more than {Count} active tasks; only the first {Count} are read",
            source.ProjectId, PageSize * MaxPages, PageSize * MaxPages);
        return tasks;
    }

    private async Task<string?> LoadProjectName(Source source, CancellationToken cancellationToken)
    {
        // The name is only the header of the overlay: when it fails, the tasks still show.
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var project = await Get<ApiProject>(client,
                $"{BaseUrl}projects/{Uri.EscapeDataString(source.ProjectId)}", source.Token, cancellationToken);
            return string.IsNullOrWhiteSpace(project.Name) ? null : project.Name;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTodoistFailure(ex))
        {
            _logger.LogDebug("Todoist project name not read ({Reason})", Describe(ex));
            return null;
        }
    }

    private static async Task<T> Get<T>(HttpClient client, string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new TodoistApiException((int)response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new JsonException("Empty response");
    }

    private TodoistTasks Select(Snapshot? snapshot)
    {
        var result = new TodoistTasks
        {
            Enabled = true,
            ProjectName = snapshot?.ProjectName,
            HideWhenEmpty = _settings.TodoistHideWhenEmpty,
        };
        if (snapshot == null)
        {
            return result;
        }

        var localNow = _timeProvider.GetLocalNow();
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var due = new List<(ApiTask Task, DateOnly Day, TimeOnly? Time)>();
        foreach (var task in snapshot.Tasks)
        {
            if (task.Checked || task.IsDeleted || task.Due?.Date is not { } date)
            {
                continue;
            }

            if (!TryParseDue(date, out var day, out var time))
            {
                _logger.LogDebug("Todoist task {Id} has a due date that is not understood: '{Date}'", task.Id, date);
                continue;
            }

            if (day < today && !_settings.TodoistShowOverdue)
            {
                continue;
            }

            if (day <= today)
            {
                due.Add((task, day, time));
            }
        }

        // Overdue first, the oldest at the top. Then today: tasks with a time by time, then the
        // others. Inside each group, the highest priority first (the API's 4 is P1).
        var ordered = due
            .OrderBy(x => x.Day)
            .ThenBy(x => x.Day < today || x.Time == null ? TimeOnly.MaxValue : x.Time.Value)
            .ThenByDescending(x => x.Task.Priority)
            .ThenBy(x => x.Task.ChildOrder)
            .ToList();

        var max = Math.Clamp(_settings.TodoistMaxTasks, 1, MaxTasksLimit);
        result.Tasks = ordered.Take(max).Select(x => new TodoistTask
        {
            Id = x.Task.Id,
            Content = MarkdownLink.Replace(x.Task.Content, "$1"),
            Priority = 5 - Math.Clamp(x.Task.Priority, 1, 4),
            DueDate = x.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DueTime = x.Time?.ToString("HH:mm", CultureInfo.InvariantCulture),
            IsOverdue = x.Day < today,
            DaysOverdue = today.DayNumber - x.Day.DayNumber,
            IsRecurring = x.Task.Due?.IsRecurring ?? false,
            Labels = x.Task.Labels ?? new List<string>(),
        }).ToList();
        result.HiddenCount = ordered.Count - result.Tasks.Count;
        return result;
    }

    /// <summary>
    /// Todoist sends a full-day due as "2026-11-22", a floating time as "2026-11-22T18:00:00" and a
    /// time with a fixed time zone in UTC, as "2026-11-22T17:00:00Z". A UTC time is changed to the
    /// server time zone, so it can fall on another day.
    /// </summary>
    private bool TryParseDue(string date, out DateOnly day, out TimeOnly? time)
    {
        time = null;
        if (date.Length == 10)
        {
            return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
        }

        var hasZone = date.EndsWith('Z') || date.LastIndexOfAny(['+', '-']) > 10;
        if (hasZone)
        {
            if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            {
                var local = TimeZoneInfo.ConvertTime(instant, _timeProvider.LocalTimeZone);
                day = DateOnly.FromDateTime(local.DateTime);
                time = TimeOnly.FromDateTime(local.DateTime);
                return true;
            }
        }
        else if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var floating))
        {
            day = DateOnly.FromDateTime(floating);
            time = TimeOnly.FromDateTime(floating);
            return true;
        }

        day = default;
        return false;
    }

    private static bool IsTodoistFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or TodoistApiException;

    private static string Describe(Exception ex) => ex switch
    {
        TodoistApiException { StatusCode: 401 or 403 } api => $"HTTP {api.StatusCode}: check TodoistApiToken",
        TodoistApiException { StatusCode: 404 } api => $"HTTP {api.StatusCode}: check TodoistProjectId",
        TodoistApiException api => $"HTTP {api.StatusCode}",
        TaskCanceledException => "timeout",
        _ => ex.GetType().Name,
    };

    private sealed record Source(string Token, string ProjectId);

    private sealed record Snapshot(Source Source, DateTimeOffset FetchedAt, List<ApiTask> Tasks, string? ProjectName);

    private sealed class TodoistApiException(int statusCode) : Exception($"Todoist API answered {statusCode}")
    {
        public int StatusCode { get; } = statusCode;
    }
}

internal sealed class ApiPage
{
    public List<ApiTask> Results { get; set; } = new();
    public string? NextCursor { get; set; }
}

internal sealed class ApiTask
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public ApiDue? Due { get; set; }
    public List<string>? Labels { get; set; }
    public int ChildOrder { get; set; }
    public bool Checked { get; set; }
    public bool IsDeleted { get; set; }
}

internal sealed class ApiDue
{
    public string? Date { get; set; }
    public bool IsRecurring { get; set; }
}

internal sealed class ApiProject
{
    public string? Name { get; set; }
}
