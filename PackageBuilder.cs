using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClubPortableLinker;

public static partial class PackageBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static PackageBuildResult Build(PackageBuildRequest request, Action<string> log)
    {
        var source = Path.GetFullPath(request.SourceFolder);
        var destination = Path.GetFullPath(request.DestinationFolder);
        var profileName = string.IsNullOrWhiteSpace(request.ProfileName)
            ? new DirectoryInfo(source).Name
            : request.ProfileName.Trim();

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        if (destination.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Destination cannot be inside the source folder.");
        }

        log($"Сканирование папки: {source}");
        var profile = ScanProfile(source, destination, profileName, log);
        profile.ConfigDirectory = destination;
        profile.PortableRoot = @"{configDir}";

        log($"Копирование portable-папки: {destination}");
        CopyDirectory(source, destination, log);
        PatchRegistryFiles(destination, profile, log);
        var batchResult = PortableEngine.Execute(profile, new ExecutionOptions(true, OperationMode.Batches), log);
        if (!batchResult.Success)
        {
            // Раньше результат отбрасывался: ошибка записи Run.cmd логировалась, но
            // --pack печатал «Настройки записаны» и возвращал 0 — ложный успех.
            throw new InvalidOperationException("Не удалось записать Run.cmd/Stop.cmd — пакет неполный (см. лог выше).");
        }

        var config = new PortableConfig { Profiles = [profile] };
        var configPath = ConfigStore.SavePackage(destination, config);

        log($"Настройки записаны: {configPath}");
        return new PackageBuildResult(destination, configPath, profile);
    }

    public static AppProfile ScanProfile(string sourceFolder, string destinationFolder, string profileName, Action<string> log)
    {
        var profile = new AppProfile
        {
            Name = profileName,
            PortableRoot = destinationFolder
        };

        var batFiles = Directory.GetFiles(sourceFolder, "*.bat", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => SafeReadText(path).Contains("mklink", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var bat in batFiles)
        {
            try
            {
                ScanBat(sourceFolder, bat, profile, log);
            }
            catch (Exception ex)
            {
                log($"  не удалось разобрать {Path.GetFileName(bat)}: {ex.Message}");
            }
        }

        foreach (var reg in Directory.GetFiles(sourceFolder, "*.reg", SearchOption.TopDirectoryOnly).OrderBy(path => path))
        {
            var relative = Path.GetRelativePath(sourceFolder, reg);
            profile.RegistryFiles.Add(@"{portableRoot}\" + relative);
        }

        AddFallbackEpicLinks(sourceFolder, profile, log);
        Deduplicate(profile);
        return profile;
    }

    private static void ScanBat(string sourceFolder, string batPath, AppProfile profile, Action<string> log)
    {
        log($"Чтение батника: {Path.GetFileName(batPath)}");
        var text = File.ReadAllText(batPath);

        foreach (Match match in MklinkRegex().Matches(text))
        {
            var source = NormalizeBatchPath(match.Groups["source"].Value, sourceFolder);
            var target = NormalizeBatchPath(match.Groups["target"].Value, sourceFolder);
            if (source is null || target is null)
            {
                log($"  пропущена нераспознанная mklink-строка в {Path.GetFileName(batPath)}");
                continue;
            }

            var targetRelative = TryMakeRelativeToSource(sourceFolder, target);

            // Тип ссылки берём из флага mklink, а не зашиваем Junction:
            // /J — junction (папка), /D — symlink на папку, /H — hardlink файла.
            var kind = match.Groups["flag"].Value.ToLowerInvariant() switch
            {
                "/d" => LinkKind.SymlinkDir,
                "/h" => LinkKind.HardlinkFile,
                _ => LinkKind.Junction
            };

            profile.Links.Add(new LinkRule
            {
                Name = Path.GetFileName(source.TrimEnd('\\')),
                Source = source,
                Target = targetRelative is null ? target : @"{portableRoot}\" + targetRelative,
                Kind = kind,
                MoveExisting = true,
                OverwriteEmptySource = kind is LinkKind.Junction or LinkKind.SymlinkDir
            });
        }

        foreach (Match match in StartRegex().Matches(text))
        {
            var exe = NormalizeBatchPath(match.Groups["exe"].Value, sourceFolder);
            var relative = exe is null ? null : TryMakeRelativeToSource(sourceFolder, exe);
            if (relative is null)
            {
                continue;
            }

            profile.Batches.Add(new BatchRule
            {
                Name = "Запуск " + profile.Name,
                Path = @"{portableRoot}\Run.cmd",
                TargetExe = @"{portableRoot}\" + relative,
                Arguments = "%*",
                WorkingDirectory = @"{portableRoot}\" + Path.GetDirectoryName(relative)
            });
            break;
        }
    }

    private static void AddFallbackEpicLinks(string sourceFolder, AppProfile profile, Action<string> log)
    {
        var dirs = Directory.GetDirectories(sourceFolder).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dirs.Contains("Epic Games") && profile.Links.All(l => !l.Source.Contains(@"Epic Games", StringComparison.OrdinalIgnoreCase)))
        {
            log("Найдена папка Epic Games, добавлена ссылка для Program Files (x86).");
            profile.Links.Add(new LinkRule
            {
                Name = "Epic Games в Program Files (x86)",
                Source = @"C:\Program Files (x86)\Epic Games",
                Target = @"{portableRoot}\Epic Games",
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true
            });
        }

        if (dirs.Contains("ProgramData") && profile.Links.All(l => !l.Source.Contains(@"ProgramData\Epic", StringComparison.OrdinalIgnoreCase)))
        {
            log("Найдена папка ProgramData, добавлена ссылка для C:\\ProgramData\\Epic.");
            profile.Links.Add(new LinkRule
            {
                Name = "Epic в ProgramData",
                Source = @"C:\ProgramData\Epic",
                Target = @"{portableRoot}\ProgramData",
                Kind = LinkKind.Junction,
                MoveExisting = true,
                OverwriteEmptySource = true
            });
        }
    }

    private static void Deduplicate(AppProfile profile)
    {
        profile.Links = profile.Links
            .GroupBy(link => link.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        profile.RegistryFiles = profile.RegistryFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        profile.Batches = profile.Batches
            .GroupBy(batch => batch.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void CopyDirectory(string source, string destination, Action<string> log)
    {
        // Ручная рекурсия с пропуском junction/symlink — иначе обход пошёл бы по ссылке
        // за пределы папки (например, при повторной упаковке уже частично портативной
        // папки) и продублировал/зациклил данные.
        var sourceRoot = Path.GetFullPath(source);
        var destRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destRoot);

        var stack = new Stack<string>();
        stack.Push(sourceRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            Directory.CreateDirectory(Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, dir)));

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                File.Copy(file, Path.Combine(destRoot, Path.GetRelativePath(sourceRoot, file)), true);
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static void PatchRegistryFiles(string packageFolder, AppProfile profile, Action<string> log)
    {
        var rules = profile.Links
            .Select(link => new
            {
                Source = link.Source,
                Target = PathTokens.Expand(link.Target, profile),
                Leaf = Path.GetFileName(PathTokens.Expand(link.Target, profile).TrimEnd('\\'))
            })
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Leaf))
            .ToList();

        foreach (var reg in Directory.GetFiles(packageFolder, "*.reg", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(reg);
            var original = text;

            foreach (var rule in rules)
            {
                text = ReplaceRegPath(text, rule.Target, rule.Source);
                if (CanReplaceByLeaf(rule.Leaf))
                {
                    text = ReplaceByLeaf(text, rule.Leaf, rule.Source);
                }
            }

            if (!string.Equals(original, text, StringComparison.Ordinal))
            {
                File.WriteAllText(reg, text, Encoding.Unicode);
                log($"Пути в реестре поправлены: {Path.GetFileName(reg)}");
            }
        }
    }

    private static string ReplaceRegPath(string text, string from, string to)
    {
        return text
            .Replace(EscapeRegBackslash(from), EscapeRegBackslash(to), StringComparison.OrdinalIgnoreCase)
            .Replace(from.Replace('\\', '/'), to.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanReplaceByLeaf(string leaf)
    {
        // Замена путей в .reg «по листу» (имени конечной папки) опасна для общих
        // имён: leaf вроде bin/data/cache совпадёт с посторонними путями в значениях
        // реестра и перепишет их неверно → битый реестр у клиента. Поэтому короткие
        // и типовые имена не заменяем по листу (только полным путём, см. ReplaceRegPath).
        if (string.IsNullOrWhiteSpace(leaf) || leaf.Length <= 3)
        {
            return false;
        }

        var unsafeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ProgramData", "Program Files", "Program Files (x86)", "Users", "Windows",
            "AppData", "Local", "LocalLow", "Roaming", "Temp", "Common", "Shared",
            "bin", "data", "cache", "temp", "tmp", "logs", "log", "lib", "libs",
            "config", "content", "assets", "save", "saves", "profiles", "profile",
            "game", "games", "app", "apps", "files", "engine", "plugins", "mods"
        };

        return !unsafeNames.Contains(leaf);
    }

    private static string ReplaceByLeaf(string text, string leaf, string to)
    {
        var escapedTo = EscapeRegBackslash(to).TrimEnd('\\');
        var slashTo = to.Replace('\\', '/').TrimEnd('/');

        text = Regex.Replace(
            text,
            $@"[A-Z]:\\\\(?:[^""\r\n\\]+\\\\)*{Regex.Escape(EscapeRegBackslash(leaf))}(?=\\\\|\"")",
            escapedTo,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(
            text,
            $@"[A-Z]:/(?:[^""\r\n/]+/)*{Regex.Escape(leaf)}(?=/|\"")",
            slashTo,
            RegexOptions.IgnoreCase);

        return text;
    }

    private static string EscapeRegBackslash(string path)
    {
        return path.Replace(@"\", @"\\");
    }

    private static string SafeReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return "";
        }
    }

    private static string? NormalizeBatchPath(string path, string sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path
            .Replace("%fixpath%", sourceFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            .Replace("%~dp0", sourceFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "\\");

        try
        {
            // Кривая строка (недопустимые символы после раскрытия %VAR%) не должна
            // ронять весь скан — пропускаем такую mklink/start строку.
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryMakeRelativeToSource(string sourceFolder, string path)
    {
        var source = Path.GetFullPath(sourceFolder).TrimEnd('\\') + "\\";
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetRelativePath(sourceFolder, fullPath);
    }

    // Поддерживаем /D /J /H, пути в кавычках и без, лишние пробелы.
    [GeneratedRegex(@"mklink\s+(?<flag>/[dhj])\s+(?:""(?<source>[^""]+)""|(?<source>[^""\s]+))\s+(?:""(?<target>[^""]+)""|(?<target>[^""\s]+))", RegexOptions.IgnoreCase)]
    private static partial Regex MklinkRegex();

    // start ["заголовок"] [флаги] "exe"|exe — заголовок и кавычки опциональны.
    [GeneratedRegex(@"start\s+(?:""[^""]*""\s+)?(?:/[a-z]\S*\s+)*(?:""(?<exe>[^""]+\.exe)""|(?<exe>[^""\s]+\.exe))", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();
}
