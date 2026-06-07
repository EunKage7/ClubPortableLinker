namespace ClubPortableLinker;

public sealed record PlanIssue(string Severity, string Code, string Message, string? Path = null);

public sealed record PackagePlan(
    string ProfileName,
    string PortableRoot,
    int LinkCount,
    int MoveLinkCount,
    int BackupOnlyLinkCount,
    int StopLinkCount,
    int RegistryCount,
    int BatchCount,
    int TaskCount,
    int ServiceCount,
    IReadOnlyList<PlanIssue> Issues,
    IReadOnlyList<GameModule> Games)
{
    public bool HasBlockingIssues => Issues.Any(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
}
