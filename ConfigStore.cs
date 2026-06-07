using System.Text.Json;

namespace ClubPortableLinker;

public static class ConfigStore
{
    public const string PortableDirectoryName = ".portable";
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static PortableConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Путь к настройкам пустой.");
        }

        var resolvedPath = ResolveConfigPath(path);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("Файл настроек не найден.", resolvedPath);
        }

        var json = File.ReadAllText(resolvedPath);
        var config = JsonSerializer.Deserialize<PortableConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Файл настроек пустой или поврежден.");

        // Защита от null-коллекций в манифесте ("Profiles": null, "Games": null и т.п.):
        // иначе .Count / AllLinks() бросят NRE на ручном/частичном манифесте.
        config.Profiles ??= [];
        if (config.Profiles.Count == 0)
        {
            throw new InvalidOperationException("В файле настроек нет сборок.");
        }

        var configDirectory = ResolvePortableRootFromConfigPath(resolvedPath);
        foreach (var profile in config.Profiles)
        {
            profile.Links ??= [];
            profile.RegistryFiles ??= [];
            profile.Batches ??= [];
            profile.Tasks ??= [];
            profile.Services ??= [];
            profile.Games ??= [];
            foreach (var game in profile.Games)
            {
                game.Links ??= [];
                game.RegistryFiles ??= [];
                game.Batches ??= [];
            }

            profile.ConfigDirectory = configDirectory;
        }

        return config;
    }

    public static string GetManifestPath(string portableRoot)
    {
        return Path.Combine(Path.GetFullPath(portableRoot), PortableDirectoryName, ManifestFileName);
    }

    public static bool HasHiddenManifest(string portableRoot)
    {
        return File.Exists(GetManifestPath(portableRoot));
    }

    public static string ResolvePortableRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        return ResolvePortableRootFromConfigPath(ResolveConfigPath(path));
    }

    public static string ResolveConfigPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var hiddenManifest = GetManifestPath(fullPath);
            if (File.Exists(hiddenManifest))
            {
                return hiddenManifest;
            }

            var legacyProfile = Path.Combine(fullPath, "profiles.json");
            if (File.Exists(legacyProfile))
            {
                return legacyProfile;
            }

            return hiddenManifest;
        }

        return fullPath;
    }

    public static string SavePackage(string portableRoot, PortableConfig config)
    {
        var root = Path.GetFullPath(portableRoot);
        var portableMeta = Path.Combine(root, PortableDirectoryName);
        Directory.CreateDirectory(portableMeta);

        try
        {
            var attributes = File.GetAttributes(portableMeta);
            File.SetAttributes(portableMeta, attributes | FileAttributes.Hidden);
        }
        catch
        {
            // Скрытый атрибут не критичен (FAT/сеть/гонка могут бросить) — не валим Save.
        }

        foreach (var profile in config.Profiles)
        {
            profile.ConfigDirectory = root;
            if (string.IsNullOrWhiteSpace(profile.PortableRoot))
            {
                profile.PortableRoot = "{configDir}";
            }
        }

        // Атомарная запись: пишем во временный файл и заменяем — обрыв питания
        // на бездисковой машине не оставит обрезанный/битый manifest.json.
        var manifestPath = Path.Combine(portableMeta, ManifestFileName);
        // Уникальный temp на запись — иначе два одновременных Save через общий
        // manifest.json.tmp могли затереть данные друг друга.
        var tempPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(tempPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) { try { File.Delete(tempPath); } catch { } }
        }

        return manifestPath;
    }

    public static void WriteSample(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(CreateSample(), JsonOptions));
    }

    public static PortableConfig CreateSample()
    {
        return new PortableConfig
        {
            Profiles =
            [
                new AppProfile
                {
                    Name = "BlueStacks",
                    PortableRoot = @"{configDir}",
                    Links =
                    [
                        new LinkRule
                        {
                            Name = "BlueStacks program files",
                            Source = @"C:\Program Files\BlueStacks_nxt",
                            Target = @"{portableRoot}\ProgramFiles\BlueStacks_nxt",
                            Kind = LinkKind.Junction,
                            MoveExisting = true
                        },
                        new LinkRule
                        {
                            Name = "BlueStacks ProgramData",
                            Source = @"C:\ProgramData\BlueStacks_nxt",
                            Target = @"{portableRoot}\ProgramData\BlueStacks_nxt",
                            Kind = LinkKind.Junction,
                            MoveExisting = true
                        },
                        new LinkRule
                        {
                            Name = "BlueStacks user data",
                            Source = @"%LOCALAPPDATA%\BlueStacks",
                            Target = @"{portableRoot}\UserData\BlueStacks",
                            Kind = LinkKind.Junction,
                            MoveExisting = true
                        }
                    ],
                    RegistryFiles =
                    [
                        @"{appDir}\Profiles\BlueStacks\Regs\install.reg"
                    ],
                    Batches =
                    [
                        new BatchRule
                        {
                            Name = "Run BlueStacks",
                            Path = @"{portableRoot}\RunBlueStacks.cmd",
                            TargetExe = @"C:\Program Files\BlueStacks_nxt\HD-Player.exe",
                            Arguments = "",
                            WorkingDirectory = @"C:\Program Files\BlueStacks_nxt"
                        }
                    ]
                },

                // Платформа-лаунчер + вложенные игры. Сам RSI Launcher — это оболочка,
                // а игра (Star Citizen) ставится уже внутри него и имеет свои папки/.exe.
                // Каждая игра — отдельный GameModule: её можно включить/выключить, не трогая
                // остальные, и все данные лежат внутри одного portable-корня платформы.
                new AppProfile
                {
                    Name = "RSI Launcher",
                    PortableRoot = @"{configDir}",
                    KeepBackups = false,
                    Links =
                    [
                        new LinkRule
                        {
                            Name = "RSI Launcher program files",
                            Source = @"%ProgramFiles%\Roberts Space Industries\RSI Launcher",
                            Target = @"{portableRoot}\Launcher\RSI Launcher",
                            Kind = LinkKind.Junction
                        },
                        new LinkRule
                        {
                            Name = "RSI Launcher user data",
                            Source = @"%AppData%\rsilauncher",
                            Target = @"{portableRoot}\Launcher\rsilauncher",
                            Kind = LinkKind.Junction
                        }
                    ],
                    Games =
                    [
                        new GameModule
                        {
                            Name = "Star Citizen",
                            Enabled = true,
                            Links =
                            [
                                new LinkRule
                                {
                                    Name = "Star Citizen LIVE",
                                    Source = @"%ProgramFiles%\Roberts Space Industries\StarCitizen\LIVE",
                                    Target = @"{portableRoot}\Games\StarCitizen\LIVE",
                                    Kind = LinkKind.Junction
                                }
                            ],
                            Batches =
                            [
                                new BatchRule
                                {
                                    Name = "Run RSI Launcher",
                                    Path = @"{portableRoot}\RunStarCitizen.cmd",
                                    TargetExe = @"%ProgramFiles%\Roberts Space Industries\RSI Launcher\RSI Launcher.exe"
                                }
                            ]
                        }
                    ]
                }
            ]
        };
    }

    private static string ResolvePortableRootFromConfigPath(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        if (string.Equals(Path.GetFileName(directory), PortableDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(directory)!;
        }

        return directory;
    }
}
