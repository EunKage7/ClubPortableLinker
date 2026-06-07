using System.Text.Json.Serialization;

namespace ClubPortableLinker;

public sealed class PortableConfig
{
    public List<AppProfile> Profiles { get; set; } = [];

    public AppProfile FindProfile(string name)
    {
        // Profiles может быть null, если manifest правили вручную ("Profiles": null) —
        // даём понятную ошибку вместо NullReferenceException.
        if (Profiles is null || Profiles.Count == 0)
        {
            throw new InvalidOperationException($"Сборка не найдена: {name} (в пакете нет сборок).");
        }

        return Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Сборка не найдена: {name}");
    }
}

public sealed class AppProfile
{
    public string Name { get; set; } = "";
    public string PortableRoot { get; set; } = "";
    public string ClientResourcesRoot { get; set; } = "";
    public string SharedResourcesRoot { get; set; } = "";
    [JsonIgnore]
    public string ConfigDirectory { get; set; } = "";
    public List<LinkRule> Links { get; set; } = [];
    public List<string> RegistryFiles { get; set; } = [];
    public List<BatchRule> Batches { get; set; } = [];
    public List<TaskRule> Tasks { get; set; } = [];
    public List<ServiceRule> Services { get; set; } = [];

    /// <summary>
    /// Игры, установленные внутри платформы-лаунчера (RSI Launcher → Star Citizen,
    /// Steam → конкретная игра, Riot → Valorant и т.д.). У каждой игры свои пути,
    /// reg-файлы и запускатели, но все они лежат внутри portable-папки платформы.
    /// </summary>
    public List<GameModule> Games { get; set; } = [];

    /// <summary>
    /// Делать резервные копии при конфликте путей. В клубной (CCBOOT/diskless) среде
    /// система откатывается при перезагрузке, поэтому по умолчанию выключено.
    /// </summary>
    public bool KeepBackups { get; set; }

    /// <summary>Платформа + все включённые игры как единый набор ссылок.</summary>
    public IEnumerable<LinkRule> AllLinks()
        => Links.Concat(Games.Where(g => g.Enabled).SelectMany(g => g.Links));

    public IEnumerable<string> AllRegistryFiles()
        => RegistryFiles.Concat(Games.Where(g => g.Enabled).SelectMany(g => g.RegistryFiles));

    public IEnumerable<BatchRule> AllBatches()
        => Batches.Concat(Games.Where(g => g.Enabled).SelectMany(g => g.Batches));
}

/// <summary>
/// Игра внутри платформы. Платформа — это лаунчер (RSI Launcher, Steam, Riot Client),
/// а игра ставится уже внутри него и имеет собственные папки данных, reg-файлы и .exe.
/// </summary>
public sealed class GameModule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<LinkRule> Links { get; set; } = [];
    public List<string> RegistryFiles { get; set; } = [];
    public List<BatchRule> Batches { get; set; } = [];
}

public sealed class LinkRule
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public LinkKind Kind { get; set; } = LinkKind.Junction;
    public bool MoveExisting { get; set; } = true;
    public bool OverwriteEmptySource { get; set; } = true;
    public ExistingSourceAction ExistingSourceAction { get; set; } = ExistingSourceAction.MoveToTargetOrBackup;
}

public sealed class BatchRule
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string TargetExe { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
}

public sealed class TaskRule
{
    public string Name { get; set; } = "";
    public string XmlPath { get; set; } = "";
}

public sealed class ServiceRule
{
    public string Name { get; set; } = "";
    public string BinaryPath { get; set; } = "";
    public string Type { get; set; } = "own";
    public string StartMode { get; set; } = "demand";
    public bool StartAfterApply { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LinkKind
{
    Junction,
    SymlinkDir,
    SymlinkFile,
    HardlinkFile
}

public enum OperationMode
{
    All,
    Links,
    Registry,
    Batches,
    SafeMode,
    Tasks,
    Services
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExistingSourceAction
{
    Stop,
    MoveToTargetOrBackup,
    BackupOnly
}

public sealed record ExecutionOptions(bool Apply, OperationMode Mode);

public sealed record ExecutionResult(bool Success, int Errors);

public sealed class PackageBuildRequest
{
    public string SourceFolder { get; set; } = "";
    public string DestinationFolder { get; set; } = "";
    public string ProfileName { get; set; } = "";
}

public sealed record PackageBuildResult(string PackageFolder, string ConfigPath, AppProfile Profile);
