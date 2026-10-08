namespace FindEverything.Server;

public sealed class ServerOptions
{
    public const string SectionName = "FindEverything";
    public string DataDirectory { get; set; } = ".local/server";
    public List<SourceDefinition> Sources { get; set; } = [];
    public int MaxQueuedJobs { get; set; } = 100;
    public int SchedulerPollSeconds { get; set; } = 5;
}

public sealed class SourceDefinition
{
    public string Id { get; set; } = "";
    public string RootPath { get; set; } = "";
}
