using System.IO.Compression;
using System.Text.Json;

namespace ClubPortableLinker;

public static class Cli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Any(IsHelp))
            {
                PrintHelp(output);
                return 0;
            }

            // Опечатка в флаге раньше проглатывалась молча. Самое опасное: `--saf`
            // вместо `--safe` тихо НЕ включал SafeMode, и junction (mklink /J)
            // ставились на живой системе под админом. Теперь любой неизвестный
            // «--флаг» — явная ошибка (значения путей/имён с «--» не начинаются).
            ValidateKnownFlags(args);

            var configPath = ValueAfter(args, "--config") ?? "profiles.json";

            if (args.Contains("--init", StringComparer.OrdinalIgnoreCase))
            {
                ConfigStore.WriteSample(configPath);
                output.WriteLine($"Пример настроек записан: {Path.GetFullPath(configPath)}");
                return 0;
            }

            if (args.Contains("--pack", StringComparer.OrdinalIgnoreCase))
            {
                var sourceFolder = ValueAfter(args, "--source")
                    ?? throw new InvalidOperationException("Не указан --source для --pack.");
                var destinationFolder = ValueAfter(args, "--destination")
                    ?? throw new InvalidOperationException("Не указан --destination для --pack.");
                var name = ValueAfter(args, "--name") ?? new DirectoryInfo(sourceFolder).Name;

                PackageBuilder.Build(new PackageBuildRequest
                {
                    SourceFolder = sourceFolder,
                    DestinationFolder = destinationFolder,
                    ProfileName = name
                }, output.WriteLine);
                return 0;
            }

            if (args.Contains("--auto-folder", StringComparer.OrdinalIgnoreCase))
            {
                var sourceFolder = ValueAfter(args, "--source")
                    ?? throw new InvalidOperationException("Не указан --source для --auto-folder.");
                var destinationFolder = ValueAfter(args, "--destination")
                    ?? throw new InvalidOperationException("Не указан --destination для --auto-folder.");
                var name = ValueAfter(args, "--name") ?? new DirectoryInfo(sourceFolder).Name;

                AutoPortableBuilder.BuildFromInstalledFolder(new AutoBuildRequest
                {
                    MainFolder = sourceFolder,
                    PortableRoot = destinationFolder,
                    AppName = name,
                    ClientResourcesRoot = ValueAfter(args, "--clientresources") ?? "",
                    SharedResourcesRoot = ValueAfter(args, "--sharedresources") ?? "",
                    ApplyAfterBuild = !args.Contains("--no-apply", StringComparer.OrdinalIgnoreCase)
                }, output.WriteLine);
                return 0;
            }

            if (args.Contains("--apply-package", StringComparer.OrdinalIgnoreCase))
            {
                var packageFolder = ValueAfter(args, "--package")
                    ?? throw new InvalidOperationException("Не указан --package для --apply-package.");
                var packageConfig = ConfigStore.Load(packageFolder);
                var packageProfile = ResolveProfile(args, packageConfig);
                var packageMode = ParseMode(ValueAfter(args, "--mode"));
                if (args.Contains("--safe", StringComparer.OrdinalIgnoreCase) ||
                    args.Contains("--no-links", StringComparer.OrdinalIgnoreCase))
                {
                    packageMode = OperationMode.SafeMode;
                }

                var packageResult = PortableEngine.Execute(packageProfile, new ExecutionOptions(true, packageMode), output.WriteLine);
                return packageResult.Success ? 0 : 2;
            }

            if (args.Contains("--verify-package", StringComparer.OrdinalIgnoreCase))
            {
                // Без --package раньше молча проверялся profiles.json в текущей папке —
                // невнятный «manifest_missing» вместо понятной ошибки об аргументе.
                var packageFolder = ValueAfter(args, "--package")
                    ?? (ValueAfter(args, "--config") is not null
                        ? configPath
                        : throw new InvalidOperationException("Не указан --package для --verify-package."));
                var report = PackageVerifier.Verify(packageFolder, ValueAfter(args, "--profile"));
                if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
                {
                    output.WriteLine(JsonSerializer.Serialize(report, JsonOptions()));
                }
                else
                {
                    PackageVerifier.WriteText(report, output);
                }

                return report.HasErrors ? 2 : 0;
            }

            if (args.Contains("--export-zip", StringComparer.OrdinalIgnoreCase))
            {
                var packageFolder = ValueAfter(args, "--package")
                    ?? throw new InvalidOperationException("Не указан --package для --export-zip.");
                var packageRoot = ConfigStore.ResolvePortableRoot(packageFolder);
                var report = PackageVerifier.Verify(packageRoot, ValueAfter(args, "--profile"));
                var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
                if (report.HasErrors && !force)
                {
                    PackageVerifier.WriteText(report, output);
                    output.WriteLine("ZIP не создан. Исправьте ошибки или добавьте --force, если нужно упаковать как есть.");
                    return 2;
                }

                var outputZip = ValueAfter(args, "--out")
                    ?? Path.Combine(Path.GetDirectoryName(packageRoot)!, Path.GetFileName(packageRoot.TrimEnd('\\')) + "-portable.zip");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
                // Пишем во временный файл и заменяем атомарно только после успеха.
                // Иначе при падении CreateZip (нет места под большой Steam-пакет, обрыв
                // записи на \\SERVER) старый рабочий ZIP уже удалён, а новый битый/отсутствует.
                var tmpZip = outputZip + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    PackageArchiver.CreateZip(packageRoot, tmpZip, output.WriteLine);
                    File.Move(tmpZip, outputZip, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmpZip))
                    {
                        try { File.Delete(tmpZip); } catch { /* недописанный tmp подчищаем по возможности */ }
                    }
                }
                output.WriteLine($"ZIP создан: {Path.GetFullPath(outputZip)}");
                // Сюда доходим либо без ошибок, либо с явным --force («упаковать как
                // есть»). ZIP создан — это успех; раньше с --force возвращалось 2,
                // и по exit-коду исход был неотличим от «ZIP не создан».
                return 0;
            }

            if (args.Contains("--reg-snapshot", StringComparer.OrdinalIgnoreCase))
            {
                var snapshotPath = ValueAfter(args, "--snapshot")
                    ?? ValueAfter(args, "--out")
                    ?? throw new InvalidOperationException("Не указан --snapshot для --reg-snapshot.");
                var snapshot = RegistryGameCollector.Capture(output.WriteLine);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!);
                File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions()));
                output.WriteLine($"Reg-снимок сохранен: {Path.GetFullPath(snapshotPath)}");
                return 0;
            }

            if (args.Contains("--reg-capture", StringComparer.OrdinalIgnoreCase))
            {
                var packageFolder = ValueAfter(args, "--package")
                    ?? throw new InvalidOperationException("Не указан --package для --reg-capture.");
                var gameName = ValueAfter(args, "--game")
                    ?? throw new InvalidOperationException("Не указан --game для --reg-capture.");
                var snapshotPath = ValueAfter(args, "--snapshot");
                RegistryKeySnapshot? snapshot = null;
                if (!string.IsNullOrWhiteSpace(snapshotPath))
                {
                    // Опечатка в пути снимка раньше игнорировалась МОЛЧА — захват шёл
                    // без baseline и давал неполный результат без предупреждения.
                    if (!File.Exists(snapshotPath))
                    {
                        throw new InvalidOperationException($"Файл снимка не найден: {snapshotPath}");
                    }

                    snapshot = JsonSerializer.Deserialize<RegistryKeySnapshot>(File.ReadAllText(snapshotPath), JsonOptions());
                }

                var count = RegistryGameCollector.CaptureGameRegistry(
                    packageFolder,
                    ValueAfter(args, "--profile") ?? "",
                    gameName,
                    ValueAfter(args, "--game-folder") ?? "",
                    snapshot,
                    output.WriteLine);
                return count > 0 ? 0 : 2;
            }

            if (args.Contains("--fs-snapshot", StringComparer.OrdinalIgnoreCase))
            {
                var outPath = ValueAfter(args, "--out")
                    ?? ValueAfter(args, "--snapshot")
                    ?? throw new InvalidOperationException("Не указан --out для --fs-snapshot (куда сохранить снимок папок).");
                var snapshot = AutoPortableBuilder.CaptureSnapshot(output.WriteLine);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllText(outPath, JsonSerializer.Serialize(snapshot, JsonOptions()));
                output.WriteLine($"Снимок папок сохранён: {Path.GetFullPath(outPath)}");
                output.WriteLine("Теперь установите/докачайте игру и запустите --capture-delta с этим файлом.");
                return 0;
            }

            if (args.Contains("--capture-delta", StringComparer.OrdinalIgnoreCase))
            {
                var packageFolder = ValueAfter(args, "--package")
                    ?? throw new InvalidOperationException("Не указан --package для --capture-delta.");
                var snapshotPath = ValueAfter(args, "--snapshot")
                    ?? throw new InvalidOperationException("Не указан --snapshot (файл снимка ДО установки, из --fs-snapshot).");
                if (!File.Exists(snapshotPath))
                {
                    throw new InvalidOperationException($"Файл снимка не найден: {snapshotPath}");
                }

                var before = JsonSerializer.Deserialize<InstallSnapshot>(File.ReadAllText(snapshotPath), JsonOptions())
                    ?? throw new InvalidOperationException("Не удалось прочитать снимок папок.");
                var deltaGame = ValueAfter(args, "--game");
                var deltaApply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);

                // Без --game --apply перенёс бы ВСЕ новые папки (в т.ч. посторонние,
                // появившиеся между снимками). Требуем --game или явное --yes.
                var deltaRefused = false;
                if (deltaApply && string.IsNullOrWhiteSpace(deltaGame) && !args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
                {
                    output.WriteLine("Отказ: --apply без --game перенесёт ВСЕ новые папки (возможны посторонние).");
                    output.WriteLine("Уточните игру: --game \"Имя\", либо подтвердите перенос всего: --yes.");
                    output.WriteLine("Сейчас покажу превью без переноса:");
                    deltaApply = false;
                    deltaRefused = true;
                }

                AutoPortableBuilder.CaptureDelta(
                    before,
                    packageFolder,
                    ValueAfter(args, "--profile"),
                    deltaGame,
                    deltaApply,
                    output.WriteLine);
                // Запрошенный --apply НЕ выполнен (отказ) — код 2, а не «успех»:
                // скрипты автоматизации не должны считать перенос сделанным.
                return deltaRefused ? 2 : 0;
            }

            if (args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
            {
                var sourceFolder = ValueAfter(args, "--source")
                    ?? throw new InvalidOperationException("Не указан --source для --preview.");
                AutoPortableBuilder.PreviewBuild(new AutoBuildRequest
                {
                    MainFolder = sourceFolder,
                    AppName = ValueAfter(args, "--name") ?? "",
                    PortableRoot = ValueAfter(args, "--destination") ?? ""
                }, output.WriteLine);
                return 0;
            }

            if (args.Contains("--save-recipe", StringComparer.OrdinalIgnoreCase))
            {
                var packageFolder = ValueAfter(args, "--package")
                    ?? throw new InvalidOperationException("Не указан --package для --save-recipe.");
                var cfg = ConfigStore.Load(packageFolder);
                var prof = ResolveProfile(args, cfg);
                var path = RecipeStore.SaveFromProfile(prof, ValueAfter(args, "--out"));
                output.WriteLine($"Рецепт сохранён: {path}");
                return 0;
            }

            if (args.Contains("--list-recipes", StringComparer.OrdinalIgnoreCase))
            {
                // Как и --update-recipes: без явного --shared берём сетевую папку из
                // настроек — иначе сетевые рецепты в списке «терялись».
                var sharedList = ValueAfter(args, "--shared") ?? LinkerSettings.Load().SharedRecipesPath;
                foreach (var r in RecipeStore.List(sharedList))
                {
                    output.WriteLine($"{(r.Shared ? "[сеть]" : "[лок.]")} {r.Name}  ({r.Path})");
                }

                return 0;
            }

            if (args.Contains("--update-recipes", StringComparer.OrdinalIgnoreCase))
            {
                var shared = ValueAfter(args, "--shared") ?? LinkerSettings.Load().SharedRecipesPath;
                var n = RecipeStore.UpdateFromShared(shared);
                output.WriteLine($"Обновлено рецептов из сети: {n}. Старые версии — в recipes\\_history.");
                return 0;
            }

            if (args.Contains("--apply-recipe", StringComparer.OrdinalIgnoreCase))
            {
                var recipePath = ValueAfter(args, "--recipe")
                    ?? throw new InvalidOperationException("Не указан --recipe (имя рецепта или путь к json) для --apply-recipe.");
                var destination = ValueAfter(args, "--destination")
                    ?? throw new InvalidOperationException("Не указан --destination для --apply-recipe.");
                var recipeProfile = ResolveRecipeProfile(recipePath, ValueAfter(args, "--shared"));
                // В назначении уже живёт пакет? Перезапись манифеста стёрла бы его
                // профили/захваченные reg игр без следа — требуем явный --force.
                if (ConfigStore.HasHiddenManifest(destination) &&
                    !args.Contains("--force", StringComparer.OrdinalIgnoreCase))
                {
                    output.WriteLine($"Отказ: в «{destination}» уже есть пакет (manifest). Его профили и reg игр были бы потеряны.");
                    output.WriteLine("Укажите другую папку либо добавьте --force для перезаписи манифеста.");
                    return 2;
                }

                var savedPath = ConfigStore.SavePackage(destination, new PortableConfig { Profiles = [recipeProfile] });
                output.WriteLine($"Из рецепта «{recipeProfile.Name}» создан пакет: {destination}");
                output.WriteLine($"Манифест: {savedPath}");
                if (args.Contains("--apply", StringComparer.OrdinalIgnoreCase))
                {
                    // Раньше режим был зашит (All): --safe/--no-links/--mode молча
                    // игнорировались — пользователь просил safe, а ссылки всё равно ставились.
                    var recipeMode = ParseMode(ValueAfter(args, "--mode"));
                    if (args.Contains("--safe", StringComparer.OrdinalIgnoreCase) ||
                        args.Contains("--no-links", StringComparer.OrdinalIgnoreCase))
                    {
                        recipeMode = OperationMode.SafeMode;
                    }

                    var loaded = ConfigStore.Load(destination);
                    var built = loaded.FindProfile(recipeProfile.Name);
                    var res = PortableEngine.Execute(built, new ExecutionOptions(true, recipeMode), output.WriteLine);
                    return res.Success ? 0 : 2;
                }

                output.WriteLine("Готово. Запустите --apply-package или «Сделать всё», чтобы перенести данные и поставить ссылки.");
                return 0;
            }

            var profileName = ValueAfter(args, "--profile");
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException("Не указан --profile. Примеры есть в --help.");
            }

            var mode = ParseMode(ValueAfter(args, "--mode"));
            if (args.Contains("--safe", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--no-links", StringComparer.OrdinalIgnoreCase))
            {
                mode = OperationMode.SafeMode;
            }

            var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
            if (args.Contains("--plan", StringComparer.OrdinalIgnoreCase))
            {
                apply = false;
            }

            var config = ConfigStore.Load(configPath);
            var profile = config.FindProfile(profileName);
            if (args.Contains("--plan", StringComparer.OrdinalIgnoreCase) &&
                args.Contains("--json", StringComparer.OrdinalIgnoreCase))
            {
                output.WriteLine(JsonSerializer.Serialize(PortableEngine.CreatePlan(profile), JsonOptions()));
                return 0;
            }

            var result = PortableEngine.Execute(profile, new ExecutionOptions(apply, mode), output.WriteLine);
            return result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            error.WriteLine("ОШИБКА: " + ex.Message);
            return 1;
        }
    }

    private static bool IsHelp(string arg)
    {
        return string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
            || arg == "/?";
    }

    // Whitelist всех допустимых «--флагов». Используется для отбраковки опечаток:
    // в этом CLI значения (пути/имена/режимы) с «--» не начинаются, поэтому любой
    // токен на «--», которого тут нет, — это опечатка или лишний аргумент.
    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "--init", "--pack", "--auto-folder", "--apply-package", "--verify-package",
        "--export-zip", "--reg-snapshot", "--reg-capture", "--fs-snapshot", "--capture-delta",
        "--preview", "--save-recipe", "--list-recipes", "--update-recipes", "--apply-recipe", "--plan",
        "--config", "--source", "--destination", "--name", "--clientresources", "--sharedresources",
        "--package", "--profile", "--mode", "--snapshot", "--out", "--game", "--game-folder", "--recipe", "--shared",
        "--safe", "--no-links", "--no-apply", "--apply", "--force", "--json", "--yes"
    };

    private static void ValidateKnownFlags(string[] args)
    {
        foreach (var token in args)
        {
            if (token.StartsWith("--", StringComparison.Ordinal) && !KnownFlags.Contains(token))
            {
                throw new InvalidOperationException($"Неизвестный аргумент: {token}. Список — в --help.");
            }
        }
    }

    private static string? ValueAfter(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                // Защита от «флаг как значение»: если следующий токен сам флаг
                // (--xxx), значит у key значение не задано. Пути/имена/режимы
                // в этом CLI с «--» не начинаются.
                var next = args[i + 1];
                return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
            }
        }

        return null;
    }

    private static bool HasArg(string[] args, string key)
    {
        return args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    }

    // Если --profile задан, но без значения (опечатка `--profile --safe`) — это ошибка,
    // а не молчаливое применение Profiles[0] (иначе под админом применится не та сборка).
    private static AppProfile ResolveProfile(string[] args, PortableConfig config)
    {
        var name = ValueAfter(args, "--profile");
        if (HasArg(args, "--profile") && string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Не задано значение для --profile.");
        }

        return string.IsNullOrWhiteSpace(name) ? config.Profiles[0] : config.FindProfile(name);
    }

    // --recipe принимает И путь к .json, И имя рецепта (как в GUI-списке): если файла
    // по такому пути нет — ищем рецепт с таким именем в локальной + сетевой папках.
    private static AppProfile ResolveRecipeProfile(string recipeArg, string? sharedOverride)
    {
        if (File.Exists(recipeArg))
        {
            return RecipeStore.Load(recipeArg);
        }

        var shared = sharedOverride ?? LinkerSettings.Load().SharedRecipesPath;
        var match = RecipeStore.List(shared)
            .FirstOrDefault(r => string.Equals(r.Name, recipeArg, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return RecipeStore.Load(match.Path);
        }

        throw new InvalidOperationException(
            $"Рецепт не найден: «{recipeArg}». Укажите имя из --list-recipes или путь к .json.");
    }

    private static OperationMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OperationMode.All;
        }

        // Enum.TryParse принимает и числовые строки ВНЕ диапазона («--mode 99» давал
        // (OperationMode)99: ни один режим не совпадал, ничего не применялось, exit 0 —
        // ложный «успех»). IsDefined отсекает такие значения.
        return Enum.TryParse<OperationMode>(value, true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException($"Неизвестный режим: {value}");
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine("ClubPortableLinker");
        output.WriteLine();
        output.WriteLine("Окно программы:");
        output.WriteLine("  ClubPortableLinker.exe");
        output.WriteLine();
        output.WriteLine("Командная строка:");
        output.WriteLine("  ClubPortableLinker.exe --init --config profiles.json");
        output.WriteLine("  ClubPortableLinker.exe --pack --source \"E:\\Programs\\Epic Games1\" --destination \"D:\\Portable\\Epic\" --name Epic");
        output.WriteLine("  ClubPortableLinker.exe --auto-folder --source \"C:\\Program Files\\Epic Games\" --destination \"E:\\Programs\\Epic\" --name Epic");
        output.WriteLine("  ClubPortableLinker.exe --apply-package --package \"E:\\Programs\\Epic\"");
        output.WriteLine("  ClubPortableLinker.exe --auto-folder --source \"C:\\RAGEMP\" --destination \"E:\\Programs\\RAGEMP\" --name RAGEMP --sharedresources \"\\\\SERVER\\RAGEMP\"");
        output.WriteLine("  ClubPortableLinker.exe --auto-folder --source \"C:\\Program Files\\Roberts Space Industries\\RSI Launcher\" --destination \"E:\\Programs\\RSI\" --name \"RSI Launcher\"");
        output.WriteLine("  ClubPortableLinker.exe --auto-folder --source \"C:\\Program Files (x86)\\Steam\" --destination \"E:\\Programs\\Steam\" --name Steam --no-apply");
        output.WriteLine("  ClubPortableLinker.exe --reg-snapshot --snapshot \"D:\\Temp\\before-reg.json\"");
        output.WriteLine("  ClubPortableLinker.exe --reg-capture --package \"E:\\Programs\\Epic\" --game \"Game Name\" --snapshot \"D:\\Temp\\before-reg.json\"");
        output.WriteLine("  ClubPortableLinker.exe --preview --source \"C:\\Program Files\\Epic Games\" --name Epic");
        output.WriteLine("  ClubPortableLinker.exe --fs-snapshot --out \"D:\\Temp\\before-fs.json\"");
        output.WriteLine("  ClubPortableLinker.exe --capture-delta --package \"E:\\Programs\\Steam\" --snapshot \"D:\\Temp\\before-fs.json\" --game Dota");
        output.WriteLine("  ClubPortableLinker.exe --capture-delta --package \"E:\\Programs\\Steam\" --snapshot \"D:\\Temp\\before-fs.json\" --apply");
        output.WriteLine("  ClubPortableLinker.exe --plan --config profiles.json --profile BlueStacks");
        output.WriteLine("  ClubPortableLinker.exe --plan --json --config profiles.json --profile BlueStacks");
        output.WriteLine("  ClubPortableLinker.exe --verify-package --package \"E:\\Programs\\Epic\" --json");
        output.WriteLine("  ClubPortableLinker.exe --export-zip --package \"E:\\Programs\\Epic\" --out \"E:\\Archives\\Epic-portable.zip\"");
        output.WriteLine("  ClubPortableLinker.exe --apply --config profiles.json --profile BlueStacks --mode All");
        output.WriteLine("  ClubPortableLinker.exe --apply-package --package \"E:\\Programs\\Epic\" --safe");
        output.WriteLine();
        output.WriteLine("Рецепты (переносимые шаблоны сборки):");
        output.WriteLine("  ClubPortableLinker.exe --save-recipe --package \"E:\\Programs\\Steam\"   (сохранить пакет как рецепт)");
        output.WriteLine("  ClubPortableLinker.exe --list-recipes [--shared \"\\\\SERVER\\recipes\"]");
        output.WriteLine("  ClubPortableLinker.exe --apply-recipe --recipe \"recipes\\Epic.json\" --destination \"E:\\Programs\\Epic\" [--apply]");
        output.WriteLine("  ClubPortableLinker.exe --update-recipes [--shared \"\\\\SERVER\\recipes\"]   (с бэкапом в recipes\\_history)");
        output.WriteLine();
        output.WriteLine("Режимы: All, Links, Registry, Batches, SafeMode, Tasks, Services");
    }
}
