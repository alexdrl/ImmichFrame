using ImmichFrame.Core.Models;

namespace ImmichFrame.Core.Interfaces;

public interface ITodoistService
{
    /// <summary>
    /// The tasks of the configured Todoist project that are due today, and the overdue tasks if
    /// TodoistShowOverdue is set.
    /// </summary>
    Task<TodoistTasks> GetTasks(CancellationToken cancellationToken = default);
}
