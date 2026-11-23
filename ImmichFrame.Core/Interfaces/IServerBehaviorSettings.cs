namespace ImmichFrame.Core.Interfaces
{
    public interface IServerBehaviorSettings
    {
        public List<string> Webcalendars { get; }
        public int RefreshAlbumPeopleInterval { get; }
        public string? WeatherApiKey { get; }
        // Todoist overlay: tasks of one project that are due today or overdue. Server-side only:
        // the token never goes to the client.
        public string? TodoistApiToken { get; }
        public string? TodoistProjectId { get; }
        public bool TodoistShowOverdue { get; }
        public int TodoistMaxTasks { get; }
        public bool TodoistHideWhenEmpty { get; }
        public int TodoistRefreshMinutes { get; }
        // Background opacity of the overlay cards, 0 (text only) to 100 (solid).
        public int TodoistOpacity { get; }
        public string? WeatherLatLong { get; }
        public string? UnitSystem { get; }
        public string? Webhook { get; }
        public string? AuthenticationSecret { get; }
        public string? AdminPassword { get; }
    }
}
