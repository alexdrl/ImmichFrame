namespace ImmichFrame.Core.Models;

/// <summary>
/// The Todoist overlay: tasks of one project that are due today or overdue.
/// </summary>
public class TodoistTasks
{
    /// <summary>False when no API token or project id is set. The client then shows nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The project name, or null when Todoist did not return it.</summary>
    public string? ProjectName { get; set; }

    public List<TodoistTask> Tasks { get; set; } = new();

    /// <summary>Tasks that match but are not in <see cref="Tasks"/> because of TodoistMaxTasks.</summary>
    public int HiddenCount { get; set; }

    public bool HideWhenEmpty { get; set; }
}

public class TodoistTask
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The task name as plain text: Markdown links are replaced by their text.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>As in the Todoist app: 1 = P1 (urgent) to 4 = P4 (normal). The API counts the other way.</summary>
    public int Priority { get; set; } = 4;

    /// <summary>The due day, yyyy-MM-dd, in the server time zone.</summary>
    public string DueDate { get; set; } = string.Empty;

    /// <summary>The due time, HH:mm, in the server time zone. Null for a task without a time.</summary>
    public string? DueTime { get; set; }

    public bool IsOverdue { get; set; }

    /// <summary>Days since the due day: 0 for today, 1 for yesterday.</summary>
    public int DaysOverdue { get; set; }

    public bool IsRecurring { get; set; }

    public List<string> Labels { get; set; } = new();
}
