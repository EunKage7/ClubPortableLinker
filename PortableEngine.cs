using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ClubPortableLinker;

public static class PortableEngine
{
    public static PackagePlan CreatePlan(AppProfile profile)
    {
        var issues = InspectProfile(profile);
        var links = profile.AllLinks().ToList();
        return new PackagePlan(
            profile.Name,
            PathTokens.Expand(profile.PortableRoot, profile),
            links.Count,
            links.Count(link => link.ExistingSourceAction == ExistingSourceAction.MoveToTargetOrBackup),
            links.Count(link => link.ExistingSourceAction == ExistingSourceAction.BackupOnly),
            links.Count(link => link.ExistingSourceAction == ExistingSourceAction.Stop),
            profile.AllRegistryFiles().Count(),
            profile.AllBatches().Count(),
            profile.Tasks.Count,
            profile.Services.Count,
            issues,
            profile.Games);
    }

    public static ExecutionResult Execute(AppProfile profile, ExecutionOptions options, Action<string> log)
    {
        var errors = 0;
        var plan = CreatePlan(profile);
        LogPlanHeader(plan, options.Apply, log);

        if (options.Apply && plan.HasBlockingIssues)
        {
            log("Применение остановлено: в плане есть опасные пути. Исправьте manifest и повторите.");
            return new ExecutionResult(false, 1);
        }

        if (options.Mode is OperationMode.All or OperationMode.Links)
        {
            foreach (var link in profile.AllLinks())
            {
                if (!RunSafely(() => ProcessLink(profile, link, options.Apply, log), log))
                {
                    errors++;
                }
            }
        }

        if (options.Mode is OperationMode.All or OperationMode.Registry or OperationMode.SafeMode)
        {
            foreach (var file in profile.AllRegistryFiles())
            {
                if (!RunSafely(() => ImportRegistry(profile, file, options.Apply, log), log))
                {
                    errors++;
                }
            }
        }

        if (options.Mode is OperationMode.All or OperationMode.Batches or OperationMode.SafeMode)
        {
            foreach (var batch in profile.AllBatches())
            {
                if (!RunSafely(() => WriteBatch(profile, batch, options.Apply, log), log))
                {
                    errors++;
                }
            }
        }

        if (options.Mode is OperationMode.All or OperationMode.Tasks)
        {
            foreach (var task in profile.Tasks)
            {
                if (!RunSafely(() => RegisterTask(profile, task, options.Apply, log), log))
                {
                    errors++;
                }
            }
        }

        if (options.Mode is OperationMode.All or OperationMode.Services)
        {
            foreach (var service in profile.Services)
            {
                if (!RunSafely(() => ConfigureService(profile, service, options.Apply, log), log))
                {
                    errors++;
                }
            }
        }

        return new ExecutionResult(errors == 0, errors);
    }

    private static void LogPlanHeader(PackagePlan plan, bool apply, Action<string> log)
    {
        log($"{(apply ? "ПРИМЕНЕНИЕ" : "ПЛАН")}: {plan.ProfileName} ({plan.PortableRoot})");
        log($"  Ссылок:  {plan.LinkCount}  (Move: {plan.MoveLinkCount}, BackupOnly: {plan.BackupOnlyLinkCount}, Stop: {plan.StopLinkCount})");
        log($"  Reg:     {plan.RegistryCount}");
        log($"  Batches: {plan.BatchCount}");
        log($"  Tasks:   {plan.TaskCount}");
        log($"  Services:{plan.ServiceCount}");
        if (plan.Games.Count > 0)
        {
            log($"  Игр внутри платформы: {plan.Games.Count(g => g.Enabled)} вкл. / {plan.Games.Count} всего");
            foreach (var game in plan.Games)
            {
                var mark = game.Enabled ? "+" : "-";
                log($"    [{mark}] {game.Name}: ссылок {game.Links.Count}, reg {game.RegistryFiles.Count}, batch {game.Batches.Count}");
            }
        }

        foreach (var issue in plan.Issues)
        {
            var label = issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)
                ? "БЛОК"
                : "Внимание";
            var path = string.IsNullOrWhiteSpace(issue.Path) ? "" : $" [{issue.Path}]";
            log($"  {label}: {issue.Message}{path}");
        }
    }

    private static IReadOnlyList<PlanIssue> InspectProfile(AppProfile profile)
    {
        var issues = new List<PlanIssue>();
        var isAdmin = IsAdministrator();

        foreach (var link in profile.AllLinks())
        {
            var source = SafeFullPath(PathTokens.Expand(link.Source, profile));
            var target = SafeFullPath(PathTokens.Expand(link.Target, profile));

            if (IsDangerousSourceRoot(source))
            {
                issues.Add(new PlanIssue(
                    "error",
                    "dangerous_source",
                    $"Опасный Windows-путь для ссылки «{link.Name}». Корневые и системные папки нельзя линковать.",
                    source));
            }

            if (!isAdmin && NeedsAdminForPath(source))
            {
                issues.Add(new PlanIssue(
                    "warning",
                    "admin_required",
                    $"Для ссылки «{link.Name}» нужны права администратора.",
                    source));
            }

            if (IsLikelyImageDrive(target))
            {
                issues.Add(new PlanIssue(
                    "warning",
                    "target_on_image_drive",
                    $"Данные ссылки «{link.Name}» лежат на C:. Для CCBOOT лучше держать portable на game-disk.",
                    target));
            }
        }

        foreach (var batch in profile.AllBatches())
        {
            var exe = SafeFullPath(PathTokens.Expand(batch.TargetExe, profile));
            if (!File.Exists(exe))
            {
                issues.Add(new PlanIssue(
                    "warning",
                    "launcher_missing",
                    $"Запускатель «{batch.Name}» пока не найден. Пакет будет считаться частично готовым.",
                    exe));
            }
        }

        foreach (var service in profile.Services)
        {
            var binary = SafeFullPath(PathTokens.Expand(service.BinaryPath, profile));
            if (!File.Exists(binary))
            {
                issues.Add(new PlanIssue(
                    "warning",
                    "service_binary_missing",
                    $"Файл службы «{service.Name}» пока не найден.",
                    binary));
            }
        }

        foreach (var registryFile in profile.AllRegistryFiles())
        {
            var path = SafeFullPath(PathTokens.Expand(registryFile, profile));
            if (!File.Exists(path))
            {
                issues.Add(new PlanIssue(
                    "warning",
                    "registry_missing",
                    "Reg-файл из manifest пока не найден.",
                    path));
            }
        }

        return issues;
    }

    private static bool RunSafely(Action action, Action<string> log)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            log("ОШИБКА: " + ex.Message);
            return false;
        }
    }

    private static void ProcessLink(AppProfile profile, LinkRule link, bool apply, Action<string> log)
    {
        var source = Path.GetFullPath(PathTokens.Expand(link.Source, profile));
        var target = Path.GetFullPath(PathTokens.Expand(link.Target, profile));

        log($"Ссылка: {link.Name}");
        log($"  путь Windows: {source}");
        log($"  данные на диске: {target}");
        log($"  тип: {DescribeKind(link.Kind)}");

        if (!apply)
        {
            return;
        }

        // Защита от потери данных: источник и цель не должны совпадать или быть вложены
        // друг в друга (перенос пошёл бы сам в себя), а цель — быть системным/корневым путём.
        if (IsSameOrUnder(source, target) || IsSameOrUnder(target, source))
        {
            log($"  ПРОПУСК: источник и цель совпадают или вложены друг в друга — во избежание потери данных");
            return;
        }

        if (IsDangerousSourceRoot(target))
        {
            log($"  ПРОПУСК: цель указывает на системный/корневой путь ({target})");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var sourceExists = Directory.Exists(source) || File.Exists(source);
        // ВИСЯЧИЙ junction (цель удалена/диск переключён): Directory.Exists и File.Exists
        // оба возвращают false, но reparse-точка на месте и mklink упадёт «уже существует».
        // GetAttributes видит атрибуты самой ссылки даже у битой — проверяем им.
        bool sourceIsLink;
        try
        {
            sourceIsLink = File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            sourceIsLink = false; // пути нет вовсе — нечего снимать
        }

        if (sourceIsLink)
        {
            DeleteLinkOnly(source);
            sourceExists = false;
            log("  старая ссылка убрана");
        }

        if (sourceExists)
        {
            sourceExists = PrepareExistingSource(profile, source, target, link, log);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        if (link.Kind is LinkKind.Junction or LinkKind.SymlinkDir)
        {
            Directory.CreateDirectory(target);
        }

        CreateLink(source, target, link.Kind);
        log("  ссылка создана");
    }

    private static bool PrepareExistingSource(AppProfile profile, string source, string target, LinkRule link, Action<string> log)
    {
        if (link.OverwriteEmptySource && IsEmptyDirectory(source))
        {
            Directory.Delete(source);
            log("  пустая папка Windows убрана перед созданием ссылки");
            return false;
        }

        return link.ExistingSourceAction switch
        {
            ExistingSourceAction.Stop => throw new InvalidOperationException("На пути Windows уже есть папка или файл. Остановлено, данные не тронуты."),
            ExistingSourceAction.BackupOnly => BackupExistingSource(profile, source, link.Kind, log),
            ExistingSourceAction.MoveToTargetOrBackup => MoveExistingSource(profile, source, target, link.Kind, log),
            _ => throw new InvalidOperationException("Неизвестный режим обработки существующего пути.")
        };
    }

    private static bool MoveExistingSource(AppProfile profile, string source, string target, LinkKind kind, Action<string> log)
    {
        if (Directory.Exists(source) && kind is LinkKind.Junction or LinkKind.SymlinkDir)
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                // Цель уже непустая — типичный случай: прошлый перенос упал на середине,
                // и в source остался ОСТАТОК (единственная копия недоехавших файлов).
                // Раньше остаток тихо уезжал в _Replaced и пакет оставался неполным.
                // Теперь ДОмерживаем остаток в цель (robocopy /MOVE в непустую папку).
                log("  цель непустая — домерживаю остаток источника в portable-папку...");
                MoveDirectoryResilient(source, target, log);
                log("  остаток источника домержен в portable-папку");
                return false;
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveDirectoryResilient(source, target, log);
            log("  существующая папка Windows перенесена в portable-папку");
            return false;
        }

        if (File.Exists(source) && kind is LinkKind.SymlinkFile or LinkKind.HardlinkFile)
        {
            if (File.Exists(target))
            {
                return BackupExistingSource(profile, source, kind, log);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            log("  существующий файл Windows перенесен в portable-папку");
            return false;
        }

        throw new InvalidOperationException("Тип ссылки не совпадает с существующим объектом на пути Windows.");
    }

    private static bool BackupExistingSource(AppProfile profile, string source, LinkKind kind, Action<string> log)
    {
        // KeepBackups=false (по умолчанию для клуба): данные уже есть в portable,
        // дублирующий путь Windows убираем в одну общую папку _Replaced без накопления
        // версий. KeepBackups=true: полноценный архив с датой в _Backups.
        var portableRoot = PathTokens.Expand(profile.PortableRoot, profile);
        var backupRoot = profile.KeepBackups
            ? Path.Combine(portableRoot, "_Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"))
            : Path.Combine(portableRoot, "_Replaced");
        var label = profile.KeepBackups ? "резерв" : "_Replaced";
        var backupTarget = Path.Combine(backupRoot, source.TrimEnd('\\').Replace(':', '_').Replace('\\', Path.DirectorySeparatorChar));

        // Без накопления версий старую копию в _Replaced перезаписываем.
        if (!profile.KeepBackups)
        {
            if (Directory.Exists(backupTarget)) Directory.Delete(backupTarget, recursive: true);
            else if (File.Exists(backupTarget)) File.Delete(backupTarget);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupTarget)!);

        if (Directory.Exists(source) && kind is LinkKind.Junction or LinkKind.SymlinkDir)
        {
            MoveDirectoryResilient(source, backupTarget, log);
            log($"  существующая папка Windows убрана в {label}: {backupTarget}");
            return false;
        }

        if (File.Exists(source) && kind is LinkKind.SymlinkFile or LinkKind.HardlinkFile)
        {
            File.Move(source, backupTarget);
            log($"  существующий файл Windows убран в {label}: {backupTarget}");
            return false;
        }

        throw new InvalidOperationException("Не удалось убрать существующий путь.");
    }

    private static bool IsEmptyDirectory(string path)
    {
        return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
    }

    private static void DeleteLinkOnly(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
            return;
        }

        // Висячий каталожный junction: Directory.Exists=false (цели нет), но снять его
        // может только Directory.Delete — File.Delete на нём даёт AccessDenied.
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            {
                Directory.Delete(path);
                return;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return; // пути нет вовсе — удалять нечего
        }

        File.Delete(path);
    }

    private static void CreateLink(string source, string target, LinkKind kind)
    {
        var args = kind switch
        {
            LinkKind.Junction => $"/c mklink /J \"{source}\" \"{target}\"",
            LinkKind.SymlinkDir => $"/c mklink /D \"{source}\" \"{target}\"",
            LinkKind.SymlinkFile => $"/c mklink \"{source}\" \"{target}\"",
            LinkKind.HardlinkFile => $"/c mklink /H \"{source}\" \"{target}\"",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        RunProcess("cmd.exe", args);
    }

    private static void ImportRegistry(AppProfile profile, string file, bool apply, Action<string> log)
    {
        var path = Path.GetFullPath(PathTokens.Expand(file, profile));
        log($"Импорт реестра: {path}");

        if (!File.Exists(path))
        {
            log("  пропущено: файл не найден");
            return;
        }

        if (apply)
        {
            RunProcess("reg.exe", $"import \"{path}\"");
            log("  импортировано");
        }
    }

    private static void WriteBatch(AppProfile profile, BatchRule batch, bool apply, Action<string> log)
    {
        var path = Path.GetFullPath(PathTokens.Expand(batch.Path, profile));
        var exeRaw = PathTokens.Expand(batch.TargetExe, profile);
        var workDirRaw = string.IsNullOrWhiteSpace(batch.WorkingDirectory)
            ? Path.GetDirectoryName(exeRaw) ?? ""
            : PathTokens.Expand(batch.WorkingDirectory, profile);
        // Если exe/рабочая папка лежат под Source ссылки — переписываем на её Target
        // (папку ВНУТРИ пакета). Тогда запуск идёт из РЕАЛЬНОГО расположения пакета
        // (%~dp0 = D:/E:), а не через junction на C:. Так на образном ПК сразу видно,
        // где физически лежит платформа/игра, и запуск не зависит от junction.
        var exe = MapThroughLinks(exeRaw, profile);
        var workDir = MapThroughLinks(workDirRaw, profile);
        var batchExe = ToBatchPath(exe, profile);
        var batchWorkDir = ToBatchPath(workDir, profile);

        // Батч может лежать НЕ в корне пакета (напр. .portable\Tools\*.cmd у BlueStacks).
        // Тогда %~dp0 — это НЕ корень пакета, и все %~dp0-пути в шаблоне ломались бы:
        // relink снёс бы живые junction и пересоздал их на несуществующие цели.
        // Поэтому PORTABLE_ROOT считаем от реального расположения батча, а все пути
        // внутри шаблона идут через %PORTABLE_ROOT% (см. ToBatchPath).
        var portableRootFull = Path.GetFullPath(PathTokens.Expand(profile.PortableRoot, profile)).TrimEnd('\\');
        var batchDir = (Path.GetDirectoryName(path) ?? portableRootFull).TrimEnd('\\');
        var rootPrefix = "%~dp0";
        if (!string.Equals(batchDir, portableRootFull, StringComparison.OrdinalIgnoreCase))
        {
            var relToRoot = Path.GetRelativePath(batchDir, portableRootFull);
            if (relToRoot != ".")
            {
                rootPrefix = "%~dp0" + relToRoot.TrimEnd('\\') + "\\";
            }
        }

        log($"Командный файл: {batch.Name} -> {path}");
        if (!apply)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var content = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("chcp 65001 >nul")
            .AppendLine("setlocal EnableExtensions")
            .AppendLine("rem ============================================================")
            .AppendLine($"rem  Club Portable Linker — запуск пакета: {batch.Name}")
            .AppendLine("rem  Этот файл можно открыть и поправить вручную.")
            .AppendLine("rem  Ссылки и reg применяются автоматически при каждом запуске.")
            .AppendLine("rem ============================================================")
            .AppendLine()
            .AppendLine("rem --- Корень portable-пакета ---")
            .AppendLine($"set \"PORTABLE_ROOT={rootPrefix}\"")
            .AppendLine();

        AppendAdminCheck(content);
        AppendClientResourcesBootstrap(content, profile);

        if (profile.AllLinks().Any())
        {
            content
                .AppendLine()
                .AppendLine("rem --- Ссылки: переназначаем на ЭТОТ пакет (%~dp0) при каждом запуске ---");

            // Только если есть СИМВОЛИЧЕСКИЕ ссылки: на диклесс/iSCSI-томах следование
            // symlink (R2L/R2R) бывает выключено политикой → «symlink type disabled».
            // Junction под это не подпадает, поэтому для junction-пакетов строку не пишем.
            if (profile.AllLinks().Any(l => l.Kind is LinkKind.SymlinkDir or LinkKind.SymlinkFile))
            {
                content
                    .AppendLine("rem включаем следование symlink (на удалённых/iSCSI-томах бывает выключено)")
                    .AppendLine("fsutil behavior set SymlinkEvaluation L2L:1 L2R:1 R2L:1 R2R:1 >nul 2>nul");
            }

            AppendLinkSelfHeal(content, profile);
        }

        content
            .AppendLine()
            .AppendLine("rem --- Импорт reg-файлов (.portable\\Registry, Registry, Reg) ---");
        AppendRegistryImports(content);

        if (profile.Tasks.Count > 0)
        {
            content
                .AppendLine()
                .AppendLine("rem --- Задачи планировщика ---");
            AppendTaskSelfHeal(content, profile);
        }

        if (profile.Services.Count > 0)
        {
            content
                .AppendLine()
                .AppendLine("rem --- Службы Windows ---");
            AppendServiceSelfHeal(content, profile);
        }

        content
            .AppendLine()
            .AppendLine("rem --- Спец-подготовка платформы (создается только для BlueStacks, RAGE MP, Launcher.ini) ---")
            .AppendLine($"if exist \"%PORTABLE_ROOT%{ConfigStore.PortableDirectoryName}\\PortablePreRun.cmd\" call \"%PORTABLE_ROOT%{ConfigStore.PortableDirectoryName}\\PortablePreRun.cmd\" %*");

        content
            .AppendLine()
            .AppendLine("rem --- Запуск основной программы ---")
            .AppendLine($"cd /d \"{batchWorkDir}\"")
            .AppendLine($"start \"{SafeCmdName(batch.Name)}\" \"{batchExe}\" {batch.Arguments}".TrimEnd());

        if (profile.AllLinks().Any(l => l.Kind is LinkKind.Junction or LinkKind.SymlinkDir))
        {
            AppendRelinkSubroutine(content);
        }

        BackupExistingBatchIfChanged(profile, path, content.ToString(), log);
        File.WriteAllText(path, content.ToString(), new UTF8Encoding(false));
        log("  Run.cmd обновлён");

        // Рядом с главным Run.cmd кладём Stop.cmd — корректно закрыть пакет
        // (службы + процессы), чтобы папку можно было перенести/удалить.
        if (string.Equals(Path.GetFileName(path), "Run.cmd", StringComparison.OrdinalIgnoreCase))
        {
            WriteStopBatch(profile, path, log);
        }
    }

    private static void WriteStopBatch(AppProfile profile, string runPath, Action<string> log)
    {
        var stopPath = Path.Combine(Path.GetDirectoryName(runPath)!, "Stop.cmd");

        var content = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("chcp 65001 >nul")
            .AppendLine("setlocal EnableExtensions")
            .AppendLine("rem ============================================================")
            .AppendLine($"rem  Club Portable Linker — закрытие пакета: {profile.Name}")
            .AppendLine("rem  Закрывает службы и процессы пакета, чтобы папку можно")
            .AppendLine("rem  было перенести или удалить. Файл можно править вручную.")
            .AppendLine("rem ============================================================")
            .AppendLine();

        AppendAdminCheck(content);
        content.AppendLine();

        if (profile.Services.Count > 0)
        {
            content.AppendLine("rem --- Остановить службы пакета ---");
            foreach (var svc in profile.Services)
            {
                content.AppendLine($"sc stop \"{SafeCmdName(svc.Name)}\" >nul 2>nul");
            }
            content.AppendLine();
        }

        var exeNames = profile.Batches
            .Select(b => Path.GetFileName(PathTokens.Expand(b.TargetExe, profile)))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exeNames.Count > 0)
        {
            content.AppendLine("rem --- Закрыть процессы лаунчера ---");
            foreach (var name in exeNames)
            {
                content.AppendLine($"taskkill /F /T /IM \"{name}\" >nul 2>nul");
            }
            content.AppendLine();
        }

        // Всё, что запущено из папок пакета (дочерние процессы лаунчера/служб).
        var likePaths = new List<string> { "%~dp0*" };
        foreach (var link in profile.AllLinks())
        {
            if (link.Kind is LinkKind.Junction or LinkKind.SymlinkDir)
            {
                var src = ToKnownFolderBatchPath(Path.GetFullPath(PathTokens.Expand(link.Source, profile)));
                likePaths.Add(src.TrimEnd('\\') + "\\*");
            }
        }
        // Пути передаём через cmd-переменную (cmd корректно раскрывает %~dp0/%ProgramFiles%
        // и не парсит кавычки), а PowerShell читает $env и сплитит по ';'. Так путь пакета
        // с апострофом (напр. "Mike's Games") или '&' не ломает PS-массив. ';' в путях не бывает.
        var joined = string.Join(";", likePaths.Distinct(StringComparer.OrdinalIgnoreCase));
        content
            .AppendLine("rem --- Закрыть всё, что запущено из папок пакета ---")
            .AppendLine($"set \"CPL_STOPPATHS={joined}\"")
            .AppendLine("powershell -NoProfile -Command \"$m=$env:CPL_STOPPATHS -split ';'; Get-Process | ?{$_.Path} | ?{$p=$_.Path; @($m | ?{$p -like $_}).Count -gt 0} | Stop-Process -Force\" >nul 2>nul")
            .AppendLine();

        content
            .AppendLine("echo.")
            .AppendLine("echo Готово. Папку пакета теперь можно перенести или удалить.")
            .AppendLine("timeout /t 2 >nul");

        File.WriteAllText(stopPath, content.ToString(), new UTF8Encoding(false));
        log("  Stop.cmd обновлён");
    }

    private static void AppendAdminCheck(StringBuilder content)
    {
        // Путь передаём через env-переменную (как в Stop.cmd): литерал '%~f0' в одинарных
        // кавычках PS ломался на путях с апострофом (D:\Mike's Games\Run.cmd) — элевация
        // тихо не происходила и скрипт завершался.
        content
            .AppendLine("rem --- Нужны права администратора (junction, реестр, службы) ---")
            .AppendLine("net session >nul 2>nul || (")
            .AppendLine("  echo Требуются права администратора, повышаю...")
            .AppendLine("  set \"CPL_SELF=%~f0\"")
            .AppendLine("  powershell -NoProfile -Command \"Start-Process -Verb RunAs -FilePath $env:CPL_SELF\" >nul 2>nul")
            .AppendLine("  exit /b")
            .AppendLine(")");
    }

    private static void BackupExistingBatchIfChanged(AppProfile profile, string path, string newContent, Action<string> log)
    {
        if (!profile.KeepBackups)
        {
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        var current = File.ReadAllText(path);
        if (string.Equals(NormalizeLineEndings(current), NormalizeLineEndings(newContent), StringComparison.Ordinal))
        {
            return;
        }

        var backupRoot = Path.Combine(
            PathTokens.Expand(profile.PortableRoot, profile),
            ConfigStore.PortableDirectoryName,
            "BatchBackups");
        Directory.CreateDirectory(backupRoot);

        var backupPath = Path.Combine(
            backupRoot,
            Path.GetFileNameWithoutExtension(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(path));
        File.Copy(path, backupPath, overwrite: true);
        log($"  старый batch сохранен: .portable\\BatchBackups\\{Path.GetFileName(backupPath)}");
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private static void AppendClientResourcesBootstrap(StringBuilder content, AppProfile profile)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(profile.ClientResourcesRoot)
            ? ""
            : PathTokens.Expand(profile.ClientResourcesRoot, profile).TrimEnd('\\');

        content
            .AppendLine()
            .AppendLine("rem --- Скрытие окна консоли (если рядом есть cmdow) ---")
            .AppendLine($"set \"CLIENTRESOURCES={configuredRoot.Replace("%", "%%")}\"")
            .AppendLine("if not defined CLIENTRESOURCES if exist \"%PORTABLE_ROOT%ClientResources\\Autorun\\cmdow.exe\" set \"CLIENTRESOURCES=%PORTABLE_ROOT%ClientResources\"")
            .AppendLine("for %%d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do if not defined CLIENTRESOURCES if exist \"%%d:\\ClientResources\\Autorun\\cmdow.exe\" set \"CLIENTRESOURCES=%%d:\\ClientResources\"")
            .AppendLine("if exist \"%CLIENTRESOURCES%\\Autorun\\cmdow.exe\" \"%CLIENTRESOURCES%\\Autorun\\cmdow.exe\" @ /HID");
    }

    private static void AppendLinkSelfHeal(StringBuilder content, AppProfile profile)
    {
        foreach (var link in profile.AllLinks())
        {
            var source = Path.GetFullPath(PathTokens.Expand(link.Source, profile));
            var target = ToBatchPath(PathTokens.Expand(link.Target, profile), profile);
            var parent = Path.GetDirectoryName(source);
            if (string.IsNullOrWhiteSpace(parent))
            {
                continue;
            }

            // Защита от самоссылки: если источник == цель (кривой PortableRoot/{configDir}),
            // :relink бы отодвинул реальную папку в .bak и слинковал её саму на себя → потеря.
            var targetReal = Path.GetFullPath(PathTokens.Expand(link.Target, profile));
            if (string.Equals(source.TrimEnd('\\'), targetReal.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                content.AppendLine($"rem ПРОПУСК «{link.Name}»: источник и цель совпадают — ссылка не создаётся");
                continue;
            }

            // Путь с литеральным '%' (напр. C:\Games\100%off) cmd раскрывает как
            // переменную — :relink сработает по неверному пути. Безопаснее пропустить.
            if (source.Contains('%') || targetReal.Contains('%'))
            {
                content.AppendLine($"rem ПРОПУСК «{link.Name}»: путь содержит '%' — поддержите вручную (cmd раскрывает % как переменную)");
                continue;
            }

            var sourceBatch = ToKnownFolderBatchPath(source);

            if (!string.IsNullOrWhiteSpace(link.Name))
            {
                content.AppendLine($"rem {link.Name}");
            }

            if (link.Kind is LinkKind.Junction or LinkKind.SymlinkDir)
            {
                // Через :relink — ссылка всегда переназначается на этот пакет
                // (чужая/битая пересоздаётся, реальная папка отодвигается в .bak).
                var mklinkMode = link.Kind == LinkKind.Junction ? "/J" : "/D";
                content.AppendLine($"call :relink \"{sourceBatch.TrimEnd('\\')}\" \"{target.TrimEnd('\\')}\" {mklinkMode}");
            }
            else
            {
                // Файловые ссылки редки — простая форма «создать, если нет».
                var parentBatch = ToKnownFolderBatchPath(parent);
                var parentIsRoot = IsDriveRoot(parent)
                    || (parentBatch.StartsWith("%", StringComparison.Ordinal)
                        && !parentBatch.TrimEnd('\\').Contains('\\'));
                if (!parentIsRoot)
                {
                    content.AppendLine($"if not exist \"{parentBatch}\" md \"{parentBatch}\" >nul 2>nul");
                }

                var fileMode = link.Kind == LinkKind.HardlinkFile ? "/H " : "";
                content.AppendLine($"if not exist \"{sourceBatch}\" mklink {fileMode}\"{sourceBatch}\" \"{target}\" >nul 2>nul");
            }
        }
    }

    // Подпрограмма Run.cmd: переназначить junction-ссылку на ЭТОТ пакет.
    private static void AppendRelinkSubroutine(StringBuilder content)
    {
        content
            .AppendLine()
            .AppendLine("goto :eof")
            .AppendLine()
            .AppendLine("rem ============================================================")
            .AppendLine("rem  :relink — указать ссылку на ЭТОТ пакет.")
            .AppendLine("rem  %1 = путь-ссылка, %2 = цель в пакете, %3 = режим (/J или /D).")
            .AppendLine("rem  Чужая/битая ссылка пересоздаётся; реальная папка → в .bak.")
            .AppendLine("rem ============================================================")
            .AppendLine(":relink")
            .AppendLine("rem Снимаем ЛЮБУЮ reparse-точку (живую ИЛИ висячую — target пропал):")
            .AppendLine("rem fsutil видит reparse-данные даже у битого junction, rmdir убирает ТОЛЬКО ссылку.")
            .AppendLine("fsutil reparsepoint query \"%~1\" >nul 2>nul && rmdir \"%~1\" >nul 2>nul")
            .AppendLine("rem Осталась РЕАЛЬНАЯ папка (не ссылка) — отодвигаем в .bak, чтобы не потерять:")
            .AppendLine("if exist \"%~1\\\" move \"%~1\" \"%~1.bak.%RANDOM%\" >nul 2>nul")
            .AppendLine("if not exist \"%~dp1\" mkdir \"%~dp1\" >nul 2>nul")
            .AppendLine("mklink %~3 \"%~1\" \"%~2\" >nul 2>nul")
            .AppendLine("rem Контроль: если ссылка НЕ создана (папка занята и не отодвинулась) —")
            .AppendLine("rem говорим об этом, иначе программа тихо пишет на C: и теряет данные при ребуте.")
            .AppendLine("fsutil reparsepoint query \"%~1\" >nul 2>nul || (")
            .AppendLine("  echo [ОШИБКА] Ссылка не создана: \"%~1\" — возможно, папка занята. Закройте программу (Stop.cmd) и запустите снова.")
            .AppendLine("  echo %date% %time% relink FAIL \"%~1\" -^> \"%~2\" >> \"%PORTABLE_ROOT%relink-errors.log\"")
            .AppendLine(")")
            .AppendLine("goto :eof");
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root)
            && string.Equals(Path.GetFullPath(path).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendRegistryImports(StringBuilder content)
    {
        // ВАЖНО: тремя явными строками, а НЕ вложенным `for %%G in (...) do ... for /r "%%~G"`.
        // Вложенная форма с FOR-переменной в качестве корня `for /r` в cmd находит 0 файлов
        // (проверено) — из-за этого Run.cmd молча НЕ импортировал reg. Прямой `for /r "путь"`
        // работает корректно.
        foreach (var folder in new[] { ConfigStore.PortableDirectoryName + "\\Registry", "Registry", "Reg" })
        {
            content.AppendLine(
                $"if exist \"%PORTABLE_ROOT%{folder}\" for /r \"%PORTABLE_ROOT%{folder}\" %%r in (*.reg) do reg import \"%%r\" >nul 2>nul");
        }
    }

    private static void AppendTaskSelfHeal(StringBuilder content, AppProfile profile)
    {
        foreach (var task in profile.Tasks)
        {
            var xmlPath = ToBatchPath(PathTokens.Expand(task.XmlPath, profile), profile);
            content.AppendLine($"if exist \"{xmlPath}\" schtasks /Create /TN \"{SafeCmdName(task.Name)}\" /XML \"{xmlPath}\" /F >nul 2>nul");
        }
    }

    private static void AppendServiceSelfHeal(StringBuilder content, AppProfile profile)
    {
        foreach (var service in profile.Services)
        {
            var binaryPath = PathTokens.Expand(service.BinaryPath, profile);
            // В батнике '%' раскрывается как переменная — экранируем для литерала.
            var binBatch = binaryPath.Replace("%", "%%");
            var name = SafeCmdName(service.Name);
            var type = NormalizeServiceType(service.Type);
            var start = NormalizeServiceStart(service.StartMode);
            content
                .AppendLine($"sc query \"{name}\" >nul 2>nul || sc create \"{name}\" type= {type} start= {start} binPath= \"{binBatch}\" >nul 2>nul")
                .AppendLine($"sc config \"{name}\" type= {type} start= {start} binPath= \"{binBatch}\" >nul 2>nul");

            if (service.StartAfterApply)
            {
                content.AppendLine($"sc start \"{name}\" >nul 2>nul");
            }
        }
    }

    // Тип/режим запуска службы подставляются в sc ВНЕ кавычек — строго ограничиваем
    // белым списком (как reg-тип в ini-ветке), иначе мусор/опечатка/подделка в манифесте
    // дописала бы аргументы sc. Неизвестное → безопасный дефолт.
    private static string NormalizeServiceType(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        string[] allowed = { "own", "share", "kernel", "filesys", "rec", "interact", "userown", "usershare" };
        return Array.IndexOf(allowed, v) >= 0 ? v : "own";
    }

    private static string NormalizeServiceStart(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        string[] allowed = { "boot", "system", "auto", "demand", "disabled", "delayed-auto" };
        return Array.IndexOf(allowed, v) >= 0 ? v : "demand";
    }

    // Имя службы/задачи/заголовка start подставляется в .cmd внутри кавычек — убираем
    // batch-метасимволы и кавычки, чтобы значение из манифеста не вырвалось из строки.
    // Имена служб/задач Windows эти символы и так не допускают, потерь нет.
    private static string SafeCmdName(string? value)
    {
        var v = value ?? "";
        foreach (var ch in new[] { '"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n' })
        {
            v = v.Replace(ch.ToString(), "");
        }
        return v;
    }

    private static string ToKnownFolderBatchPath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('\\');
        var replacements = new (string Root, string Token)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%ProgramData%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%ProgramFiles(x86)%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%ProgramFiles%")
        };

        foreach (var (root, token) in replacements
            .Where(item => !string.IsNullOrWhiteSpace(item.Root))
            .OrderByDescending(item => item.Root.Length))
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd('\\');
            if (string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }

            if (fullPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return token + "\\" + Path.GetRelativePath(normalizedRoot, fullPath);
            }
        }

        return fullPath;
    }

    // Переписывает путь, лежащий под Source какой-либо ссылки, на её Target
    // (расположение ВНУТРИ пакета). Самую длинную (конкретную) Source берём первой,
    // чтобы вложенные ссылки разрешались точнее. Если совпадений нет — путь как есть.
    private static string MapThroughLinks(string path, AppProfile profile)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return path;
        }

        foreach (var link in profile.AllLinks()
                     .OrderByDescending(l => PathTokens.Expand(l.Source, profile).Length))
        {
            string source;
            try
            {
                source = Path.GetFullPath(PathTokens.Expand(link.Source, profile)).TrimEnd('\\');
            }
            catch
            {
                continue;
            }

            var target = PathTokens.Expand(link.Target, profile);
            if (string.Equals(full, source, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            if (full.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(target, full[(source.Length + 1)..]);
            }
        }

        return path;
    }

    private static string ToBatchPath(string path, AppProfile profile)
    {
        var rootWithoutSlash = Path.GetFullPath(PathTokens.Expand(profile.PortableRoot, profile)).TrimEnd('\\');
        var root = rootWithoutSlash + "\\";
        var fullPath = Path.GetFullPath(path);
        // %PORTABLE_ROOT% (а не %~dp0): переменная задаётся в начале каждого батча и
        // указывает на корень пакета ДАЖЕ если сам батч лежит в подпапке (.portable\Tools).
        if (string.Equals(fullPath.TrimEnd('\\'), rootWithoutSlash, StringComparison.OrdinalIgnoreCase))
        {
            return "%PORTABLE_ROOT%";
        }

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return ToKnownFolderBatchPath(fullPath);
        }

        return "%PORTABLE_ROOT%" + Path.GetRelativePath(rootWithoutSlash, fullPath);
    }

    private static void MoveDirectoryResilient(string source, string target, Action<string> log)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(target));

        // На одном томе Directory.Move — это мгновенное переименование. Но только
        // если цели ещё НЕТ: в существующую папку Move не умеет — такой случай
        // (домерживание остатка после упавшего переноса) идёт через robocopy ниже,
        // он корректно сливает в непустую цель и на одном томе.
        if (!string.IsNullOrEmpty(sourceRoot) &&
            string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }

        // Между дисками (например C: -> game-disk D:/E:) Directory.Move бросает
        // IOException «Source and destination path must have identical roots».
        // Переносим через robocopy /MOVE: надёжно для больших игровых каталогов
        // (длинные пути, докопирование, удаление исходника после успеха).
        // Перед копированием убеждаемся, что на целевом диске хватит места —
        // иначе robocopy упадёт на середине и оставит данные «на полпути».
        EnsureEnoughFreeSpace(source, target, log);

        log("  перенос между разными дисками — копирую содержимое (robocopy /MOVE)...");
        log("  для больших пакетов (BlueStacks и т.п.) это может занять несколько минут — НЕ закрывайте окно.");
        Directory.CreateDirectory(target);
        // /XJ обязателен: без него robocopy заходит ВНУТРЬ junction/symlink как в обычную
        // папку и /MOVE удаляет файлы ЦЕЛИ ссылки (например, данные другого пакета на D:),
        // а ссылка внутрь dest даёт бесконечное копирование до заполнения диска.
        var args = $"\"{source.TrimEnd('\\')}\" \"{target.TrimEnd('\\')}\" /E /MOVE /COPY:DAT /DCOPY:DAT /XJ /R:1 /W:1 /NFL /NDL /NP /NJH /NJS";
        var exitCode = RunRobocopy(args);

        // robocopy: коды 0..7 — успех, 8 и выше — ошибка.
        if (exitCode >= 8)
        {
            throw new InvalidOperationException(
                $"Не удалось перенести «{source}» на другой диск: robocopy вернул код {exitCode}.");
        }

        // Проверяем, что источник реально переехал целиком. Если robocopy не смог
        // перенести часть файлов (заняты антивирусом/игрой), НЕ создаём ссылку поверх
        // остатка — иначе часть данных «спрячется» под junction (тихая потеря).
        bool hasRemaining;
        try
        {
            // Не заходим в reparse-точки: junction в источнике (исключён /XJ выше) —
            // это не «оставшиеся файлы», а ссылка; обход внутрь дал бы ложный отказ
            // или IOException на цикле ссылок.
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };
            hasRemaining = Directory.Exists(source) &&
                Directory.EnumerateFiles(source, "*", opts).Any();
        }
        catch
        {
            hasRemaining = true; // не смогли проверить — считаем неполным (безопаснее)
        }

        if (hasRemaining)
        {
            throw new InvalidOperationException(
                $"Перенос «{source}» неполный (robocopy код {exitCode}): часть файлов осталась — возможно, заняты другим процессом. Закройте программу (Stop.cmd) и повторите.");
        }

        // источник пуст — дочищаем пустые каталоги
        if (Directory.Exists(source))
        {
            Directory.Delete(source, recursive: true);
        }
    }

    private static void EnsureEnoughFreeSpace(string source, string target, Action<string> log)
    {
        long sourceSize;
        try
        {
            sourceSize = GetDirectorySize(source);
        }
        catch
        {
            // Не смогли посчитать размер — не блокируем перенос, robocopy сам сообщит об ошибке.
            return;
        }

        var targetRoot = Path.GetPathRoot(Path.GetFullPath(target));
        if (string.IsNullOrEmpty(targetRoot))
        {
            return;
        }

        long free;
        try
        {
            free = new DriveInfo(targetRoot).AvailableFreeSpace;
        }
        catch
        {
            return;
        }

        // Запас 256 МБ на служебные данные/округление.
        const long reserve = 256L * 1024 * 1024;
        if (sourceSize + reserve > free)
        {
            throw new InvalidOperationException(
                $"На диске {targetRoot.TrimEnd('\\')} не хватает места: нужно ~{FormatBytes(sourceSize)}, " +
                $"свободно {FormatBytes(free)}. Освободите место и повторите.");
        }

        log($"  свободно на {targetRoot.TrimEnd('\\')}: {FormatBytes(free)}, переносим ~{FormatBytes(sourceSize)}.");
    }

    private static long GetDirectorySize(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            // Пропускаем junction/symlink — это reparse-point, реальных данных в нём нет.
            try
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(file).Length; } catch { /* недоступный файл — пропускаем */ }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    stack.Push(sub);
                }
            }
            catch
            {
                // Каталог недоступен — пропускаем.
            }
        }

        return total;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static int RunRobocopy(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException("Не удалось запустить robocopy.");

        // Читаем оба потока асинхронно до WaitForExit — иначе при заполнении
        // буфера одного из pipe'ов процесс зависнет (классический deadlock).
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        outTask.GetAwaiter().GetResult();
        errTask.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static void RunProcess(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        }) ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        // Асинхронное чтение обоих потоков до WaitForExit во избежание deadlock.
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = outTask.GetAwaiter().GetResult();
        var error = errTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} завершился с кодом {process.ExitCode}: {error}{output}");
        }
    }

    private static void RegisterTask(AppProfile profile, TaskRule task, bool apply, Action<string> log)
    {
        var xmlPath = Path.GetFullPath(PathTokens.Expand(task.XmlPath, profile));
        log($"Задача планировщика: {task.Name} <- {xmlPath}");

        if (!File.Exists(xmlPath))
        {
            log("  пропущено: XML не найден");
            return;
        }

        if (apply)
        {
            RunProcess("schtasks.exe", $"/Create /TN \"{SafeCmdName(task.Name)}\" /XML \"{xmlPath}\" /F");
            log("  задача создана или обновлена");
        }
    }

    private static void ConfigureService(AppProfile profile, ServiceRule service, bool apply, Action<string> log)
    {
        var binaryPath = PathTokens.Expand(service.BinaryPath, profile);
        log($"Служба: {service.Name}");
        log($"  файл: {binaryPath}");
        log($"  тип: {service.Type}, запуск: {service.StartMode}");

        if (!apply)
        {
            return;
        }

        var svcName = SafeCmdName(service.Name);
        var svcType = NormalizeServiceType(service.Type);
        var svcStart = NormalizeServiceStart(service.StartMode);
        var exists = ProcessReturnsZero("sc.exe", $"query \"{svcName}\"");
        if (exists)
        {
            RunProcess("sc.exe", $"config \"{svcName}\" type= {svcType} start= {svcStart} binPath= \"{binaryPath}\"");
            log("  служба обновлена");
        }
        else
        {
            RunProcess("sc.exe", $"create \"{svcName}\" type= {svcType} start= {svcStart} binPath= \"{binaryPath}\"");
            log("  служба создана");
        }

        if (service.StartAfterApply)
        {
            if (!ProcessReturnsZero("sc.exe", $"start \"{svcName}\""))
            {
                log("  служба не запущена сейчас; возможно, нужен перезапуск Windows");
            }
            else
            {
                log("  служба запущена");
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

        // Сливаем перенаправленные потоки (иначе sc/schtasks с объёмным выводом
        // может заполнить буфер pipe и WaitForExit зависнет).
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        outTask.GetAwaiter().GetResult();
        errTask.GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsDangerousSourceRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var fullPath = SafeFullPath(path).TrimEnd('\\');
        var root = Path.GetPathRoot(fullPath)?.TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dangerousExactPaths = new[]
        {
            @"C:\",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetPathRoot(Environment.SystemDirectory) + "Users",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        if (dangerousExactPaths
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => SafeFullPath(item).TrimEnd('\\'))
            .Any(item => string.Equals(fullPath, item, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Поддерево %WINDIR% (System32 и т.п.) — внутрь Windows ссылок не делаем никогда.
        var windows = SafeFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(windows) && IsSameOrUnder(fullPath, windows))
        {
            return true;
        }

        // Личные папки пользователя (Документы/Рабочий стол/Загрузки и т.п.) — защищаем
        // от случайного/злонамеренного переноса пользовательских данных. AppData при этом
        // разрешён (это нормальный источник junction для %LOCALAPPDATA%/%APPDATA%).
        var userFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        return userFolders
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => SafeFullPath(item).TrimEnd('\\'))
            .Any(item => IsSameOrUnder(fullPath, item));
    }

    private static bool NeedsAdminForPath(string path)
    {
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };

        return protectedRoots
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Any(root => IsSameOrUnder(path, root));
    }

    private static bool IsLikelyImageDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var fullPath = SafeFullPath(path).TrimEnd('\\');
        var fullRoot = SafeFullPath(root).TrimEnd('\\');
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string DescribeKind(LinkKind kind)
    {
        return kind switch
        {
            LinkKind.Junction => "junction для папки",
            LinkKind.SymlinkDir => "символическая ссылка на папку",
            LinkKind.SymlinkFile => "символическая ссылка на файл",
            LinkKind.HardlinkFile => "жесткая ссылка на файл",
            _ => kind.ToString()
        };
    }
}
