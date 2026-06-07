using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClubPortableLinker;

public sealed class RegistryKeySnapshot
{
    public HashSet<string> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Fingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static partial class RegistryGameCollector
{
    private const int ExternalProcessTimeoutMs = 15000;

    private static readonly string[] RegistryRoots =
    [
        @"HKEY_CURRENT_USER\SOFTWARE",
        @"HKEY_LOCAL_MACHINE\SOFTWARE",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node"
    ];

    public static RegistryKeySnapshot Capture(Action<string> log)
    {
        var snapshot = new RegistryKeySnapshot();
        foreach (var root in RegistryRoots)
        {
            foreach (var key in EnumerateKeys(root, maxDepth: 7))
            {
                snapshot.Keys.Add(key);
                var fingerprint = ReadKeyFingerprint(key);
                if (!string.IsNullOrWhiteSpace(fingerprint))
                {
                    snapshot.Fingerprints[key] = fingerprint;
                }
            }
        }

        log($"Reg-снимок сделан: {snapshot.Keys.Count} ключей, {snapshot.Fingerprints.Count} отпечатков значений.");
        return snapshot;
    }

    public static int CaptureGameRegistry(string packageFolder, string profileName, string gameName, RegistryKeySnapshot? before, Action<string> log)
    {
        return CaptureGameRegistry(packageFolder, profileName, gameName, "", before, log);
    }

    // gameFolder (путь установки игры) добавляется в токены: reg-ключи, чьё ЗНАЧЕНИЕ
    // содержит этот путь (InstallLocation=D:\OnlineGames\<игра> и т.п.), тоже попадут
    // в захват — игра определяется и по пути папки, не только по имени.
    public static int CaptureGameRegistry(string packageFolder, string profileName, string gameName, string gameFolder, RegistryKeySnapshot? before, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            throw new InvalidOperationException("Укажите название игры.");
        }

        var config = ConfigStore.Load(packageFolder);
        var profile = string.IsNullOrWhiteSpace(profileName)
            ? config.Profiles[0]
            : config.FindProfile(profileName);

        var portableRoot = profile.ConfigDirectory;
        var tokenSet = new HashSet<string>(BuildTokens(gameName), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            var full = gameFolder.Trim().TrimEnd('\\', '/');
            if (full.Length >= 4) { tokenSet.Add(full); }
            var leaf = Path.GetFileName(full);
            if (leaf.Length >= 3) { tokenSet.Add(leaf); }
        }

        var tokens = (IReadOnlyCollection<string>)tokenSet.ToList();
        var targetFolder = Path.Combine(portableRoot, ConfigStore.PortableDirectoryName, "Registry", "Games", SafeFileName(gameName));
        Directory.CreateDirectory(targetFolder);

        // Reg игры пишем в её GameModule (а не в общий список платформы): так
        // reg привязан к конкретной игре, её можно включать/выключать, и план/
        // проверка показывают её отдельно. На рантайме reg всё равно тянется из
        // папок .portable\Registry, поэтому импорт не зависит от Enabled.
        var game = profile.Games.FirstOrDefault(g =>
            string.Equals(g.Name, gameName, StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            game = new GameModule { Name = gameName, Enabled = true };
            profile.Games.Add(game);
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedKeys = new List<string>();
        if (before is not null)
        {
            var after = Capture(log);
            foreach (var key in after.Keys.Except(before.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var afterFingerprint = after.Fingerprints.GetValueOrDefault(key) ?? "";
                changedKeys.Add(key);
                if (!IsTooBroadRegistryKey(key) && (MatchesAnyToken(key, tokens) || MatchesAnyToken(afterFingerprint, tokens)))
                {
                    keys.Add(key);
                }
            }

            foreach (var (key, afterFingerprint) in after.Fingerprints)
            {
                if (!before.Keys.Contains(key) ||
                    string.Equals(before.Fingerprints.GetValueOrDefault(key), afterFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                changedKeys.Add(key);
                if (!IsTooBroadRegistryKey(key) &&
                    (MatchesAnyToken(key, tokens) || MatchesAnyToken(afterFingerprint, tokens) || IsLikelyLauncherRegistryKey(key)))
                {
                    keys.Add(key);
                }
            }

            changedKeys = changedKeys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key)
                .ToList();

            if (changedKeys.Count > 0)
            {
                var changedFile = Path.Combine(targetFolder, "_changed_keys.txt");
                try
                {
                    File.WriteAllLines(
                        changedFile,
                        changedKeys.Select(SanitizeText).Where(line => !string.IsNullOrWhiteSpace(line)).Take(10000),
                        Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    log($"Список измененных ключей не записан: {ex.Message}");
                }

                log($"Измененных reg-ключей после снимка: {changedKeys.Count}. Список: {changedFile}");
            }
        }

        foreach (var key in SearchRegistryKeys(tokens))
        {
            keys.Add(key);
        }

        RefreshExistingRegistryFiles(profile, log);

        var exported = 0;
        foreach (var key in keys.GroupBy(NormalizeRegistryKey, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).OrderBy(key => key))
        {
            if (ContainsInvalidSurrogate(key) || IsTooBroadRegistryKey(key))
            {
                continue;
            }

            if (!ProcessReturnsZero("reg.exe", $"query \"{key}\""))
            {
                continue;
            }

            var fileName = SafeFileName(gameName + "__" + FriendlyRegistryName(key)) + ".reg";
            var filePath = Path.Combine(targetFolder, fileName);
            if (!ProcessReturnsZero("reg.exe", $"export \"{key}\" \"{filePath}\" /y"))
            {
                continue;
            }

            game.RegistryFiles.Add(@"{portableRoot}\" + Path.GetRelativePath(portableRoot, filePath));
            exported++;
            log($"Reg игры экспортирован: {key}");
        }

        game.RegistryFiles = game.RegistryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ConfigStore.SavePackage(portableRoot, config);
        if (exported == 0 && changedKeys.Count > 0)
        {
            log("Reg-файлы по названию не экспортированы, но список измененных ключей сохранен. Укажите более точное имя игры/лаунчера или пришлите список, и ключи можно добавить вручную.");
        }

        log($"Reg-файлов игры добавлено: {exported}");
        return exported;
    }

    public static int RefreshExistingRegistryFiles(AppProfile profile, Action<string> log)
    {
        var refreshed = 0;
        // Обновляем reg как платформы, так и всех игр (независимо от Enabled —
        // файлы на диске держим актуальными).
        var allRegFiles = profile.RegistryFiles
            .Concat(profile.Games.SelectMany(g => g.RegistryFiles))
            .ToList();
        foreach (var regFile in allRegFiles)
        {
            var path = PathTokens.Expand(regFile, profile);
            if (!File.Exists(path))
            {
                continue;
            }

            var key = ReadFirstRegistryKey(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (ProcessReturnsZero("reg.exe", $"query \"{key}\"") &&
                ProcessReturnsZero("reg.exe", $"export \"{key}\" \"{path}\" /y"))
            {
                refreshed++;
            }
        }

        if (refreshed > 0)
        {
            log($"Обновлены существующие reg-файлы платформы: {refreshed}");
        }

        return refreshed;
    }

    private static string? ReadFirstRegistryKey(string regFile)
    {
        try
        {
            // detectEncodingFromByteOrderMarks: reg export даёт UTF-16 LE с BOM —
            // читаем по BOM; недоступный/занятый файл не должен ронять весь захват.
            using var reader = new StreamReader(regFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[HKEY_", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith("]"))
                {
                    return trimmed.Trim('[', ']');
                }
            }
        }
        catch
        {
            // Файл занят/недоступен/битый — пропускаем (refresh просто не тронет его).
        }

        return null;
    }

    private static string? ReadKeyFingerprint(string fullPath)
    {
        using var key = OpenKey(fullPath);
        if (key is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        try
        {
            foreach (var valueName in key.GetValueNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var kind = key.GetValueKind(valueName);
                var value = key.GetValue(valueName);
                builder
                    .Append(valueName)
                    .Append('=')
                    .Append(kind)
                    .Append(':')
                    .Append(ValueToText(value))
                    .AppendLine();
            }

            foreach (var subKeyName in key.GetSubKeyNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("subkey:").AppendLine(subKeyName);
            }
        }
        catch
        {
            return null;
        }

        return builder.ToString();
    }

    private static string ValueToText(object? value)
    {
        return value switch
        {
            null => "",
            byte[] bytes => Convert.ToHexString(bytes),
            string[] values => string.Join("|", values),
            Array array => string.Join("|", array.Cast<object>().Select(item => item?.ToString() ?? "")),
            _ => SanitizeText(value.ToString() ?? "")
        };
    }

    private static IEnumerable<string> EnumerateKeys(string rootPath, int maxDepth)
    {
        yield return rootPath;

        var root = OpenKey(rootPath);
        if (root is null)
        {
            yield break;
        }

        using (root)
        {
            foreach (var key in EnumerateKeys(root, rootPath, 0, maxDepth))
            {
                yield return key;
            }
        }
    }

    private static IEnumerable<string> EnumerateKeys(RegistryKey key, string path, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
        {
            yield break;
        }

        string[] subKeyNames;
        try
        {
            subKeyNames = key.GetSubKeyNames();
        }
        catch
        {
            yield break;
        }

        foreach (var subKeyName in subKeyNames)
        {
            var childPath = path + "\\" + subKeyName;
            yield return childPath;

            RegistryKey? child = null;
            try
            {
                child = key.OpenSubKey(subKeyName);
            }
            catch
            {
                // Ignore protected registry branches.
            }

            if (child is null)
            {
                continue;
            }

            using (child)
            {
                foreach (var grandChild in EnumerateKeys(child, childPath, depth + 1, maxDepth))
                {
                    yield return grandChild;
                }
            }
        }
    }

    private static RegistryKey? OpenKey(string fullPath)
    {
        var normalized = NormalizeRegistryKey(fullPath);
        var parts = normalized.Split('\\', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var root = parts[0] switch
        {
            "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKEY_USERS" => Registry.Users,
            _ => null
        };

        try
        {
            return root?.OpenSubKey(parts[1]);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SearchRegistryKeys(IReadOnlyCollection<string> tokens)
    {
        foreach (var token in tokens.Where(token => token.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var root in RegistryRoots)
            {
                var output = CaptureProcessOutput("reg.exe", $"query \"{root}\" /f \"{token}\" /k");
                foreach (var key in ParseRegQueryKeys(output, token))
                {
                    yield return key;
                }
            }

            foreach (var root in RegistryValueSearchRoots())
            {
                var output = CaptureProcessOutput("reg.exe", $"query \"{root}\" /f \"{token}\" /s");
                foreach (var key in ParseRegQueryKeys(output, token))
                {
                    yield return key;
                }
            }
        }
    }

    private static IEnumerable<string> RegistryValueSearchRoots()
    {
        yield return @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        yield return @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        yield return @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    }

    private static IEnumerable<string> ParseRegQueryKeys(string output, string token)
    {
        string? currentKey = null;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
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

    private static bool IsLikelyLauncherRegistryKey(string key)
    {
        var normalized = NormalizeRegistryKey(key);
        var usefulPrefixes = new[]
        {
            @"HKEY_CURRENT_USER\SOFTWARE\Epic Games",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Epic Games",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Epic Games",
            @"HKEY_CURRENT_USER\SOFTWARE\Electronic Arts",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Electronic Arts",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Electronic Arts",
            @"HKEY_CURRENT_USER\SOFTWARE\EA Games",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\EA Games",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\EA Games",
            @"HKEY_CURRENT_USER\SOFTWARE\Roberts Space Industries",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Roberts Space Industries",
            @"HKEY_CURRENT_USER\SOFTWARE\Cloud Imperium Games",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Cloud Imperium Games",
            @"HKEY_CURRENT_USER\SOFTWARE\RAGE-MP",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\RAGE-MP",
            @"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
        };

        return usefulPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
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

    private static string SanitizeText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(ch);
                    builder.Append(value[++i]);
                }
                else
                {
                    builder.Append('_');
                }

                continue;
            }

            builder.Append(char.IsLowSurrogate(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static bool ContainsInvalidSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyCollection<string> BuildTokens(string name)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        values.Add(name.Trim());

        var compact = Regex.Replace(name, @"[^\p{L}\p{Nd}]+", "");
        if (compact.Length >= 3)
        {
            values.Add(compact);
        }

        foreach (Match match in WordRegex().Matches(name))
        {
            if (match.Value.Length >= 3)
            {
                values.Add(match.Value);
            }
        }

        return values.ToList();
    }

    private static bool MatchesAnyToken(string value, IReadOnlyCollection<string> tokens)
    {
        return tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
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

        // Сливаем оба перенаправлённых потока, иначе reg query с объёмным
        // выводом может заполнить буфер pipe и ложно «истечь по таймауту».
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

    private static string SafeFileName(string value)
    {
        var safe = value.Replace('\\', '_').Replace('/', '_').Replace(':', '_');
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(invalid, '_');
        }

        return safe;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordRegex();
}
