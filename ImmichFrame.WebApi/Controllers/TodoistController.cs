using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImmichFrame.WebApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class TodoistController : ControllerBase
    {
        private readonly ILogger<TodoistController> _logger;
        private readonly ITodoistService _todoistService;

        public TodoistController(ILogger<TodoistController> logger, ITodoistService todoistService)
        {
            _logger = logger;
            _todoistService = todoistService;
        }

        [HttpGet(Name = "GetTodoistTasks")]
        public async Task<TodoistTasks> GetTodoistTasks(string clientIdentifier = "", CancellationToken cancellationToken = default)
        {
            var sanitizedClientIdentifier = clientIdentifier.SanitizeString();
            _logger.LogDebug("Todoist tasks requested by '{sanitizedClientIdentifier}'", sanitizedClientIdentifier);
            return await _todoistService.GetTasks(cancellationToken);
        }
    }
}
