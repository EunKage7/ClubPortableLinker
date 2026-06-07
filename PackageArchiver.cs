using System.IO.Compression;

namespace ClubPortableLinker;

// Упаковка готового portable-пакета в zip. Общая логика для CLI и UI.
public static class PackageArchiver
{
    public static void CreateZip(string packageRoot, string outputZip, Action<string> log, Action<int, int>? onProgress = null)
    {
        var root = Path.GetFullPath(packageRoot).TrimEnd('\\');
        var baseName = Path.GetFileName(root);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "package";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
        if (File.Exists(outputZip))
        {
            File.Delete(outputZip);
        }

        var skipped = 0;
        // Материализуем список заранее — чтобы знать общее число файлов для прогресса.
        var files = EnumeratePackageFiles(root, () => skipped++).ToList();
        var total = files.Count;
        var added = 0;
        using (var zip = ZipFile.Open(outputZip, ZipArchiveMode.Create))
        {
            var failed = 0;
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                try
                {
                    zip.CreateEntryFromFile(file, baseName + "/" + relative, CompressionLevel.Optimal);
                }
                catch (Exception ex)
                {
                    // Занятый/слишком длинный/недоступный файл не должен рушить весь архив.
                    failed++;
                    log($"  пропущен файл (не удалось добавить): {relative} — {ex.Message}");
                }

                added++;
                // Троттлинг: не дёргаем UI на каждый файл (на десятках тысяч файлов
                // это залипало бы) — раз в 25 файлов и в конце.
                if (added % 25 == 0 || added == total)
                {
                    onProgress?.Invoke(added, total);
                }
            }

            if (failed > 0)
            {
                log($"Не удалось добавить файлов: {failed} (остальные упакованы).");
            }
        }

        if (skipped > 0)
        {
            log($"В zip не попали служебные каталоги (_Backups, _Replaced, .portable\\BatchBackups, junction-папки): {skipped}.");
        }

        log($"Файлов в архиве: {added}.");
    }

    private static IEnumerable<string> EnumeratePackageFiles(string root, Action onSkip)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
            {
                yield return file;
            }

            foreach (var sub in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
            {
                // Не идём внутрь junction/symlink — иначе zip пойдёт по ссылке за пределы пакета.
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    onSkip();
                    continue;
                }

                if (IsExcludedFromZip(root, sub))
                {
                    onSkip();
                    continue;
                }

                stack.Push(sub);
            }
        }
    }

    private static bool IsExcludedFromZip(string root, string directory)
    {
        // Бэкапы и убранные данные могут весить как сам пакет — в дистрибутив они не нужны.
        var name = Path.GetFileName(directory);
        if (string.Equals(name, "_Backups", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "_Replaced", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Резервные копии прежних Run.cmd.
        var relative = Path.GetRelativePath(root, directory).Replace('/', '\\');
        return string.Equals(
            relative,
            ConfigStore.PortableDirectoryName + "\\BatchBackups",
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate().ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
