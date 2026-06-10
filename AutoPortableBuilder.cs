using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClubPortableLinker;

public sealed class AutoBuildRequest
{
    public string AppName { get; set; } = "";
    public string PortableRoot { get; set; } = "";
    public string MainFolder { get; set; } = "";
    public string ClientResourcesRoot { get; set; } = "";
    public string SharedResourcesRoot { get; set; } = "";
    public bool ApplyAfterBuild { get; set; } = true;
}

public sealed class InstallSnapshot
{
    public DateTime CapturedAtUtc { get; set; }
    public List<DirectoryProbe> Directories { get; set; } = [];
}

public sealed class DirectoryProbe
{
    public string Root { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime LastWriteUtc { get; set; }
}

public static partial class AutoPortableBuilder
{
    private const int ExternalProcessTimeoutMs = 15000;

    private static readonly string[] LauncherBadWords =
    [
        "unins", "uninstall", "setup", "install", "update", "updater", "crash",
        "report", "helper", "service", "redist", "directx", "repair"
    ];

    // internal: используется и RegistryGameCollector — токены захвата reg игры
    // проходят тот же стоп-фильтр (без него игра «EVE Online» давала токен «Online»,
    // который reg-поиском утаскивал ключи посторонних вендоров).
    internal static readonly string[] TokenStopWords =
    [
        "program", "files", "launcher", "setup", "installer", "app", "game", "games",
        "client", "online", "service", "services", "x64", "x86"
    ];

    // Однозначно установочные маркеры — допускают подстроку (например
    // "InstallerTemp_12345"): такие папки точно мусорные.
    private static readonly string[] InstallerTempSubstringMarkers =
    [
        "installertemp", "setuptemp", "tempinstall", "installtemp"
    ];

    // Неоднозначные маркеры (кэш) — срабатывают только при ТОЧНОМ совпадении
    // имени папки, иначе бы резали легитимные папки игр вроде "WebCache".
    private static readonly string[] TemporaryFolderExactMarkers =
    [
        "installertemp", "setuptemp", "tempinstall", "installtemp",
        "downloadcache", "webcache"
    ];

    public static InstallSnapshot CaptureSnapshot(Action<string> log)
    {
        var snapshot = new InstallSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            Directories = CaptureDirectories()
        };

        log($"Снимок сделан: {snapshot.Directories.Count} папок под наблюдением.");
        return snapshot;
    }

    public static PackageBuildResult BuildFromInstalledFolder(AutoBuildRequest request, Action<string> log)
    {
        if (!Directory.Exists(request.MainFolder))
        {
            throw new DirectoryNotFoundException(request.MainFolder);
        }

        if (LooksLikePortableLayout(request.MainFolder))
        {
            log("Похоже на уже собранную portable-папку. Конвертирую layout без установки.");
            return BuildFromPortableLayout(request, log);
        }

        var appName = CleanName(request.AppName, request.MainFolder);
        var portableRoot = RequirePortableRoot(request.PortableRoot);
        var tokens = BuildTokens(appName, request.MainFolder);
        log($"Автосборка: {appName}");

        var paths = DiscoverRelatedDirectories(request.MainFolder, tokens, log);
        return BuildFromPaths(appName, portableRoot, paths, tokens, request.ClientResourcesRoot, request.SharedResourcesRoot, request.ApplyAfterBuild, log);
    }

    private static PackageBuildResult BuildFromPortableLayout(AutoBuildRequest request, Action<string> log)
    {
        var sourceRoot = Path.GetFullPath(request.MainFolder);
        var portableRoot = RequirePortableRoot(request.PortableRoot);
        var appName = CleanName(request.AppName, sourceRoot);

        if (!SamePath(sourceRoot, portableRoot))
        {
            // Цель не должна быть ВНУТРИ источника — иначе CopyDirectory копировал бы
            // растущую цель саму в себя до заполнения диска.
            if (IsUnderRoot(portableRoot, sourceRoot))
            {
                throw new InvalidOperationException(
                    $"«Куда собрать» ({portableRoot}) находится внутри исходной папки ({sourceRoot}). Выберите папку вне источника.");
            }

            log($"Копирование portable-layout: {sourceRoot} -> {portableRoot}");
            CopyDirectory(sourceRoot, portableRoot);
        }

        var profile = new AppProfile
        {
            Name = appName,
            PortableRoot = "{configDir}",
            ClientResourcesRoot = request.ClientResourcesRoot,
            SharedResourcesRoot = request.SharedResourcesRoot,
            ConfigDirectory = portableRoot
        };

        var importedLauncherConfig = AddLauncherIniLayout(profile, portableRoot, appName, log);
        if (!importedLauncherConfig)
        {
            AddPortableLayoutLinks(profile, portableRoot, appName, log);
            AddPortableLayoutRegistry(profile, portableRoot, log);
        }

        var confirmedSpecial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var specialLauncherAdded = importedLauncherConfig || AddSpecialAppRules(profile, [portableRoot], portableRoot, confirmedSpecial, log);

        var tokens = EnrichTokens(BuildTokens(appName, sourceRoot), Directory.GetDirectories(portableRoot));
        if (!specialLauncherAdded)
        {
            var launcher = FindBestLauncher([portableRoot], tokens, log);
            if (launcher is not null)
            {
                profile.Batches.Add(new BatchRule
                {
                    Name = appName,
                    Path = @"{portableRoot}\Run.cmd",
                    TargetExe = launcher,
                    Arguments = "%*",
                    WorkingDirectory = Path.GetDirectoryName(launcher) ?? ""
                });
            }
        }

        if (!importedLauncherConfig)
        {
            ExportRegistry(profile, portableRoot, tokens, confirmedSpecial, log);
        }
        else
        {
            log("Registry auto-search пропущен: используются reg/config из launcher-layout.");
        }

        ExportSpecialRegistry(profile, confirmedSpecial, portableRoot, log);
        Deduplicate(profile, log);
        var configPath = ConfigStore.SavePackage(portableRoot, new PortableConfig { Profiles = [profile] });
        log($"Служебный manifest записан: {configPath}");
        WriteBuildReport(profile, portableRoot, log);

        if (request.ApplyAfterBuild)
        {
            var result = PortableEngine.Execute(profile, new ExecutionOptions(true, OperationMode.All), log);
            if (!result.Success)
            {
                throw new InvalidOperationException("Конвертация portable-layout завершилась с ошибками. Проверьте лог.");
            }
        }

        return new PackageBuildResult(portableRoot, configPath, profile);
    }

    public static PackageBuildResult BuildFromSnapshot(InstallSnapshot before, AutoBuildRequest request, Action<string> log)
    {
        var portableRoot = RequirePortableRoot(request.PortableRoot);
        var initialName = CleanName(request.AppName, request.MainFolder);
        var tokens = BuildTokens(initialName, request.MainFolder);
        var changed = DetectChangedDirectories(before, tokens, log);
        var mainFolder = request.MainFolder;

        if (!Directory.Exists(mainFolder))
        {
            mainFolder = InferMainFolder(changed, log) ?? "";
        }

        var appName = CleanName(request.AppName, mainFolder);
        tokens = BuildTokens(appName, mainFolder);

        if (Directory.Exists(mainFolder) && changed.All(path => !SamePath(path, mainFolder)))
        {
            changed.Insert(0, Path.GetFullPath(mainFolder));
        }

        if (changed.Count == 0)
        {
            throw new InvalidOperationException("После установки не найдено новых папок. Укажите главную папку программы и соберите через режим уже установленной программы.");
        }

        return BuildFromPaths(appName, portableRoot, changed, tokens, request.ClientResourcesRoot, request.SharedResourcesRoot, request.ApplyAfterBuild, log);
    }

    // Превью «что попадёт в портабл» БЕЗ переноса файлов: список папок + размеры.
    public static void PreviewBuild(AutoBuildRequest request, Action<string> log)
    {
        if (!Directory.Exists(request.MainFolder))
        {
            throw new DirectoryNotFoundException(request.MainFolder);
        }

        var appName = CleanName(request.AppName, request.MainFolder);
        var tokens = BuildTokens(appName, request.MainFolder);
        log($"Предпросмотр сборки: {appName} (файлы не переносятся)");
        var paths = DiscoverRelatedDirectories(request.MainFolder, tokens, log);

        log("");
        log("=== Что попадёт в портабл ===");
        long total = 0;
        var count = 0;
        foreach (var path in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsTemporaryInstallerFolder(path))
            {
                log($"  (пропуск, мусор установщика) {path}");
                continue;
            }

            var target = MapPortableTarget(path);
            if (target is null)
            {
                log($"  (пропуск, вне стандартных путей Windows) {path}");
                continue;
            }

            var size = SafeDirectorySize(path);
            total += size;
            count++;
            log($"  [{FormatSize(size),9}]  {path}  ->  {target}");
        }

        log($"Итого папок данных: {count}, объём {FormatSize(total)}");
        log("reg и службы будут добавлены при фактической сборке по типу приложения.");
    }

    // Дельта-захват: что НОВОГО появилось после снимка (докачанная игра и т.п.)
    // и, при --apply, перенос новых папок в существующий пакет + junction.
    public static PackageBuildResult? CaptureDelta(
        InstallSnapshot before,
        string packageFolder,
        string? profileName,
        string? gameFilter,
        bool apply,
        Action<string> log)
    {
        var config = ConfigStore.Load(packageFolder);
        var profile = string.IsNullOrWhiteSpace(profileName)
            ? config.Profiles.FirstOrDefault() ?? throw new InvalidOperationException("В пакете нет профилей.")
            : config.FindProfile(profileName);

        var existing = profile.Links
            .Select(l => Path.GetFullPath(PathTokens.Expand(l.Source, profile)).TrimEnd('\\'))
            .ToList();

        var beforeMap = before.Directories.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (var entry in CaptureDirectories())
        {
            if (beforeMap.ContainsKey(entry.Path))
            {
                continue; // папка уже была до установки
            }

            var full = Path.GetFullPath(entry.Path).TrimEnd('\\');
            if (IsTemporaryInstallerFolder(full) || MapPortableTarget(full) is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(gameFilter) &&
                !Path.GetFileName(full).Contains(gameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // уже покрыта существующей ссылкой пакета?
            if (existing.Any(s => full.Equals(s, StringComparison.OrdinalIgnoreCase)
                || IsUnderRoot(full, s) || IsUnderRoot(s, full)))
            {
                continue;
            }

            candidates.Add(full);
        }

        candidates = DeduplicatePaths(candidates);
        if (candidates.Count == 0)
        {
            log("Новых папок для захвата не найдено.");
            return null;
        }

        log("");
        log("=== Новые папки после снимка (дельта) ===");
        long total = 0;
        foreach (var path in candidates)
        {
            var size = SafeDirectorySize(path);
            total += size;
            log($"  + [{FormatSize(size),9}]  {path}  ->  {MapPortableTarget(path)}");
        }
        log($"Итого: {candidates.Count} папок, {FormatSize(total)}");

        if (!apply)
        {
            log("Это предпросмотр. Добавьте --apply, чтобы перенести их в пакет и поставить junction.");
            return null;
        }

        foreach (var path in candidates)
        {
            profile.Links.Add(new LinkRule
            {
                Name = Path.GetFileName(path),
                Source = path,
                Target = MapPortableTarget(path)!,
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true,
                ExistingSourceAction = ExistingSourceAction.MoveToTargetOrBackup
            });
        }

        Deduplicate(profile, log);
        var portableRoot = ConfigStore.ResolvePortableRoot(packageFolder);
        var configPath = ConfigStore.SavePackage(portableRoot, config);
        log($"Манифест обновлён: {configPath}");

        var result = PortableEngine.Execute(profile, new ExecutionOptions(true, OperationMode.All), log);
        if (!result.Success)
        {
            throw new InvalidOperationException("Захват дельты завершился с ошибками. Проверьте лог.");
        }

        return new PackageBuildResult(portableRoot, configPath, profile);
    }

    private static long SafeDirectorySize(string root)
    {
        long sum = 0;
        var stack = new Stack<(string Dir, bool IsRoot)>();
        stack.Push((root, true));
        while (stack.Count > 0)
        {
            var (dir, isRoot) = stack.Pop();
            try
            {
                // Внутрь вложенных junction/symlink не идём (зацикливание/двойной счёт),
                // но корень считаем даже если он сам ссылка — для превью уже слинкованной папки.
                if (!isRoot && (new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try { sum += new FileInfo(file).Length; }
                    catch { /* недоступный файл — пропускаем */ }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    stack.Push((sub, false));
                }
            }
            catch { /* недоступная папка — пропускаем */ }
        }

        return sum;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return $"{value:0.#} {units[i]}";
    }

    private static PackageBuildResult BuildFromPaths(
        string appName,
        string portableRoot,
        List<string> paths,
        IReadOnlyCollection<string> tokens,
        string clientResourcesRoot,
        string sharedResourcesRoot,
        bool applyAfterBuild,
        Action<string> log)
    {
        Directory.CreateDirectory(portableRoot);
        var enrichedTokens = EnrichTokens(tokens, paths);

        var profile = new AppProfile
        {
            Name = appName,
            PortableRoot = "{configDir}",
            ClientResourcesRoot = clientResourcesRoot,
            SharedResourcesRoot = sharedResourcesRoot,
            ConfigDirectory = portableRoot
        };

        foreach (var path in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsTemporaryInstallerFolder(path))
            {
                log($"Пропущена временная папка установщика: {path}");
                continue;
            }

            var target = MapPortableTarget(path);
            if (target is null)
            {
                log($"Пропущено вне стандартных путей Windows: {path}");
                continue;
            }

            profile.Links.Add(new LinkRule
            {
                Name = Path.GetFileName(path.TrimEnd('\\')),
                Source = path,
                Target = target,
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true,
                ExistingSourceAction = ExistingSourceAction.MoveToTargetOrBackup
            });
        }

        var confirmedSpecial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var specialLauncherAdded = AddSpecialAppRules(profile, paths, portableRoot, confirmedSpecial, log);
        Deduplicate(profile, log);
        if (profile.Links.Count == 0 && profile.Batches.Count == 0 && !specialLauncherAdded)
        {
            throw new InvalidOperationException("Не найдено папок, которые можно вынести в portable.");
        }

        if (!specialLauncherAdded)
        {
            var launcher = FindBestLauncher(profile.Links.Select(link => link.Source), enrichedTokens, log);
            if (launcher is not null)
            {
                profile.Batches.Add(new BatchRule
                {
                    Name = appName,
                    Path = @"{portableRoot}\Run.cmd",
                    TargetExe = launcher,
                    Arguments = "%*",
                    WorkingDirectory = Path.GetDirectoryName(launcher) ?? ""
                });
                log($"Запускатель найден: {launcher}");
            }
            else
            {
                log("Запускатель .exe не найден автоматически. Run.cmd не создан.");
            }
        }
        else
        {
            log("Запускатель задан спец-профилем.");
        }

        ExportRegistry(profile, portableRoot, enrichedTokens, confirmedSpecial, log);

        ExportSpecialRegistry(profile, confirmedSpecial, portableRoot, log);
        Deduplicate(profile, log);
        var configPath = ConfigStore.SavePackage(portableRoot, new PortableConfig { Profiles = [profile] });
        log($"Служебный manifest записан: {configPath}");
        WriteBuildReport(profile, portableRoot, log);

        if (applyAfterBuild)
        {
            var result = PortableEngine.Execute(profile, new ExecutionOptions(true, OperationMode.All), log);
            if (!result.Success)
            {
                throw new InvalidOperationException("Автосборка завершилась с ошибками. Проверьте лог.");
            }
        }

        return new PackageBuildResult(portableRoot, configPath, profile);
    }

    private static bool AddSpecialAppRules(AppProfile profile, IReadOnlyCollection<string> paths, string portableRoot, Action<string> log)
    {
        return AddSpecialAppRules(profile, paths, portableRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase), log);
    }

    // confirmed пополняется именами платформ, чьё правило РЕАЛЬНО сработало (exe найден).
    // Только для них потом экспортируется спец-reg — иначе ключи чужой платформы
    // (напр. Steam, обновлявшийся в фоне) утекали в пакет по совпадению ключевого слова.
    private static bool AddSpecialAppRules(AppProfile profile, IReadOnlyCollection<string> paths, string portableRoot, ISet<string> confirmed, Action<string> log)
    {
        if (IsBlueStacks(profile, paths) && AddBlueStacksRules(profile, paths, portableRoot, log)) { confirmed.Add("BlueStacks"); }
        if (IsRsiLauncher(profile, paths) && AddRsiLauncherRules(profile, paths, portableRoot, log)) { confirmed.Add("RSI"); }
        if (IsRageMp(profile, paths) && AddRageMpRules(profile, paths, portableRoot, log)) { confirmed.Add("RAGEMP"); }
        if (IsSteam(profile, paths) && AddSteamRules(profile, paths, log)) { confirmed.Add("Steam"); }
        if (IsRiot(profile, paths) && AddRiotRules(profile, paths, log)) { confirmed.Add("Riot"); }
        if (IsFaceit(profile, paths) && AddFaceitRules(profile, paths, log)) { confirmed.Add("FACEIT"); }
        if (IsEpic(profile, paths) && AddEpicRules(profile, paths, log)) { confirmed.Add("Epic"); }
        if (IsBattleNet(profile, paths) && AddBattleNetRules(profile, paths, log)) { confirmed.Add("BattleNet"); }
        if (IsEa(profile, paths) && AddEaRules(profile, paths, log)) { confirmed.Add("EA"); }
        if (IsUbisoft(profile, paths) && AddUbisoftRules(profile, paths, log)) { confirmed.Add("Ubisoft"); }
        if (IsGog(profile, paths) && AddGogRules(profile, paths, log)) { confirmed.Add("GOG"); }
        if (IsRockstar(profile, paths) && AddRockstarRules(profile, paths, log)) { confirmed.Add("Rockstar"); }
        if (IsWargaming(profile, paths) && AddWargamingRules(profile, paths, log)) { confirmed.Add("Wargaming"); }
        if (IsLesta(profile, paths) && AddLestaRules(profile, paths, log)) { confirmed.Add("Lesta"); }
        if (IsBattleState(profile, paths) && AddBattleStateRules(profile, paths, log)) { confirmed.Add("BattleState"); }
        if (IsVkPlay(profile, paths) && AddVkPlayRules(profile, paths, log)) { confirmed.Add("VKPlay"); }
        if (IsFourGame(profile, paths) && AddFourGameRules(profile, paths, log)) { confirmed.Add("4game"); }
        return confirmed.Count > 0;
    }

    // Epic Games Launcher: вся папка «Epic Games» (с установленными в ней играми) +
    // данные лаунчера + запуск EpicGamesLauncher.exe.
    private static bool AddEpicRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var epicExe = FindNamedExe(profile, paths, "EpicGamesLauncher.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games")
        ]);

        if (string.IsNullOrWhiteSpace(epicExe))
        {
            return false;
        }

        var epicRoot = FindParentDirectoryNamed(epicExe, "Epic Games") ?? Path.GetDirectoryName(epicExe) ?? "";
        AddKnownDirectoryLink(profile, epicRoot, log); // вся папка Epic Games — игры внутри едут вместе
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealEngine"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Epic",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = epicExe,
            Arguments = "%*",
            WorkingDirectory = Path.GetDirectoryName(epicExe) ?? epicRoot
        });

        log("Epic Games: ссылки (папка лаунчера с играми, ProgramData\\Epic, LocalAppData) и запуск через Run.cmd.");
        return true;
    }

    // Battle.net (Blizzard): папка лаунчера + конфиги (в Blizzard Entertainment лежит
    // список игр и их пути) + запуск Battle.net.exe.
    private static bool AddBattleNetRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var bnetExe = FindNamedExe(profile, paths, "Battle.net.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net")
        ]);

        if (string.IsNullOrWhiteSpace(bnetExe))
        {
            return false;
        }

        var bnetRoot = Path.GetDirectoryName(bnetExe) ?? "";
        AddKnownDirectoryLink(profile, bnetRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Blizzard Entertainment"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Battle.net",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = bnetExe,
            Arguments = "%*",
            WorkingDirectory = bnetRoot
        });

        log("Battle.net: ссылки (лаунчер + конфиги Blizzard) и запуск. Игры в отдельных папках Program Files добавьте вручную, если нужны.");
        return true;
    }

    // EA App (бывший Origin): EA Desktop + данные + папка игр EA Games + запуск EADesktop.exe.
    private static bool AddEaRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var eaExe = FindNamedExe(profile, paths, "EADesktop.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Electronic Arts", "EA Desktop"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Electronic Arts", "EA Desktop")
        ]);

        if (string.IsNullOrWhiteSpace(eaExe))
        {
            return false;
        }

        var eaRoot = FindParentDirectoryNamed(eaExe, "EA Desktop") ?? Path.GetDirectoryName(eaExe) ?? "";
        AddKnownDirectoryLink(profile, eaRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EA Desktop"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Electronic Arts"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "EA",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = eaExe,
            Arguments = "%*",
            WorkingDirectory = Path.GetDirectoryName(eaExe) ?? eaRoot
        });

        log("EA App: ссылки (EA Desktop + Electronic Arts + EA Games) и запуск через Run.cmd.");
        return true;
    }

    // Ubisoft Connect (бывший Uplay): папка лаунчера + кэш/сейвы в LocalAppData + запуск UbisoftConnect.exe.
    private static bool AddUbisoftRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var ubiRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ubisoft", "Ubisoft Game Launcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ubisoft", "Ubisoft Game Launcher")
        };

        var ubiExe = FindNamedExe(profile, paths, "UbisoftConnect.exe", ubiRoots)
            ?? FindNamedExe(profile, paths, "upc.exe", ubiRoots);

        if (string.IsNullOrWhiteSpace(ubiExe))
        {
            return false;
        }

        var ubiRoot = FindParentDirectoryNamed(ubiExe, "Ubisoft Game Launcher") ?? Path.GetDirectoryName(ubiExe) ?? "";
        AddKnownDirectoryLink(profile, ubiRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Ubisoft Connect",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = ubiExe,
            Arguments = "%*",
            WorkingDirectory = Path.GetDirectoryName(ubiExe) ?? ubiRoot
        });

        log("Ubisoft Connect: ссылки (лаунчер + LocalAppData) и запуск через Run.cmd. InstallDir восстанавливается из reg.");
        return true;
    }

    // GOG Galaxy: папка лаунчера + конфиг/база в ProgramData\GOG.com + запуск GalaxyClient.exe.
    private static bool AddGogRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var gogExe = FindNamedExe(profile, paths, "GalaxyClient.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GOG Galaxy")
        ]);

        if (string.IsNullOrWhiteSpace(gogExe))
        {
            return false;
        }

        var gogRoot = Path.GetDirectoryName(gogExe) ?? "";
        AddKnownDirectoryLink(profile, gogRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GOG.com"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "GOG Galaxy",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = gogExe,
            Arguments = "%*",
            WorkingDirectory = gogRoot
        });

        log("GOG Galaxy: ссылки (лаунчер + ProgramData\\GOG.com) и запуск через Run.cmd.");
        return true;
    }

    // Rockstar Games Launcher + Social Club: папки лаунчера + данные в LocalAppData/ProgramData + запуск Launcher.exe.
    private static bool AddRockstarRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var rockstarExe = FindNamedExe(profile, paths, "Launcher.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rockstar Games", "Launcher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rockstar Games", "Launcher")
        ]);

        // "Launcher.exe" — родовое имя (есть у многих). Берём только если найден
        // именно в Rockstar Games — иначе можно зацепить чужой Launcher.exe.
        if (string.IsNullOrWhiteSpace(rockstarExe) ||
            !rockstarExe.Contains(@"\Rockstar Games\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var launcherRoot = Path.GetDirectoryName(rockstarExe) ?? "";
        AddKnownDirectoryLink(profile, launcherRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rockstar Games", "Social Club"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rockstar Games", "Social Club"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rockstar Games"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rockstar Games"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Rockstar Games",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = rockstarExe,
            Arguments = "%*",
            WorkingDirectory = launcherRoot
        });

        log("Rockstar Games: ссылки (Launcher + Social Club + Local/ProgramData) и запуск через Run.cmd.");
        return true;
    }

    // Wargaming.net Game Center (WoT/WoWs/WoWp): папка лаунчера + данные + запуск wgc.exe.
    private static bool AddWargamingRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var wgcExe = FindNamedExe(profile, paths, "wgc.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Wargaming.net", "GameCenter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wargaming.net", "GameCenter")
        ]);

        if (string.IsNullOrWhiteSpace(wgcExe))
        {
            return false;
        }

        var wgcRoot = Path.GetDirectoryName(wgcExe) ?? "";
        AddKnownDirectoryLink(profile, wgcRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Wargaming.net"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wargaming.net"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Wargaming Game Center",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = wgcExe,
            Arguments = "%*",
            WorkingDirectory = wgcRoot
        });

        log("Wargaming Game Center: ссылки (лаунчер + ProgramData/AppData Wargaming.net) и запуск через Run.cmd.");
        return true;
    }

    // Lesta Game Center (Мир танков/кораблей/Blitz) — RU-форк Wargaming GC. Запуск lgc.exe.
    private static bool AddLestaRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var lgcExe = FindNamedExe(profile, paths, "lgc.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lesta", "GameCenter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Lesta", "GameCenter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lesta Games", "GameCenter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Lesta Games", "GameCenter")
        ]);

        if (string.IsNullOrWhiteSpace(lgcExe))
        {
            return false;
        }

        var lgcRoot = Path.GetDirectoryName(lgcExe) ?? "";
        AddKnownDirectoryLink(profile, lgcRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lesta"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lesta Games"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lesta"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Lesta Game Center",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = lgcExe,
            Arguments = "%*",
            WorkingDirectory = lgcRoot
        });

        log("Lesta Game Center: ссылки (лаунчер + ProgramData/AppData Lesta) и запуск через Run.cmd.");
        return true;
    }

    // BattleState Games (Escape from Tarkov / Arena) — BsgLauncher.exe.
    private static bool AddBattleStateRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var bsgExe = FindNamedExe(profile, paths, "BsgLauncher.exe",
        [
            @"C:\Battlestate Games\BsgLauncher",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battlestate Games", "BsgLauncher"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battlestate Games", "BsgLauncher")
        ]);

        if (string.IsNullOrWhiteSpace(bsgExe))
        {
            return false;
        }

        var bsgRoot = Path.GetDirectoryName(bsgExe) ?? "";
        AddKnownDirectoryLink(profile, bsgRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battlestate Games"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battlestate Games"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "BattleState (Tarkov)",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = bsgExe,
            Arguments = "%*",
            WorkingDirectory = bsgRoot
        });

        log("BattleState Games: ссылки (BsgLauncher + данные) и запуск через Run.cmd.");
        return true;
    }

    // VK Play (бывший MY.GAMES / Mail.ru Game Center): vkplay.exe или GameCenter.exe.
    private static bool AddVkPlayRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var vkRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKPlay"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VK Play"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameCenter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VK", "VK Play"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VK", "VK Play"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GameCenter")
        };

        var vkExe = FindNamedExe(profile, paths, "vkplay.exe", vkRoots)
            ?? FindNamedExe(profile, paths, "VKPlay.exe", vkRoots)
            ?? FindNamedExe(profile, paths, "GameCenter.exe", vkRoots);

        if (string.IsNullOrWhiteSpace(vkExe))
        {
            return false;
        }

        var vkRoot = Path.GetDirectoryName(vkExe) ?? "";
        AddKnownDirectoryLink(profile, vkRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKPlay"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameCenter"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mail.Ru"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "VK Play",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = vkExe,
            Arguments = "%*",
            WorkingDirectory = vkRoot
        });

        log("VK Play: ссылки (лаунчер + LocalAppData/Mail.Ru) и запуск через Run.cmd.");
        return true;
    }

    // 4game (Innova): Lineage II, Aion, Black Desert, PointBlank. Запуск 4game.exe.
    private static bool AddFourGameRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var fgRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "4game"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "4game"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "4game")
        };

        var fgExe = FindNamedExe(profile, paths, "4game.exe", fgRoots)
            ?? FindNamedExe(profile, paths, "4gameClient.exe", fgRoots);

        if (string.IsNullOrWhiteSpace(fgExe))
        {
            return false;
        }

        var fgRoot = Path.GetDirectoryName(fgExe) ?? "";
        AddKnownDirectoryLink(profile, fgRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "4game"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "4game"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "4game",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = fgExe,
            Arguments = "%*",
            WorkingDirectory = fgRoot
        });

        log("4game (Innova): ссылки (лаунчер + Local/ProgramData 4game) и запуск через Run.cmd.");
        return true;
    }

    private static bool AddBlueStacksRules(AppProfile profile, IReadOnlyCollection<string> paths, string portableRoot, Action<string> log)
    {
        var programFiles = FindBlueStacksProgramFiles(profile, paths);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return false;
        }

        var player = Path.Combine(programFiles, "HD-Player.exe");
        if (!File.Exists(player))
        {
            return false;
        }

        var instance = DetectBlueStacksInstance(profile, paths) ?? "Nougat32";
        // Имя инстанса берётся из bluestacks.conf (внешний файл) и идёт в Run.cmd
        // как аргумент — оставляем только безопасные символы, без cmd-метасимволов.
        instance = Regex.Replace(instance, "[^A-Za-z0-9_.-]", "");
        if (string.IsNullOrEmpty(instance))
        {
            instance = "Nougat32";
        }
        profile.Batches.Add(new BatchRule
        {
            Name = "BlueStacks",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = player,
            Arguments = $"--instance {instance} %*",
            WorkingDirectory = programFiles
        });

        var multiInstance = Path.Combine(programFiles, "HD-MultiInstanceManager.exe");
        if (File.Exists(multiInstance))
        {
            profile.Batches.Add(new BatchRule
            {
                Name = "BlueStacks Multi-Instance",
                Path = @"{portableRoot}\.portable\Tools\MultiInstanceManager.cmd",
                TargetExe = multiInstance,
                Arguments = "%*",
                WorkingDirectory = programFiles
            });
        }

        var driver = Path.Combine(programFiles, "BstkDrv_nxt.sys");
        if (File.Exists(driver) && profile.Services.All(service => !service.Name.Equals("BlueStacksDrv_nxt", StringComparison.OrdinalIgnoreCase)))
        {
            profile.Services.Add(new ServiceRule
            {
                Name = "BlueStacksDrv_nxt",
                // Путь драйвера — из реально найденной папки установки (может быть
                // Program Files (x86) / другой язык ОС), а не захардкоженная константа.
                BinaryPath = @"\??\" + driver,
                Type = "kernel",
                StartMode = "auto",
                StartAfterApply = true
            });
        }

        ExportScheduledTask(portableRoot, "BlueStacksHelper_nxt", profile, log);
        WriteBlueStacksPreRun(portableRoot, log);
        log($"BlueStacks: запуск через HD-Player.exe --instance {instance}, добавлены service/task self-heal.");
        return true;
    }

    private static bool AddRsiLauncherRules(AppProfile profile, IReadOnlyCollection<string> paths, string portableRoot, Action<string> log)
    {
        var launcher = FindRsiLauncher(profile, paths);
        if (string.IsNullOrWhiteSpace(launcher))
        {
            return false;
        }

        var programRoot = FindRsiProgramRoot(launcher);
        RemoveChildLinks(profile, programRoot, log);
        AddKnownDirectoryLink(profile, programRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rsilauncher"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rsilauncher"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rsilauncher-updater"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roberts Space Industries"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roberts Space Industries"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roberts Space Industries"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "RSI Launcher",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = launcher,
            Arguments = "%*",
            WorkingDirectory = Path.GetDirectoryName(launcher) ?? programRoot
        });

        log("RSI Launcher: добавлены типовые ссылки Program Files/AppData и запуск через Run.cmd.");
        return true;
    }

    private static void RemoveChildLinks(AppProfile profile, string parentPath, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return;
        }

        var parent = Path.GetFullPath(parentPath).TrimEnd('\\');
        var removed = profile.Links.RemoveAll(link =>
        {
            var source = Path.GetFullPath(PathTokens.Expand(link.Source, profile)).TrimEnd('\\');
            return source.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
        });

        if (removed > 0)
        {
            log($"Удалены перекрывающиеся дочерние ссылки: {removed}");
        }
    }

    private static bool AddRageMpRules(AppProfile profile, IReadOnlyCollection<string> paths, string portableRoot, Action<string> log)
    {
        var launcher = FindRageMpLauncher(profile, paths);
        if (string.IsNullOrWhiteSpace(launcher))
        {
            return false;
        }

        var rageRoot = Path.GetDirectoryName(launcher) ?? @"C:\RAGEMP";
        AddCustomDirectoryLink(profile, rageRoot, @"{portableRoot}\RAGEMP", log);

        if (string.IsNullOrWhiteSpace(profile.SharedResourcesRoot))
        {
            profile.SharedResourcesRoot = @"\\SERVER\RAGEMP";
        }

        WriteRageMpPreRun(portableRoot, profile.SharedResourcesRoot, log);
        profile.Batches.Add(new BatchRule
        {
            Name = "RAGE Multiplayer",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = launcher,
            Arguments = "%*",
            WorkingDirectory = rageRoot
        });

        log($"RAGE MP: client_resources будут перенаправляться в сетевую папку {profile.SharedResourcesRoot}.");
        return true;
    }

    private static bool AddSteamRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var steamExe = FindNamedExe(profile, paths, "steam.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        ]);

        if (string.IsNullOrWhiteSpace(steamExe))
        {
            return false;
        }

        var steamRoot = Path.GetDirectoryName(steamExe) ?? "";
        AddKnownDirectoryLink(profile, steamRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Steam"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"), log);

        profile.Batches.Add(new BatchRule
        {
            Name = "Steam",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = steamExe,
            Arguments = "%*",
            WorkingDirectory = steamRoot
        });

        log("Steam: добавлены типовые ссылки и запуск через Run.cmd.");
        return true;
    }

    private static bool AddRiotRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var riotExe = FindNamedExe(profile, paths, "RiotClientServices.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Riot Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games")
        ]);

        if (string.IsNullOrWhiteSpace(riotExe))
        {
            return false;
        }

        var riotRoot = FindParentDirectoryNamed(riotExe, "Riot Games") ?? Path.GetDirectoryName(riotExe) ?? "";
        AddKnownDirectoryLink(profile, riotRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games"), log);

        var args = profile.Name.Contains("Valorant", StringComparison.OrdinalIgnoreCase)
            ? "--launch-product=valorant --launch-patchline=live %*"
            : "%*";

        profile.Batches.Add(new BatchRule
        {
            Name = "Riot Client",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = riotExe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(riotExe) ?? riotRoot
        });

        log("Riot Client: добавлены ссылки ProgramData/AppData и запуск через Run.cmd.");
        return true;
    }

    private static bool AddFaceitRules(AppProfile profile, IReadOnlyCollection<string> paths, Action<string> log)
    {
        var faceitClient = FindNamedExe(profile, paths, "faceitclient.exe",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FACEIT AC"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FACEIT AC")
        ]);

        if (string.IsNullOrWhiteSpace(faceitClient))
        {
            return false;
        }

        var faceitRoot = Path.GetDirectoryName(faceitClient) ?? "";
        AddKnownDirectoryLink(profile, faceitRoot, log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FACEIT"), log);
        AddKnownDirectoryLink(profile, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FACEIT"), log);

        var serviceExe = Path.Combine(faceitRoot, "faceitservice.exe");
        if (File.Exists(serviceExe))
        {
            profile.Services.RemoveAll(service => service.Name.Equals("FACEITService", StringComparison.OrdinalIgnoreCase));
            profile.Services.Add(new ServiceRule
            {
                Name = "FACEITService",
                BinaryPath = serviceExe,
                Type = "own",
                StartMode = "demand",
                StartAfterApply = false
            });
        }

        profile.Batches.Add(new BatchRule
        {
            Name = "FACEIT",
            Path = @"{portableRoot}\Run.cmd",
            TargetExe = faceitClient,
            Arguments = "%*",
            WorkingDirectory = faceitRoot
        });

        log("FACEIT: добавлена служба FACEITService и запуск faceitclient.exe через Run.cmd.");
        return true;
    }

    private static void WriteBlueStacksPreRun(string portableRoot, Action<string> log)
    {
        Directory.CreateDirectory(Path.Combine(portableRoot, ConfigStore.PortableDirectoryName));

        var scriptPath = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "BlueStacksPreRun.ps1");
        var script = """
$ErrorActionPreference = 'SilentlyContinue'

$conf = Join-Path $env:ProgramData 'BlueStacks_nxt\bluestacks.conf'
if (Test-Path -LiteralPath $conf) {
    $text = [IO.File]::ReadAllText($conf)
    $settings = [ordered]@{
        'bst.launch_store_on_boot' = '0'
        'bst.enable_boot_banner' = '0'
        'bst.feature.show_boot_banner_preference' = '0'
        'bst.feature.show_gp_ads' = '0'
        'bst.feature.show_sdk_gp_popup' = '0'
        'bst.feature.programmatic_ads' = '0'
        'bst.feature.show_programmatic_ads_preference' = '0'
        'bst.enable_programmatic_ads' = '0'
        'bst.feature.show_ai_highlights' = '0'
        'bst.enable_ai_highlights' = '0'
        'bst.feature.nowgg_cloud_upload_enabled' = '0'
        'bst.enable_discord_integration' = '0'
        'bst.create_desktop_shortcuts' = '0'
        'bst.enable_bsx_app_shortcuts' = '0'
        'bst.feature.nowgg_login_popup' = '0'
        'bst.do_not_show_link_account_popup' = '1'
    }

    foreach ($entry in $settings.GetEnumerator()) {
        $line = "$($entry.Key)=`"$($entry.Value)`""
        $pattern = "(?m)^$([regex]::Escape($entry.Key))=`"[^`"]*`""
        if ($text -match $pattern) {
            $text = [regex]::Replace($text, $pattern, $line)
        } else {
            $text = $text.TrimEnd("`r", "`n") + "`r`n" + $line + "`r`n"
        }
    }

    # Звук Android при нажатии — выключаем во всех инстансах.
    $text = [regex]::Replace($text, '(?m)^(bst\.instance\.[^.]+\.android_sound_while_tapping)="[^"]*"', '$1="0"')

    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($conf, $text, $utf8NoBom)
}

# Авто-очистка логов и временных файлов BlueStacks (инстансы и игры НЕ трогаем).
$cleanTargets = @(
    (Join-Path $env:ProgramData 'BlueStacks_nxt\Logs'),
    (Join-Path $env:LOCALAPPDATA 'BlueStacks_nxt\Logs'),
    (Join-Path $env:ProgramData 'BlueStacks_nxt\Engine\Logs')
)
foreach ($t in $cleanTargets) {
    if (Test-Path -LiteralPath $t) {
        Get-ChildItem -LiteralPath $t -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Get-ChildItem -LiteralPath $env:TEMP -Filter 'BlueStacks*' -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
""";
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        var toolsRoot = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "Tools");
        Directory.CreateDirectory(toolsRoot);

        var preRunPath = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "PortablePreRun.cmd");
        var batch = """
@echo off
setlocal
if exist "%~dp0BlueStacksPreRun.ps1" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BlueStacksPreRun.ps1"
exit /b 0
""";
        File.WriteAllText(preRunPath, batch, new UTF8Encoding(false));
        log("BlueStacks: добавлен PortablePreRun.cmd, отключающий старт магазина и лишние баннеры перед запуском.");
    }

    private static void WriteRageMpPreRun(string portableRoot, string shareRoot, Action<string> log)
    {
        Directory.CreateDirectory(Path.Combine(portableRoot, ConfigStore.PortableDirectoryName));
        Directory.CreateDirectory(Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "Logs"));

        shareRoot = NormalizeShareRoot(shareRoot);
        var preRunPath = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "PortablePreRun.cmd");
        var batch = $$"""
@echo off
setlocal EnableExtensions
set "RAGE_SHARE={{shareRoot}}"
set "gtasteam=D:\Steam\steamapps\common\Grand Theft Auto V"
set "gtaepic=D:\OnlineGames\GTAV"
set "gtarockstar=D:\OnlineGames\Grand Theft Auto V"
set "_share_no_slashes=%RAGE_SHARE:~2%"
for /f "tokens=1 delims=\" %%s in ("%_share_no_slashes%") do set "RAGE_SERVER=%%s"

if not defined PORTABLE_ROOT for %%d in ("%~dp0..") do set "PORTABLE_ROOT=%%~fd\"
set "RAGE_ROOT=%PORTABLE_ROOT%RAGEMP"
if not exist "%RAGE_ROOT%\updater.exe" set "RAGE_ROOT=%PORTABLE_ROOT%"
set "LOCAL_RES=%RAGE_ROOT%\client_resources"
set "RESOURCE_MODE=shared"

if /I "%RAGEMP_RESOURCE_MODE%"=="pc" set "RESOURCE_MODE=pc"
for %%a in (%*) do (
  if /I "%%~a"=="-pcresources" set "RESOURCE_MODE=pc"
  if /I "%%~a"=="-sharedresources" set "RESOURCE_MODE=shared"
)

set "wmic_cmd=wmic useraccount where name='%username%' get sid /value"
for /f "tokens=2 delims==" %%i in ('%wmic_cmd% 2^>nul ^| findstr /I "SID="') do set "sid=%%i"

set "gamepath="
for %%a in (%*) do (
  if /I "%%~a"=="-steam" set "gamepath=%gtasteam%"
  if /I "%%~a"=="-epic" set "gamepath=%gtaepic%"
  if /I "%%~a"=="-rockstar" set "gamepath=%gtarockstar%"
)
if not defined gamepath if exist "%gtasteam%\GTA5.exe" set "gamepath=%gtasteam%"
if not defined gamepath if exist "%gtaepic%\GTA5.exe" set "gamepath=%gtaepic%"
if not defined gamepath if exist "%gtarockstar%\GTA5.exe" set "gamepath=%gtarockstar%"

if defined gamepath (
  reg add "HKCU\SOFTWARE\RAGE-MP" /v game_v_path /t REG_SZ /d "%gamepath%" /f >nul 2>nul
  if defined sid reg add "HKEY_USERS\%sid%\SOFTWARE\RAGE-MP" /v game_v_path /t REG_SZ /d "%gamepath%" /f >nul 2>nul
)

if /I "%RESOURCE_MODE%"=="pc" (
  set "RESOURCE_TARGET=%RAGE_SHARE%\%COMPUTERNAME%\client_resources"
) else (
  set "RESOURCE_TARGET=%RAGE_SHARE%\client_resources"
)

ping -n 1 -w 700 "%RAGE_SERVER%" >nul 2>nul
if errorlevel 1 (
  if not exist "%PORTABLE_ROOT%.portable\Logs" mkdir "%PORTABLE_ROOT%.portable\Logs" >nul 2>nul
  echo [%date% %time%] RAGE MP server not reachable: %RAGE_SERVER%>>"%PORTABLE_ROOT%.portable\Logs\RAGEMP.log"
  exit /b 0
)

if not exist "%RAGE_SHARE%\" (
  if not exist "%PORTABLE_ROOT%.portable\Logs" mkdir "%PORTABLE_ROOT%.portable\Logs" >nul 2>nul
  echo [%date% %time%] RAGE MP share not available: %RAGE_SHARE%>>"%PORTABLE_ROOT%.portable\Logs\RAGEMP.log"
  exit /b 0
)

if not exist "%RESOURCE_TARGET%" mkdir "%RESOURCE_TARGET%" >nul 2>nul
if not exist "%RESOURCE_TARGET%\" (
  echo [%date% %time%] Cannot create resource target: %RESOURCE_TARGET%>>"%PORTABLE_ROOT%.portable\Logs\RAGEMP.log"
  exit /b 0
)

if exist "%LOCAL_RES%" (
  fsutil reparsepoint query "%LOCAL_RES%" >nul 2>nul
  if errorlevel 1 (
    robocopy "%LOCAL_RES%" "%RESOURCE_TARGET%" /E /XO /R:1 /W:1 >nul
    if not errorlevel 8 rmdir /S /Q "%LOCAL_RES%" >nul 2>nul
  ) else (
    rmdir "%LOCAL_RES%" >nul 2>nul
  )
)

mklink /D "%LOCAL_RES%" "%RESOURCE_TARGET%" >nul 2>nul
exit /b 0
""";

        File.WriteAllText(preRunPath, batch, new UTF8Encoding(false));
        log($"RAGE MP: добавлен pre-run для game_v_path и client_resources -> {shareRoot}.");
    }

    private static bool IsBlueStacks(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRsiLauncher(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("RSI", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Roberts Space Industries", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Star Citizen", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("RSI Launcher", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("Roberts Space Industries", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("StarCitizen", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("RSI Launcher", StringComparison.OrdinalIgnoreCase) ||
                                         link.Source.Contains("Roberts Space Industries", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("RSI Launcher", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Roberts Space Industries", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRageMp(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("RAGE", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("RAGEMP", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("RAGEMP", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("RAGE MP", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("RAGE Multiplayer", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("RAGEMP", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("RAGEMP", StringComparison.OrdinalIgnoreCase));
    }

    // Сегмент пути ровно равен name (\Steam\ или …\Steam в конце): подстрочный Contains
    // ловил «SteamWorld Dig», «Steamworks Shared» и т.п. — и чужой пакет «подтверждал»
    // Steam, после чего УСТАНОВЛЕННЫЙ Steam с машины физически утаскивался в этот пакет.
    private static bool HasPathSegment(string path, string name)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\\" + name + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSteam(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            || profile.Name.StartsWith("Steam ", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => HasPathSegment(path, "Steam"))
            || profile.Links.Any(link => HasPathSegment(link.Source, "Steam") ||
                                         HasPathSegment(link.Target, "Steam"));
    }

    private static bool IsRiot(AppProfile profile, IEnumerable<string> paths)
    {
        // «League of Legends», а не голое «League»: пакет «Rocket League» подтверждал
        // Riot и утаскивал установленный Riot Client в чужой пакет.
        return profile.Name.Contains("Riot", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Valorant", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("League of Legends", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Riot Games", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("Valorant", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Riot Games", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Riot Games", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFaceit(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("FACEIT", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("FACEIT", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("FACEIT", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("FACEIT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEpic(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("Epic", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) ||
                                         link.Source.Contains("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBattleNet(AppProfile profile, IEnumerable<string> paths)
    {
        // «Battle.net», а не голое «Battle»: пакет «Battlefield» подтверждал Battle.net
        // и утаскивал установленный Battle.net с машины в чужой пакет.
        return profile.Name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) ||
                                         link.Source.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEa(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Equals("EA", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("EA App", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Origin", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase) ||
                                         link.Source.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUbisoft(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Uplay", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGog(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("GOG", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("GOG Galaxy", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("GOG.com", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("GOG Galaxy", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("GOG Galaxy", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRockstar(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Social Club", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWargaming(AppProfile profile, IEnumerable<string> paths)
    {
        // ВНИМАНИЕ: не ловим бар «Game Center» — это слишком широко (Lesta Game Center,
        // VK Play «GameCenter», Mail.ru GameCenter). Только явный Wargaming.
        return profile.Name.Contains("Wargaming", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Wargaming.net", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Wargaming.net", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Wargaming.net", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLesta(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("Lesta", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Мир танков", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Мир кораблей", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Lesta", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Lesta", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Lesta", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBattleState(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("Battlestate", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("BattleState", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Tarkov", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("Battlestate", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("BsgLauncher", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("Battlestate", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("Battlestate", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVkPlay(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("VK Play", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("VKPlay", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("MY.GAMES", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("VK Play", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("VKPlay", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("MailRuGameCenter", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("vkplay.exe", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("VK Play", StringComparison.OrdinalIgnoreCase) ||
                                         link.Source.Contains("VKPlay", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("VK Play", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFourGame(AppProfile profile, IEnumerable<string> paths)
    {
        return profile.Name.Contains("4game", StringComparison.OrdinalIgnoreCase)
            || profile.Name.Contains("Innova", StringComparison.OrdinalIgnoreCase)
            || paths.Any(path => path.Contains("4game", StringComparison.OrdinalIgnoreCase) ||
                                 path.Contains("Innova", StringComparison.OrdinalIgnoreCase))
            || profile.Links.Any(link => link.Source.Contains("4game", StringComparison.OrdinalIgnoreCase) ||
                                         link.Target.Contains("4game", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindBlueStacksProgramFiles(AppProfile profile, IEnumerable<string> paths)
    {
        foreach (var path in profile.Links.Select(link => PathTokens.Expand(link.Source, profile)).Concat(paths))
        {
            if (File.Exists(Path.Combine(path, "HD-Player.exe")))
            {
                return Path.GetFullPath(path);
            }
        }

        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BlueStacks_nxt");
        return File.Exists(Path.Combine(defaultPath, "HD-Player.exe")) ? defaultPath : null;
    }

    private static string? FindRsiLauncher(AppProfile profile, IEnumerable<string> paths)
    {
        var roots = profile.Links
            .SelectMany(link => new[] { PathTokens.Expand(link.Source, profile), PathTokens.Expand(link.Target, profile) })
            .Concat(paths)
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roberts Space Industries"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roberts Space Industries")
            })
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, "RSI Launcher.exe"),
                Path.Combine(root, "RSI Launcher", "RSI Launcher.exe")
            })
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            var found = SafeEnumerateExe(root)
                .FirstOrDefault(exe => Path.GetFileName(exe).Equals("RSI Launcher.exe", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(found))
            {
                return Path.GetFullPath(found);
            }
        }

        return null;
    }

    private static string? FindRageMpLauncher(AppProfile profile, IEnumerable<string> paths)
    {
        var roots = profile.Links
            .SelectMany(link => new[] { PathTokens.Expand(link.Source, profile), PathTokens.Expand(link.Target, profile) })
            .Concat(paths)
            .Concat(new[] { @"C:\RAGEMP" })
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(root, "updater.exe"),
                Path.Combine(root, "ragemp_v.exe")
            })
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? FindNamedExe(AppProfile profile, IEnumerable<string> paths, string fileName, IEnumerable<string> defaults)
    {
        var roots = profile.Links
            .SelectMany(link => new[] { PathTokens.Expand(link.Source, profile), PathTokens.Expand(link.Target, profile) })
            .Concat(paths)
            .Concat(defaults)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var direct = Path.Combine(root, fileName);
            if (File.Exists(direct))
            {
                return Path.GetFullPath(direct);
            }

            var found = SafeEnumerateExe(root)
                .FirstOrDefault(exe => Path.GetFileName(exe).Equals(fileName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(found))
            {
                return Path.GetFullPath(found);
            }
        }

        return null;
    }

    private static string? FindParentDirectoryNamed(string fileOrDirectory, string directoryName)
    {
        var current = Directory.Exists(fileOrDirectory)
            ? new DirectoryInfo(fileOrDirectory)
            : new DirectoryInfo(Path.GetDirectoryName(fileOrDirectory) ?? fileOrDirectory);

        while (current.Parent is not null)
        {
            if (current.Name.Equals(directoryName, StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string FindRsiProgramRoot(string launcher)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(launcher) ?? launcher);
        while (current.Parent is not null)
        {
            if (current.Name.Equals("Roberts Space Industries", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetDirectoryName(launcher) ?? launcher;
    }

    private static void AddKnownDirectoryLink(AppProfile profile, string source, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            return;
        }

        if (profile.Links.Any(link => SamePath(PathTokens.Expand(link.Source, profile), source)))
        {
            return;
        }

        var target = MapPortableTarget(source);
        if (target is null)
        {
            return;
        }

        profile.Links.Add(new LinkRule
        {
            Name = Path.GetFileName(source.TrimEnd('\\')),
            Source = source,
            Target = target,
            Kind = LinkKind.Junction,
            MoveExisting = true,
            OverwriteEmptySource = true,
            ExistingSourceAction = ExistingSourceAction.MoveToTargetOrBackup
        });
        log($"Добавлена типовая ссылка: {source}");
    }

    private static void AddCustomDirectoryLink(AppProfile profile, string source, string target, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            return;
        }

        if (profile.Links.Any(link => SamePath(PathTokens.Expand(link.Source, profile), source)))
        {
            return;
        }

        profile.Links.Add(new LinkRule
        {
            Name = Path.GetFileName(source.TrimEnd('\\')),
            Source = source,
            Target = target,
            Kind = LinkKind.Junction,
            MoveExisting = true,
            OverwriteEmptySource = true,
            ExistingSourceAction = ExistingSourceAction.MoveToTargetOrBackup
        });
        log($"Добавлена ссылка: {source} -> {target}");
    }

    private static string NormalizeShareRoot(string value)
    {
        var root = string.IsNullOrWhiteSpace(value)
            ? @"\\SERVER\RAGEMP"
            : value.Trim().TrimEnd('\\');

        if (!root.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            root = @"\\" + root.TrimStart('\\');
        }

        var parts = root.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            root = @"\\" + parts[0] + @"\RAGEMP";
        }

        return root.TrimEnd('\\');
    }

    private static string? DetectBlueStacksInstance(AppProfile profile, IEnumerable<string> paths)
    {
        var candidates = profile.Links
            .SelectMany(link => new[] { PathTokens.Expand(link.Source, profile), PathTokens.Expand(link.Target, profile) })
            .Concat(paths)
            .Select(path => Path.Combine(path, "bluestacks.conf"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = new List<string>();

        foreach (var conf in candidates.Where(File.Exists))
        {
            string text;
            try
            {
                text = File.ReadAllText(conf); // conf может быть занят/битый — не роняем сборку
            }
            catch
            {
                continue;
            }

            var match = Regex.Match(text, "bst\\.installed_images=\"(?<images>[^\"]+)\"");
            if (match.Success)
            {
                names.AddRange(match.Groups["images"].Value
                    .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        foreach (var path in candidates.Select(Path.GetDirectoryName).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var engine = Path.Combine(path!, "Engine");
            if (!Directory.Exists(engine))
            {
                continue;
            }

            foreach (var instance in Directory.GetDirectories(engine))
            {
                if (File.Exists(Path.Combine(instance, Path.GetFileName(instance) + ".bstk")) ||
                    File.Exists(Path.Combine(instance, "Data.vhdx")))
                {
                    names.Add(Path.GetFileName(instance));
                }
            }
        }

        // Предпочитаем самый свежий и 64-битный инстанс (Android 13/11/9 > Nougat,
        // 64-бит > 32-бит): современные игры (Brawl Stars и т.п.) требуют 64-бит.
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(InstanceScore)
            .FirstOrDefault();
    }

    private static int InstanceScore(string name)
    {
        var n = name.ToLowerInvariant();
        var score = n switch
        {
            _ when n.Contains("tiramisu") => 130, // Android 13
            _ when n.Contains("rvc") => 110,       // Android 11
            _ when n.Contains("pie") => 90,        // Android 9
            _ when n.Contains("nougat") => 70,     // Android 7
            _ => 50
        };

        if (n.Contains("64"))
        {
            score += 5; // 64-бит важнее 32-бит
        }

        return score;
    }

    // Экспортирует спец-ветки реестра ТОЛЬКО для платформ из confirmed (чьё правило
    // реально сработало — лаунчер найден). Раньше шло по IsXxx(paths) и тащило ключи
    // чужих платформ (напр. Steam в пакет Ubisoft), если их папка случайно была в paths.
    private static void ExportSpecialRegistry(AppProfile profile, ISet<string> confirmed, string portableRoot, Action<string> log)
    {
        if (confirmed.Contains("BlueStacks"))
        {
            var blueStacksKeys = new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\BlueStacks_nxt",
                @"HKEY_CURRENT_USER\SOFTWARE\BlueStacks X",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\BlueStacks_nxt",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\BlueStacksInstaller",
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BlueStacksDrv_nxt",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\.apk",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\.xapk",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\AppUserModelId\BlueStacks_nxt",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\BlueStacks_Apk",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\BlueStacks_Xapk",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\BlueStacksGP",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\BlueStacksX",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\BstLogcollector"
            };

            foreach (var key in blueStacksKeys)
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("RSI"))
        {
            var rsiKeys = new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Roberts Space Industries",
                @"HKEY_CURRENT_USER\SOFTWARE\Cloud Imperium Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Roberts Space Industries",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Cloud Imperium Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Roberts Space Industries",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Cloud Imperium Games"
            };

            foreach (var key in rsiKeys)
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("RAGEMP"))
        {
            var rageKeys = new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\RAGE-MP",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\RAGE-MP",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\RAGE-MP"
            };

            foreach (var key in rageKeys)
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("Epic"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Epic Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Epic Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Epic Games"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("BattleNet"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Blizzard Entertainment",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Blizzard Entertainment",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("EA"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Electronic Arts",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Electronic Arts",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Electronic Arts",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\EA Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Origin"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("Steam"))
        {
            // Valve\Steam хранит SteamPath/InstallPath — без него часть игр не находит клиент.
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("Ubisoft"))
        {
            // Ubisoft\Launcher → InstallDir; без него Connect «теряет» себя после переобраза.
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Ubisoft",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Ubisoft",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("GOG"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\GOG.com",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("Rockstar"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Rockstar Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Rockstar Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rockstar Games"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("Wargaming"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Wargaming.net",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Wargaming.net",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Wargaming.net"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("Lesta"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Lesta",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Lesta",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Lesta",
                @"HKEY_CURRENT_USER\SOFTWARE\Lesta.ru",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Lesta.ru"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("BattleState"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\Battlestate Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Battlestate Games",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Battlestate Games"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("VKPlay"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\VK",
                @"HKEY_CURRENT_USER\SOFTWARE\VK Play",
                @"HKEY_CURRENT_USER\SOFTWARE\Mail.Ru",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\VK",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Mail.Ru"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }

        if (confirmed.Contains("4game"))
        {
            foreach (var key in new[]
            {
                @"HKEY_CURRENT_USER\SOFTWARE\4game",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\4game",
                @"HKEY_CURRENT_USER\SOFTWARE\Innova",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Innova"
            })
            {
                ExportRegistryKey(profile, portableRoot, key, log);
            }
        }
    }

    private static void ExportRegistryKey(AppProfile profile, string portableRoot, string key, Action<string> log)
    {
        if (!ProcessReturnsZero("reg.exe", $"query \"{key}\""))
        {
            return;
        }

        var registryRoot = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "Registry");
        Directory.CreateDirectory(registryRoot);
        var fileName = SafeFileName(profile.Name + "__" + FriendlyRegistryName(key)) + ".reg";
        var filePath = Path.Combine(registryRoot, fileName);
        if (ProcessReturnsZero("reg.exe", $"export \"{key}\" \"{filePath}\" /y"))
        {
            profile.RegistryFiles.Add(@"{portableRoot}\" + ConfigStore.PortableDirectoryName + @"\Registry\" + fileName);
            log($"Реестр экспортирован: {key}");
        }
    }

    private static void ExportScheduledTask(string portableRoot, string taskName, AppProfile profile, Action<string> log)
    {
        if (profile.Tasks.Any(task => task.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var output = CaptureProcessOutput("schtasks.exe", $"/Query /TN \"{taskName}\" /XML");
        var xmlStart = output.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        if (xmlStart < 0)
        {
            xmlStart = output.IndexOf("<Task", StringComparison.OrdinalIgnoreCase);
        }

        if (xmlStart < 0)
        {
            return;
        }

        var tasksRoot = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "Tasks");
        Directory.CreateDirectory(tasksRoot);
        var xmlPath = Path.Combine(tasksRoot, taskName + ".xml");
        File.WriteAllText(xmlPath, output[xmlStart..], Encoding.Unicode);
        profile.Tasks.Add(new TaskRule
        {
            Name = taskName,
            XmlPath = @"{portableRoot}\" + ConfigStore.PortableDirectoryName + @"\Tasks\" + taskName + ".xml"
        });
        log($"Задача планировщика экспортирована: {taskName}");
    }

    private static List<string> DiscoverRelatedDirectories(string mainFolder, IReadOnlyCollection<string> tokens, Action<string> log)
    {
        var result = new List<string> { Path.GetFullPath(mainFolder) };

        foreach (var root in WatchRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in EnumerateTopDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (MatchesTokens(name, tokens))
                {
                    result.Add(directory);
                }
            }
        }

        result = DeduplicatePaths(result);
        log($"Найдено связанных папок: {result.Count}");
        foreach (var path in result)
        {
            log($"  папка: {path}");
        }

        return result;
    }

    private static bool LooksLikePortableLayout(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        var markerNames = new[]
        {
            "Reg", "Registry", "AppData", "ProgramData", "ProgramFiles", "ProgramFilesX86", "Local", "Roaming", "UserLocal", "UserRoaming"
        };

        return markerNames.Any(name => Directory.Exists(Path.Combine(root, name)))
            || (File.Exists(Path.Combine(root, "config.ini")) && Directory.Exists(Path.Combine(root, "Data")))
            || Directory.EnumerateFiles(root, "*.bat", SearchOption.TopDirectoryOnly).Any();
    }

    private static bool AddLauncherIniLayout(AppProfile profile, string portableRoot, string appName, Action<string> log)
    {
        var iniPath = Path.Combine(portableRoot, "config.ini");
        var dataRoot = Path.Combine(portableRoot, "Data");
        if (!File.Exists(iniPath) || !Directory.Exists(dataRoot))
        {
            return false;
        }

        Dictionary<string, Dictionary<string, string>> ini;
        try
        {
            ini = ReadIniFile(iniPath);
        }
        catch (Exception ex)
        {
            // Занятый/битый config.ini не должен ронять всю сборку.
            log($"Не удалось прочитать config.ini ({ex.Message}) — layout пропущен.");
            return false;
        }
        var launcherName = IniValue(ini, "Settings", "LauncherName");
        var gameFolder = ExpandLauncherValue(IniValue(ini, "Settings", "GameFolder"));
        var linkedGameFolder = ExpandLauncherValue(IniValue(ini, "Settings", "LinkedGameFolder"));
        var launcherExe = ExpandLauncherValue(IniValue(ini, "Settings", "LauncherExe"));
        var launcherArgs = SanitizeLauncherArgs(ExpandLauncherValue(IniValue(ini, "Settings", "LauncherArgs")));
        var backupFolders = LauncherBackupFolders(IniValue(ini, "Settings", "BackupFolders"));

        log($"Launcher-layout найден: {iniPath}");

        if (ini.TryGetValue("ProgramLinks", out var programLinks))
        {
            foreach (var (key, value) in programLinks)
            {
                var windowsRoot = KnownLauncherFolderRoot(key);
                var dataFolder = KnownLauncherDataFolder(key);
                if (string.IsNullOrWhiteSpace(windowsRoot) || string.IsNullOrWhiteSpace(dataFolder) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var source = Path.Combine(windowsRoot, value);
                var target = Path.Combine(dataRoot, dataFolder, value);
                var existingSourceAction = LauncherExistingSourceAction(backupFolders, key, dataFolder, value);
                profile.Links.Add(new LinkRule
                {
                    Name = value,
                    Source = source,
                    Target = ToPortableTokenPath(portableRoot, target),
                    Kind = LinkKind.Junction,
                    MoveExisting = true,
                    OverwriteEmptySource = true,
                    ExistingSourceAction = existingSourceAction
                });
                log($"INI ProgramLinks: {source} -> {Path.GetRelativePath(portableRoot, target)}{DescribeLauncherExistingSourceAction(existingSourceAction)}");
            }
        }

        if (ini.TryGetValue("CustomLinks", out var customLinks))
        {
            foreach (var (folderName, sourcePath) in customLinks)
            {
                if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                var source = ExpandLauncherValue(sourcePath);
                var target = Path.Combine(dataRoot, folderName);
                var existingSourceAction = LauncherExistingSourceAction(backupFolders, folderName);
                profile.Links.Add(new LinkRule
                {
                    Name = folderName,
                    Source = source,
                    Target = ToPortableTokenPath(portableRoot, target),
                    Kind = LinkKind.Junction,
                    MoveExisting = true,
                    OverwriteEmptySource = true,
                    ExistingSourceAction = existingSourceAction
                });
                log($"INI CustomLinks: {source} -> {Path.GetRelativePath(portableRoot, target)}{DescribeLauncherExistingSourceAction(existingSourceAction)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(gameFolder) && !string.IsNullOrWhiteSpace(linkedGameFolder))
        {
            profile.Links.Add(new LinkRule
            {
                Name = Path.GetFileName(gameFolder.TrimEnd('\\')) is { Length: > 0 } name ? name : "GameFolder",
                Source = gameFolder,
                Target = linkedGameFolder,
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true,
                ExistingSourceAction = LauncherExistingSourceAction(backupFolders, "GameFolder", "LinkedGameFolder")
            });
            log($"INI LinkedGameFolder: {gameFolder} -> {linkedGameFolder}");
        }

        if (!string.IsNullOrWhiteSpace(gameFolder) && ini.TryGetValue("GameLinks", out var gameLinks))
        {
            foreach (var (folderName, targetPath) in gameLinks)
            {
                if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(targetPath))
                {
                    continue;
                }

                var source = Path.Combine(gameFolder, folderName);
                var target = ExpandLauncherValue(targetPath);
                var existingSourceAction = LauncherExistingSourceAction(backupFolders, "GameLinks", folderName);
                profile.Links.Add(new LinkRule
                {
                    Name = folderName,
                    Source = source,
                    Target = target,
                    Kind = LinkKind.Junction,
                    MoveExisting = true,
                    OverwriteEmptySource = true,
                    ExistingSourceAction = existingSourceAction
                });
                log($"INI GameLinks: {source} -> {target}{DescribeLauncherExistingSourceAction(existingSourceAction)}");
            }
        }

        var regsRoot = Path.Combine(dataRoot, "Regs");
        if (Directory.Exists(regsRoot))
        {
            foreach (var reg in Directory.GetFiles(regsRoot, "*.reg", SearchOption.AllDirectories).OrderBy(path => path))
            {
                profile.RegistryFiles.Add(@"{portableRoot}\" + Path.GetRelativePath(portableRoot, reg));
                log($"INI Regs: {reg}");
            }
        }

        if (ini.TryGetValue("Services", out var services))
        {
            foreach (var (name, raw) in services)
            {
                var parts = raw.Split('|', StringSplitOptions.TrimEntries);
                if (string.IsNullOrWhiteSpace(name) || parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                profile.Services.Add(new ServiceRule
                {
                    Name = name,
                    BinaryPath = ResolveLauncherRelativePath(portableRoot, ExpandLauncherValue(parts[0])),
                    StartMode = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "auto",
                    StartAfterApply = parts.Length <= 2 || !parts[2].Equals("false", StringComparison.OrdinalIgnoreCase)
                });
                log($"INI Service: {name}");
            }
        }

        var argSections = LauncherArgSections(ini).ToList();
        WriteLauncherIniPreRun(portableRoot, ini, argSections, log);

        if (!string.IsNullOrWhiteSpace(launcherExe))
        {
            var resolvedLauncherExe = ResolveLauncherRelativePath(portableRoot, launcherExe);
            var args = string.IsNullOrWhiteSpace(launcherArgs) ? "%*" : launcherArgs + " %*";
            if (argSections.Count > 0)
            {
                args = string.IsNullOrWhiteSpace(launcherArgs) ? "%CPL_LAUNCH_ARGS%" : launcherArgs + " %CPL_LAUNCH_ARGS%";
            }

            profile.Batches.Add(new BatchRule
            {
                Name = string.IsNullOrWhiteSpace(launcherName) ? appName : launcherName,
                Path = @"{portableRoot}\Run.cmd",
                TargetExe = resolvedLauncherExe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(resolvedLauncherExe) ?? "{portableRoot}"
            });
            log($"INI LauncherExe: {resolvedLauncherExe}");
        }

        return true;
    }

    private static IEnumerable<string> LauncherArgSections(Dictionary<string, Dictionary<string, string>> ini)
    {
        return ini.Keys
            .Where(section => section.StartsWith("Arg_", StringComparison.OrdinalIgnoreCase))
            .Select(section => section[4..])
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section, StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteLauncherIniPreRun(
        string portableRoot,
        Dictionary<string, Dictionary<string, string>> ini,
        IReadOnlyCollection<string> argSections,
        Action<string> log)
    {
        var hiddenRoot = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName);
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("setlocal")
            .AppendLine("set \"CPL_LAUNCH_ARGS=\"")
            .AppendLine("call :base")
            .AppendLine("for %%A in (%*) do call :handle_arg \"%%~A\"")
            .AppendLine("endlocal & set \"CPL_LAUNCH_ARGS=%CPL_LAUNCH_ARGS%\"")
            .AppendLine("exit /b 0")
            .AppendLine()
            .AppendLine(":handle_arg")
            .AppendLine("set \"CPL_ARG=%~1\"");

        var argLabels = argSections.ToDictionary(arg => arg, SafeBatchLabel, StringComparer.OrdinalIgnoreCase);
        foreach (var (arg, label) in argLabels)
        {
            script.AppendLine($"if /I \"%CPL_ARG%\"==\"-{arg}\" call :{label} & exit /b 0");
        }

        script
            .AppendLine("if defined CPL_LAUNCH_ARGS (set \"CPL_LAUNCH_ARGS=%CPL_LAUNCH_ARGS% %~1\") else set \"CPL_LAUNCH_ARGS=%~1\"")
            .AppendLine("exit /b 0")
            .AppendLine()
            .AppendLine(":base");

        var commandCount = 0;
        if (ini.TryGetValue("Registry", out var registry))
        {
            commandCount += AppendIniRegistryCommands(script, registry, key => key);
        }

        if (ini.TryGetValue("Settings", out var settings))
        {
            commandCount += AppendIniPreExeCommands(script, settings);
        }

        script.AppendLine("exit /b 0");

        foreach (var (arg, label) in argLabels)
        {
            script
                .AppendLine()
                .AppendLine($":{label}");

            if (ini.TryGetValue("Arg_" + arg, out var argSection))
            {
                commandCount += AppendIniPreExeCommands(script, argSection);
                commandCount += AppendIniRegistryCommands(
                    script,
                    argSection.Where(item => item.Key.StartsWith("Reg_", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
                    key => key[4..]);
            }

            script.AppendLine("exit /b 0");
        }

        if (commandCount == 0 && argSections.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(hiddenRoot);
        var launcherPreRunPath = Path.Combine(hiddenRoot, "LauncherIniPreRun.cmd");
        File.WriteAllText(launcherPreRunPath, script.ToString(), new UTF8Encoding(false));

        var portablePreRunPath = Path.Combine(hiddenRoot, "PortablePreRun.cmd");
        const string callLine = "call \"%~dp0LauncherIniPreRun.cmd\" %*";
        if (File.Exists(portablePreRunPath))
        {
            var existing = File.ReadAllText(portablePreRunPath);
            if (!existing.Contains("LauncherIniPreRun.cmd", StringComparison.OrdinalIgnoreCase))
            {
                File.AppendAllText(portablePreRunPath, Environment.NewLine + callLine + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        else
        {
            File.WriteAllText(portablePreRunPath, "@echo off" + Environment.NewLine + callLine + Environment.NewLine, new UTF8Encoding(false));
        }

        log($"INI pre-run: добавлены registry/pre-exe/arg правила ({commandCount}).");
    }

    private static int AppendIniRegistryCommands(
        StringBuilder script,
        IReadOnlyDictionary<string, string> values,
        Func<string, string> valueNameSelector)
    {
        var count = 0;
        foreach (var (rawName, rawValue) in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var valueName = valueNameSelector(rawName).Trim();
            if (string.IsNullOrWhiteSpace(valueName))
            {
                continue;
            }

            var parts = rawValue.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var key = NormalizeLauncherBatchValue(parts[0]);
            if (parts.Length == 2 && parts[1].Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                script.AppendLine($"reg delete \"{EscapeBatchQuoted(key)}\" /v \"{EscapeBatchQuoted(valueName)}\" /f >nul 2>nul");
                count++;
                continue;
            }

            var type = NormalizeRegType(parts.Length >= 2 ? parts[1] : "");
            var data = parts.Length >= 3 ? NormalizeLauncherBatchValue(parts[2]) : "";
            script.AppendLine($"reg add \"{EscapeBatchQuoted(key)}\" /v \"{EscapeBatchQuoted(valueName)}\" /t {type} /d \"{EscapeBatchQuoted(data)}\" /f >nul 2>nul");
            count++;
        }

        return count;
    }

    private static int AppendIniPreExeCommands(StringBuilder script, IReadOnlyDictionary<string, string> values)
    {
        var count = 0;
        foreach (var raw in OrderedPreExeValues(values))
        {
            var command = NormalizeLauncherBatchValue(raw);
            var (exe, args) = SplitLauncherCommand(command);
            if (string.IsNullOrWhiteSpace(exe))
            {
                continue;
            }

            script.AppendLine($"if exist \"{EscapeBatchQuoted(exe)}\" start \"\" \"{EscapeBatchQuoted(exe)}\" {args}".TrimEnd());
            script.AppendLine("timeout /t 1 /nobreak >nul 2>nul");
            count++;
        }

        return count;
    }

    private static IEnumerable<string> OrderedPreExeValues(IReadOnlyDictionary<string, string> values)
    {
        return values
            .Where(item => IsPreExeKey(item.Key))
            .OrderBy(item => item.Key.Equals("PreExe", StringComparison.OrdinalIgnoreCase) ? 0 : PreExeIndex(item.Key))
            .Select(item => item.Value);
    }

    private static bool IsPreExeKey(string key)
    {
        if (key.Equals("PreExe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return key.StartsWith("PreExe", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key["PreExe".Length..], out _);
    }

    private static int PreExeIndex(string key)
    {
        return int.TryParse(key["PreExe".Length..], out var index) ? index : int.MaxValue;
    }

    private static (string Exe, string Args) SplitLauncherCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return ("", "");
        }

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0)
            {
                return (command[1..end], command[(end + 1)..].Trim());
            }

            return (command.Trim('"'), "");
        }

        var exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            var exeEnd = exeIndex + 4;
            return (command[..exeEnd].Trim(), command[exeEnd..].Trim());
        }

        var split = command.IndexOf(' ');
        return split > 0 ? (command[..split].Trim(), command[(split + 1)..].Trim()) : (command, "");
    }

    private static string NormalizeLauncherBatchValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppDataLocal"] = "%LOCALAPPDATA%",
            ["AppDataRoaming"] = "%APPDATA%",
            ["ProgramData"] = "%ProgramData%",
            ["ProgramFiles"] = "%ProgramFiles%",
            ["ProgramFilesx86"] = "%ProgramFiles(x86)%",
            ["UserProfile"] = "%USERPROFILE%",
            ["Desktop"] = "%USERPROFILE%\\Desktop",
            ["Documents"] = "%USERPROFILE%\\Documents",
            ["Windows"] = "%WINDIR%",
            ["System32"] = "%WINDIR%\\System32",
            ["Temp"] = "%TEMP%",
            ["CommonAppData"] = "%ProgramData%"
        };

        var result = value.Trim();
        foreach (var (token, replacement) in replacements)
        {
            result = result
                .Replace("%" + token + "%", replacement, StringComparison.OrdinalIgnoreCase)
                .Replace("{" + token + "}", replacement, StringComparison.OrdinalIgnoreCase)
                .Replace("$" + token, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string SafeBatchLabel(string value)
    {
        var label = Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "arg";
        }

        return char.IsDigit(label[0]) ? "arg_" + label : "arg_" + label;
    }

    private static string EscapeBatchQuoted(string value)
    {
        return value.Replace("\"", "\\\"");
    }

    // Аргументы запуска из внешнего config.ini идут в Run.cmd как `start ... <args>`.
    // Убираем cmd-метасимволы (& | < > ^), которыми можно дописать произвольную
    // команду; обычным флагам/путям/кавычкам это не мешает.
    private static string SanitizeLauncherArgs(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        return new string(value.Where(c => c is not ('&' or '|' or '<' or '>' or '^')).ToArray());
    }

    // Тип reg-значения из ini подставляется в Run.cmd ВНЕ кавычек (/t TYPE),
    // поэтому строго ограничиваем его белым списком: иначе мусор/опечатка в ini
    // (или «REG_SZ /f &amp; del ...») попали бы в батник как команда. Неизвестное → REG_SZ.
    private static string NormalizeRegType(string rawType)
    {
        var type = (rawType ?? "").Trim().ToUpperInvariant();
        var allowed = new[]
        {
            "REG_SZ", "REG_MULTI_SZ", "REG_EXPAND_SZ",
            "REG_DWORD", "REG_QWORD", "REG_BINARY", "REG_NONE"
        };
        return Array.IndexOf(allowed, type) >= 0 ? type : "REG_SZ";
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIniFile(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!result.ContainsKey(section))
                {
                    result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0 || string.IsNullOrWhiteSpace(section))
            {
                continue;
            }

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            result[section][key] = value;
        }

        return result;
    }

    private static string IniValue(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
    {
        return ini.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
            ? value
            : "";
    }

    private static HashSet<string> LauncherBackupFolders(string value)
    {
        return value
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ExistingSourceAction LauncherExistingSourceAction(IReadOnlySet<string> backupFolders, params string[] names)
    {
        if (backupFolders.Count == 0)
        {
            return ExistingSourceAction.MoveToTargetOrBackup;
        }

        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (backupFolders.Contains(name) || backupFolders.Contains(KnownLauncherDataFolder(name)))
            {
                return ExistingSourceAction.BackupOnly;
            }
        }

        return ExistingSourceAction.MoveToTargetOrBackup;
    }

    private static string DescribeLauncherExistingSourceAction(ExistingSourceAction action)
    {
        return action == ExistingSourceAction.BackupOnly ? " (backup local)" : "";
    }

    private static string KnownLauncherDataFolder(string key)
    {
        var known = new[] { "AppDataLocal", "AppDataRoaming", "ProgramData", "ProgramFilesx86", "ProgramFiles" };
        foreach (var folder in known)
        {
            if (key.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }

            if (key.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key[folder.Length..], out _))
            {
                return folder;
            }
        }

        return key;
    }

    private static string KnownLauncherFolderRoot(string key)
    {
        var normalized = KnownLauncherDataFolder(key);
        return normalized.Equals("AppDataLocal", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : normalized.Equals("AppDataRoaming", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : normalized.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                    : normalized.Equals("ProgramFiles", StringComparison.OrdinalIgnoreCase)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                        : normalized.Equals("ProgramFilesx86", StringComparison.OrdinalIgnoreCase)
                            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                            : "";
    }

    private static string ExpandLauncherValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppDataLocal"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["AppDataRoaming"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFilesx86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["UserProfile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["Desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ["Documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["Windows"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["System32"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
            ["Temp"] = Path.GetTempPath().TrimEnd('\\'),
            ["CommonAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        var result = value;
        foreach (var (token, replacement) in replacements)
        {
            if (string.IsNullOrWhiteSpace(replacement))
            {
                continue;
            }

            result = result
                .Replace("%" + token + "%", replacement, StringComparison.OrdinalIgnoreCase)
                .Replace("{" + token + "}", replacement, StringComparison.OrdinalIgnoreCase)
                .Replace("$" + token, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return Environment.ExpandEnvironmentVariables(result);
    }

    private static string ResolveLauncherRelativePath(string portableRoot, string value)
    {
        if (Path.IsPathFullyQualified(value))
        {
            return value;
        }

        return ToPortableTokenPath(portableRoot, Path.Combine(portableRoot, value));
    }

    private static string ToPortableTokenPath(string portableRoot, string path)
    {
        var fullRoot = Path.GetFullPath(portableRoot).TrimEnd('\\') + "\\";
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? @"{portableRoot}\" + Path.GetRelativePath(portableRoot, fullPath)
            : fullPath;
    }

    private static void AddPortableLayoutLinks(AppProfile profile, string portableRoot, string appName, Action<string> log)
    {
        AddLayoutChildren(profile, portableRoot, "ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), log);
        AddLayoutChildren(profile, portableRoot, "ProgramFilesX86", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), log);
        AddLayoutChildren(profile, portableRoot, "ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), log);
        AddLayoutChildren(profile, portableRoot, "Local", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), log);
        AddLayoutChildren(profile, portableRoot, "Roaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), log);
        // Совместимость со старыми пакетами: раньше папки назывались UserLocal/UserRoaming.
        AddLayoutChildren(profile, portableRoot, "UserLocal", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), log);
        AddLayoutChildren(profile, portableRoot, "UserRoaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), log);

        var appData = Path.Combine(portableRoot, "AppData");
        if (Directory.Exists(appData))
        {
            profile.Links.Add(new LinkRule
            {
                Name = appName + " AppData",
                Source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName),
                Target = @"{portableRoot}\AppData",
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true
            });
            log($"Layout AppData: %APPDATA%\\{appName} -> AppData");
        }
    }

    private static void AddLayoutChildren(AppProfile profile, string portableRoot, string folderName, string windowsRoot, Action<string> log)
    {
        var folder = Path.Combine(portableRoot, folderName);
        if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(windowsRoot))
        {
            return;
        }

        foreach (var child in Directory.GetDirectories(folder))
        {
            var name = Path.GetFileName(child);
            profile.Links.Add(new LinkRule
            {
                Name = name,
                Source = Path.Combine(windowsRoot, name),
                Target = @"{portableRoot}\" + folderName + "\\" + name,
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true
            });
            log($"Layout {folderName}: {Path.Combine(windowsRoot, name)} -> {folderName}\\{name}");
        }
    }

    private static void AddPortableLayoutRegistry(AppProfile profile, string portableRoot, Action<string> log)
    {
        foreach (var folderName in new[] { "Reg", "Registry" })
        {
            var folder = Path.Combine(portableRoot, folderName);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var reg in Directory.GetFiles(folder, "*.reg", SearchOption.AllDirectories).OrderBy(path => path))
            {
                profile.RegistryFiles.Add(@"{portableRoot}\" + Path.GetRelativePath(portableRoot, reg));
                log($"Reg из layout: {reg}");
            }
        }
    }

    private static List<string> DetectChangedDirectories(InstallSnapshot before, IReadOnlyCollection<string> tokens, Action<string> log)
    {
        var beforeMap = before.Directories.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();
        var after = CaptureDirectories();

        foreach (var entry in after)
        {
            var name = Path.GetFileName(entry.Path);
            if (!MatchesTokens(name, tokens))
            {
                continue;
            }

            if (!beforeMap.TryGetValue(entry.Path, out var old) || entry.LastWriteUtc > old.LastWriteUtc.AddSeconds(2))
            {
                changed.Add(entry.Path);
            }
        }

        changed = DeduplicatePaths(changed);
        log($"После установки найдено папок: {changed.Count}");
        foreach (var path in changed)
        {
            log($"  найдено: {path}");
        }

        return changed;
    }

    private static string? InferMainFolder(IReadOnlyCollection<string> changed, Action<string> log)
    {
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var inferred = changed
            .Where(path => programFiles.Any(root => IsUnderRoot(path, root)))
            .OrderByDescending(path => SafeEnumerateExe(path).Any() ? 1 : 0)
            .ThenBy(path => path.Length)
            .FirstOrDefault()
            ?? changed.OrderBy(path => path.Length).FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(inferred))
        {
            log($"Главная папка не указана, выбрана автоматически: {inferred}");
        }

        return inferred;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root).TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase);
    }

    // Папки вида "<имя>.bak.<число>" создаёт :relink в Run.cmd как резервную копию
    // при перепривязке junction. В свежий портабл их тащить НЕЛЬЗЯ: это бэкап-мусор,
    // он раздувает пакет (могут быть сотни МБ) и плодит дубли/вложенные ссылки.
    private static bool IsRelinkBackup(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return System.Text.RegularExpressions.Regex.IsMatch(
            name, @"\.bak\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static List<DirectoryProbe> CaptureDirectories()
    {
        var result = new List<DirectoryProbe>();
        foreach (var root in WatchRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in EnumerateTopDirectories(root))
            {
                try
                {
                    result.Add(new DirectoryProbe
                    {
                        Root = root,
                        Path = Path.GetFullPath(directory),
                        LastWriteUtc = Directory.GetLastWriteTimeUtc(directory)
                    });
                }
                catch
                {
                    // Some system folders can race with installers. They are not critical for detection.
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> WatchRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    private static IEnumerable<string> EnumerateTopDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? MapPortableTarget(string source)
    {
        var full = Path.GetFullPath(source).TrimEnd('\\');
        return TryMap(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProgramFiles")
            ?? TryMap(full, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ProgramFilesX86")
            ?? TryMap(full, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData")
            ?? TryMap(full, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Local")
            ?? TryMap(full, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roaming");
    }

    private static string? TryMap(string full, string root, string portableFolder)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return @"{portableRoot}\" + portableFolder + "\\" + Path.GetRelativePath(normalizedRoot, full);
    }

    private static string? FindBestLauncher(IEnumerable<string> roots, IReadOnlyCollection<string> tokens, Action<string> log)
    {
        var candidates = new List<(string Path, int Score)>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var exe in SafeEnumerateExe(root).Take(5000))
            {
                var score = ScoreLauncher(exe, tokens);
                if (score > 0)
                {
                    candidates.Add((exe, score));
                }
            }
        }

        var best = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path.Length)
            .FirstOrDefault();

        if (best.Path is not null)
        {
            log($"Лучший .exe: {best.Path} (score {best.Score})");
        }

        return best.Path;
    }

    private static IEnumerable<string> SafeEnumerateExe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var budget = 20000; // потолок просмотренных папок — без зависаний на огромных деревьях
        while (pending.Count > 0 && budget-- > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files = [];
            IEnumerable<string> directories = [];

            try
            {
                files = Directory.EnumerateFiles(current, "*.exe");
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var directory in directories)
            {
                // Не идём по junction/symlink — иначе уйдём в цель ссылки (в т.ч. в саму
                // portable-папку при повторной сборке) и можем зациклиться.
                try
                {
                    if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                pending.Push(directory);
            }
        }
    }

    private static int ScoreLauncher(string exe, IReadOnlyCollection<string> tokens)
    {
        var fileName = Path.GetFileNameWithoutExtension(exe);
        var lowerPath = exe.ToLowerInvariant();
        var lowerName = fileName.ToLowerInvariant();

        if (LauncherBadWords.Any(word => lowerName.Contains(word, StringComparison.OrdinalIgnoreCase)))
        {
            return -100;
        }

        var score = 0;
        foreach (var token in tokens)
        {
            var lowerToken = token.ToLowerInvariant();
            if (lowerName.Contains(lowerToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }
            else if (lowerPath.Contains(lowerToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
        }

        if (lowerName.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            lowerName.Contains("client", StringComparison.OrdinalIgnoreCase) ||
            lowerName.Contains("player", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        try
        {
            score += (int)Math.Min(new FileInfo(exe).Length / 1024 / 1024, 30);
        }
        catch
        {
            // ignore
        }

        return score;
    }

    private static void ExportRegistry(AppProfile profile, string portableRoot, IReadOnlyCollection<string> tokens, ISet<string> confirmed, Action<string> log)
    {
        var registryRoot = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "Registry");
        Directory.CreateDirectory(registryRoot);

        var exported = 0;
        var keys = RegistryCandidateKeys(tokens)
            .Concat(SearchRegistryKeys(tokens, log))
            .Concat(UninstallRegistryKeys(tokens))
            .GroupBy(NormalizeRegistryKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(key => !IsTooBroadRegistryKey(key))
            .Where(key => !IsForeignLauncherKey(key, confirmed, log))
            .OrderBy(key => key.Length)
            .ToList();

        foreach (var key in keys)
        {
            if (!ProcessReturnsZero("reg.exe", $"query \"{key}\""))
            {
                continue;
            }

            var fileName = SafeFileName(profile.Name + "__" + FriendlyRegistryName(key)) + ".reg";
            var filePath = Path.Combine(registryRoot, fileName);
            if (ProcessReturnsZero("reg.exe", $"export \"{key}\" \"{filePath}\" /y"))
            {
                profile.RegistryFiles.Add(@"{portableRoot}\" + ConfigStore.PortableDirectoryName + @"\Registry\" + fileName);
                exported++;
                log($"Реестр экспортирован: {key}");
            }
        }

        if (exported == 0)
        {
            log("Связанные ключи реестра не найдены.");
        }
        else
        {
            log($"Reg-файлов добавлено в portable: {exported}");
        }
    }

    private static IEnumerable<string> RegistryCandidateKeys(IReadOnlyCollection<string> tokens)
    {
        foreach (var token in tokens)
        {
            yield return $@"HKEY_CURRENT_USER\SOFTWARE\{token}";
            yield return $@"HKEY_LOCAL_MACHINE\SOFTWARE\{token}";
            yield return $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\{token}";
        }
    }

    // Ключ принадлежит ИЗВЕСТНОЙ платформе-лаунчеру? Если да и эта платформа НЕ
    // подтверждена (не её мы собираем) — это чужой ключ (напр. Steam при сборке Ubisoft),
    // его в пакет тащить нельзя. Защищает generic-экспорт от кросс-контаминации, когда
    // в paths/токены затесалась папка другого лаунчера.
    private static readonly (string Fragment, string App)[] ForeignVendorFragments =
    [
        (@"\Valve\Steam", "Steam"), ("Steam App", "Steam"),
        ("Epic Games", "Epic"), ("EpicGames", "Epic"),
        ("Blizzard Entertainment", "BattleNet"), ("Battle.net", "BattleNet"),
        ("Electronic Arts", "EA"), ("EA Desktop", "EA"), (@"\Origin", "EA"),
        ("Ubisoft", "Ubisoft"), ("Uplay", "Ubisoft"),
        ("GOG.com", "GOG"),
        ("Rockstar Games", "Rockstar"),
        ("Wargaming.net", "Wargaming"),
        (@"\Lesta", "Lesta"),
        ("Riot Games", "Riot"),
        ("Battlestate Games", "BattleState"),
        (@"\4game", "4game"), (@"\Innova", "4game"),
        ("BlueStacks", "BlueStacks"),
        ("Roberts Space Industries", "RSI"), ("Cloud Imperium", "RSI"),
        ("RAGE-MP", "RAGEMP"),
        ("FACEIT", "FACEIT"),
        ("VK Play", "VKPlay"), ("VKPlay", "VKPlay"), ("Mail.Ru", "VKPlay"),
    ];

    private static bool IsForeignLauncherKey(string key, ISet<string> confirmed, Action<string> log)
    {
        foreach (var (fragment, app) in ForeignVendorFragments)
        {
            if (ContainsVendorFragment(key, fragment) && !confirmed.Contains(app))
            {
                log($"Пропущен чужой ключ ({app}, не собирается): {key}");
                return true;
            }
        }

        return false;
    }

    // Фрагмент должен заканчиваться на ГРАНИЦЕ сегмента пути (дальше '\' или конец):
    // голый Contains ложно метил «…\Original War» как Origin (EA) и «…\Innovative Soft»
    // как Innova (4game) — ключи СВОЕЙ программы выкидывались из экспорта как чужие.
    private static bool ContainsVendorFragment(string key, string fragment)
    {
        var start = 0;
        while (true)
        {
            var idx = key.IndexOf(fragment, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            var end = idx + fragment.Length;
            if (end >= key.Length || key[end] == '\\' || key[end] == ' ')
            {
                return true;
            }

            start = idx + 1;
        }
    }

    private static IEnumerable<string> SearchRegistryKeys(IReadOnlyCollection<string> tokens, Action<string> log)
    {
        var keyRoots = new[]
        {
            @"HKCU\SOFTWARE",
            @"HKLM\SOFTWARE",
            @"HKLM\SOFTWARE\WOW6432Node"
        };

        foreach (var token in tokens.Where(token => token.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var root in keyRoots)
            {
                var output = CaptureProcessOutput("reg.exe", $"query \"{root}\" /f \"{token}\" /k");
                foreach (var key in ParseRegQueryKeys(output, token))
                {
                    if (IsUsefulRegistryKey(key, token))
                    {
                        yield return key;
                    }
                }
            }

        }
    }

    private static IEnumerable<string> UninstallRegistryKeys(IReadOnlyCollection<string> tokens)
    {
        var strongTokens = tokens
            .Where(IsStrongRegistryToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (strongTokens.Count == 0)
        {
            yield break;
        }

        var roots = new (RegistryKey Hive, string SubKey, string FullPath)[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, subKey, fullPath) in roots)
        {
            using var root = hive.OpenSubKey(subKey);
            if (root is null)
            {
                continue;
            }

            foreach (var childName in root.GetSubKeyNames())
            {
                using var child = root.OpenSubKey(childName);
                if (child is null)
                {
                    continue;
                }

                var haystack = string.Join(" ", new[]
                {
                    child.GetValue("DisplayName")?.ToString(),
                    child.GetValue("InstallLocation")?.ToString(),
                    child.GetValue("UninstallString")?.ToString(),
                    child.GetValue("QuietUninstallString")?.ToString(),
                    child.GetValue("DisplayIcon")?.ToString(),
                    child.GetValue("Publisher")?.ToString(),
                    child.GetValue("MenuDirectory")?.ToString()
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

                if (strongTokens.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return fullPath + "\\" + childName;
                }
            }
        }
    }

    private static bool IsStrongRegistryToken(string token)
    {
        return token.Length >= 4 && !TokenStopWords.Contains(token, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUsefulRegistryKey(string key, string token)
    {
        if (IsTooBroadRegistryKey(key))
        {
            return false;
        }

        // Для Uninstall-ветки токен должен быть в ИМЕНИ листового подключа
        // (…\Uninstall\<имя с токеном>, без дальнейшей вложенности). Прежняя форма
        // была тавтологией (всегда true) — фильтр не работал, и /f-поиск мог
        // протащить «не свои» uninstall-записи.
        const string uninstallMarker = @"\microsoft\windows\currentversion\uninstall";
        var lower = NormalizeRegistryKey(key).ToLowerInvariant();
        var idx = lower.IndexOf(uninstallMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return true;
        }

        var leaf = lower[(idx + uninstallMarker.Length)..].TrimStart('\\');
        return leaf.Length > 0
            && !leaf.Contains('\\')
            && leaf.Contains(token.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static bool IsTooBroadRegistryKey(string key)
    {
        var normalized = NormalizeRegistryKey(key).TrimEnd('\\');
        var broadKeys = new[]
        {
            @"HKEY_CURRENT_USER\SOFTWARE",
            @"HKEY_LOCAL_MACHINE\SOFTWARE",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node"
        };

        return broadKeys.Any(item => string.Equals(normalized, item, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ParseRegQueryKeys(string output, string token)
    {
        string? currentKey = null;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in SplitLines(output))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                currentKey = line;
            }

            if (currentKey is not null &&
                line.Contains(token, StringComparison.OrdinalIgnoreCase) &&
                emitted.Add(currentKey))
            {
                yield return currentKey;
            }
        }
    }

    private static bool ProcessReturnsZero(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        if (process is null)
        {
            return false;
        }

        // Сливаем оба перенаправлённых потока, иначе чатти-процесс может
        // заполнить буфер pipe, заблокироваться и ложно «истечь по таймауту».
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ExternalProcessTimeoutMs))
        {
            TryKill(process);
            return false;
        }

        outTask.GetAwaiter().GetResult();
        errTask.GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }

    private static string CaptureProcessOutput(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        if (process is null)
        {
            return "";
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ExternalProcessTimeoutMs))
        {
            TryKill(process);
            return "";
        }

        return outputTask.GetAwaiter().GetResult() + Environment.NewLine + errorTask.GetAwaiter().GetResult();
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between timeout detection and kill.
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyCollection<string> BuildTokens(string appName, string mainFolder)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTokenSet(values, appName);

        if (!string.IsNullOrWhiteSpace(mainFolder))
        {
            AddTokenSet(values, Path.GetFileName(mainFolder.TrimEnd('\\')));
        }

        var joined = string.Concat(SplitWords(appName));
        if (joined.Length >= 3)
        {
            values.Add(joined);
            values.Add(joined + "Launcher");
        }

        return values.Where(value => value.Length >= 3).ToList();
    }

    private static IReadOnlyCollection<string> EnrichTokens(IReadOnlyCollection<string> tokens, IEnumerable<string> paths)
    {
        var values = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            AddTokenSet(values, Path.GetFileName(path.TrimEnd('\\')));
        }

        return values.Where(value => value.Length >= 3).ToList();
    }

    private static void AddTokenSet(HashSet<string> values, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        // НЕ добавляем токен, который сам является стоп-словом ("Launcher", "App",
        // "Games"…): иначе leaf-папка вроде "…\Rockstar Games\Launcher" даёт токен
        // "launcher", и он матчит ВСЕ чужие лаунчеры (Epic/Ubisoft/RSI/DayZ) —
        // их данные утекают в чужой пакет. Многословные имена ("Ubisoft Game
        // Launcher") стоп-словом не являются и остаются (они специфичны).
        if (!TokenStopWords.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(trimmed);
        }

        var compactRaw = Regex.Replace(trimmed, @"[^\p{L}\p{Nd}]+", "");
        if (compactRaw.Length >= 3 && !TokenStopWords.Contains(compactRaw, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(compactRaw);
        }

        var words = SplitWords(value)
            .Where(word => !TokenStopWords.Contains(word, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var word in words)
        {
            values.Add(word);
        }

        if (words.Count > 1)
        {
            values.Add(string.Join(" ", words));
            values.Add(string.Concat(words));
        }
    }

    private static IEnumerable<string> SplitWords(string value)
    {
        return WordRegex()
            .Matches(value)
            .Select(match => match.Value)
            .Where(word => word.Length >= 3);
    }

    private static bool MatchesTokens(string value, IReadOnlyCollection<string> tokens)
    {
        return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTemporaryInstallerFolder(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        var compact = Regex.Replace(name, @"[^\p{L}\p{Nd}]+", "").ToLowerInvariant();
        if (string.IsNullOrEmpty(compact))
        {
            return false;
        }

        // Точное совпадение имени с любым маркером — самый надёжный сигнал.
        if (TemporaryFolderExactMarkers.Any(marker =>
                compact.Equals(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Подстрока — только для однозначно установочных маркеров.
        return InstallerTempSubstringMarkers.Any(marker =>
            compact.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanName(string appName, string mainFolder)
    {
        if (!string.IsNullOrWhiteSpace(appName))
        {
            return appName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(mainFolder))
        {
            return Path.GetFileName(mainFolder.TrimEnd('\\'));
        }

        return "PortableApp";
    }

    // Пишет .portable\build-report.txt: что перенесено, reg, запуск, предупреждения —
    // чтобы было видно, «норм ли собрался» портабл, и как проверить целостность.
    private static void WriteBuildReport(AppProfile profile, string portableRoot, Action<string> log)
    {
        try
        {
            var lines = new List<string>
            {
                "ClubPortableLinker — отчёт сборки",
                $"Дата:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Платформа: {profile.Name}",
                $"Пакет:     {portableRoot}",
            };

            var root = (Path.GetPathRoot(Path.GetFullPath(portableRoot)) ?? "").TrimEnd('\\');
            var sysRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "").TrimEnd('\\');
            if (string.Equals(root, sysRoot, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add("ВНИМАНИЕ: пакет на системном диске (C:). Для бездисковых ПК лучше игровой диск (D:/E:).");
            }

            lines.Add("");
            lines.Add($"Ссылки ({profile.Links.Count}) — путь Windows -> расположение в пакете:");
            foreach (var l in profile.Links)
            {
                lines.Add($"  {PathTokens.Expand(l.Source, profile)}  ->  {PathTokens.Expand(l.Target, profile)}");
            }

            lines.Add("");
            lines.Add($"Reg-файлы ({profile.RegistryFiles.Count}):");
            foreach (var r in profile.RegistryFiles)
            {
                lines.Add("  " + Path.GetFileName(PathTokens.Expand(r, profile)));
            }

            lines.Add("");
            lines.Add($"Запуск:  {string.Join(", ", profile.Batches.Select(b => b.Name))}");
            lines.Add($"Службы:  {profile.Services.Count}   Задачи: {profile.Tasks.Count}   Игры: {profile.Games.Count}");
            lines.Add("");
            lines.Add($"Проверка целостности: ClubPortableLinker.exe --verify-package --package \"{portableRoot}\"");

            var reportPath = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "build-report.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            // UTF-8 С BOM — чтобы кириллица корректно открывалась в любом редакторе.
            File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines), new System.Text.UTF8Encoding(true));
            log($"Отчёт сборки: {reportPath}");
        }
        catch (Exception ex)
        {
            log("Отчёт сборки не записан: " + ex.Message);
        }
    }

    private static string RequirePortableRoot(string portableRoot)
    {
        if (string.IsNullOrWhiteSpace(portableRoot))
        {
            throw new InvalidOperationException("Укажите папку, куда собрать portable.");
        }

        return Path.GetFullPath(portableRoot);
    }

    private static void Deduplicate(AppProfile profile, Action<string> log)
    {
        // 1) Точные дубли по развёрнутому Source.
        var unique = profile.Links
            .GroupBy(link => Path.GetFullPath(PathTokens.Expand(link.Source, profile)).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        // 2) Отсечь .bak.<число> (бэкапы relink) и схлопнуть вложенность: ссылку,
        // чей Source лежит ВНУТРИ Source другой ссылки, убираем — родительская
        // ссылка уже переносит ребёнка (иначе junction-в-junction и двойной перенос).
        var ordered = unique
            .Select(l => (link: l, src: Path.GetFullPath(PathTokens.Expand(l.Source, profile)).TrimEnd('\\')))
            .Where(x => !IsRelinkBackup(x.src))
            .OrderBy(x => x.src.Length)
            .ToList();

        var keptLinks = new List<LinkRule>();
        var keptSources = new List<string>();
        foreach (var (link, src) in ordered)
        {
            var parent = keptSources.FirstOrDefault(root => IsUnderRoot(src, root));
            if (parent is not null)
            {
                log($"Пропуск вложенной ссылки {src} — уже покрыта родительской {parent}.");
                continue;
            }

            keptLinks.Add(link);
            keptSources.Add(src);
        }

        profile.Links = keptLinks;

        profile.RegistryFiles = profile.RegistryFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Несколько платформ пишут в один и тот же Run.cmd ({portableRoot}\Run.cmd).
        // Дедуп по Path оставит только первый запускатель — об этом надо
        // явно предупредить, иначе второй лаунчер молча потеряется.
        var batchGroups = profile.Batches
            .GroupBy(batch => batch.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in batchGroups.Where(g => g.Count() > 1))
        {
            var dropped = group.Skip(1).Select(b => b.Name);
            log($"Внимание: несколько запускателей пишут в один файл {group.Key}. " +
                $"Оставлен «{group.First().Name}», пропущены: {string.Join(", ", dropped)}. " +
                "Задайте им разные пути Run-файлов, если нужны оба.");
        }

        profile.Batches = batchGroups.Select(group => group.First()).ToList();
    }

    private static List<string> DeduplicatePaths(IEnumerable<string> paths)
    {
        var distinct = paths
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Where(p => !IsRelinkBackup(p))           // отсечь .bak.<число> (бэкапы relink)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)             // родитель (короче) раньше ребёнка
            .ToList();

        // Коллапс вложенности: путь ВНУТРИ уже принятого пропускаем — родительская
        // ссылка уже покрывает ребёнка (иначе получили бы junction внутри junction).
        var kept = new List<string>();
        foreach (var p in distinct)
        {
            if (kept.Any(root => IsUnderRoot(p, root)))
            {
                continue;
            }

            kept.Add(p);
        }

        return kept;
    }

    private static void CopyDirectory(string source, string destination)
    {
        // Рекурсия вручную (а не SearchOption.AllDirectories), чтобы НЕ заходить
        // внутрь junction/symlink: иначе обход пошёл бы по ссылке за пределы папки
        // и мог зациклиться или продублировать данные.
        var sourceRoot = Path.GetFullPath(source);
        var destRoot = Path.GetFullPath(destination);
        var skipRelative = ConfigStore.PortableDirectoryName + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destRoot);

        var stack = new Stack<string>();
        stack.Push(sourceRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var targetDir = Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, dir));
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                if (relative.StartsWith(skipRelative, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(file, Path.Combine(destRoot, relative), true);
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // junction/symlink не копируем как дерево — у них нет своих данных,
                // это лишь ссылка (часто на уже перенесённые портативные папки).
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string value)
    {
        var safe = value.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return safe;
    }

    private static string FriendlyRegistryName(string key)
    {
        var normalized = NormalizeRegistryKey(key);
        return normalized
            .Replace(@"HKEY_CURRENT_USER\SOFTWARE\", "HKCU_", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\", "HKLM_WOW6432_", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKEY_LOCAL_MACHINE\SOFTWARE\", "HKLM_", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKEY_CLASSES_ROOT\", "HKCR_", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKEY_USERS\", "HKU_", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '_');
    }

    private static string NormalizeRegistryKey(string key)
    {
        return key
            .Replace(@"HKCU\", @"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKLM\", @"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKCR\", @"HKEY_CLASSES_ROOT\", StringComparison.OrdinalIgnoreCase)
            .Replace(@"HKU\", @"HKEY_USERS\", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('\\');
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordRegex();
}
