using System.Text.Json;

namespace ClubPortableLinker;

public sealed class RecipeInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Shared { get; set; }

    public override string ToString()
        => (Shared ? "[сеть] " : "[локально] ") + Name;
}

// Рецепт = переносимый шаблон сборки приложения (профиль с токен-путями).
// Хранится в JSON: рядом с exe (recipes\) и/или в сетевой папке.
// Папка рядом с exe едет вместе с линкером при переносе сервер→сервер;
// сетевая папка читается всеми линкерами вживую (правка сразу у всех).
public static class RecipeStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Как в ConfigStore: рецепт, написанный руками в camelCase, без этого молча
        // десериализовался в ПУСТОЙ профиль (Name="", без ссылок).
        PropertyNameCaseInsensitive = true
    };

    private static string? _localDir;

    // Папка рядом с exe; если туда нельзя писать (линкер в Program Files) —
    // фоллбэк в %LOCALAPPDATA%\ClubPortableLinker\recipes.
    public static string LocalDir => _localDir ??= ResolveLocalDir();

    private static string ResolveLocalDir()
    {
        var exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var primary = System.IO.Path.Combine(exeDir, "recipes");
        try
        {
            Directory.CreateDirectory(primary);
            var probe = System.IO.Path.Combine(primary, ".w");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return primary;
        }
        catch
        {
            // нет прав рядом с exe — пишем в профиль пользователя
        }

        var fallback = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClubPortableLinker", "recipes");
        try { Directory.CreateDirectory(fallback); } catch { /* последний шанс — вернём путь как есть */ }
        return fallback;
    }

    public static IReadOnlyList<RecipeInfo> List(string? sharedDir)
    {
        // Локальные первыми, сетевые — последними (перекрывают по имени).
        var result = new Dictionary<string, RecipeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, shared) in new[] { (LocalDir, false), (sharedDir ?? "", true) })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in SafeEnumerateJson(dir))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                result[name] = new RecipeInfo { Name = name, Path = file, Shared = shared };
            }
        }

        return result.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string SaveFromProfile(AppProfile profile, string? targetDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(targetDir) ? LocalDir : targetDir;
        Directory.CreateDirectory(dir);
        var recipe = CloneAsTemplate(profile);
        var path = System.IO.Path.Combine(dir, SafeName(profile.Name) + ".json");
        BackupIfExists(path); // старую версию — в _history (для отката, если обнова сломала)
        File.WriteAllText(path, JsonSerializer.Serialize(recipe, Json), new System.Text.UTF8Encoding(false));
        return path;
    }

    public static string HistoryDir(string dir) => System.IO.Path.Combine(dir, "_history");

    // Тянет рецепты из сетевой папки в локальную, сохраняя старые версии в _history.
    // Если сетевое обновление что-то сломало — откат из _history.
    public static int UpdateFromShared(string? sharedDir)
    {
        if (string.IsNullOrWhiteSpace(sharedDir) || !Directory.Exists(sharedDir))
        {
            return 0;
        }

        Directory.CreateDirectory(LocalDir);
        var count = 0;
        foreach (var src in SafeEnumerateJson(sharedDir))
        {
            var dest = System.IO.Path.Combine(LocalDir, System.IO.Path.GetFileName(src));
            BackupIfExists(dest);
            try
            {
                File.Copy(src, dest, true);
                count++;
            }
            catch
            {
                // недоступный файл пропускаем
            }
        }

        return count;
    }

    private static void BackupIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            var hist = HistoryDir(dir);
            Directory.CreateDirectory(hist);
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var dest = System.IO.Path.Combine(hist, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, dest, true);
        }
        catch
        {
            // бэкап — не критично для самой записи
        }
    }

    public static AppProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<AppProfile>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException("Не удалось прочитать рецепт: " + path);

        // Рецепт могли поправить руками — защищаемся от null-коллекций (как ConfigStore.Load).
        profile.Links ??= [];
        profile.RegistryFiles ??= [];
        profile.Batches ??= [];
        profile.Tasks ??= [];
        profile.Services ??= [];
        profile.Games ??= [];

        // Битый/чужой JSON без имени и без единого правила — это не рецепт:
        // молча создать из него «пустой пакет» хуже, чем честно отказать.
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidOperationException($"Рецепт без имени профиля (Name): {path}");
        }

        if (profile.Links.Count == 0 && profile.Batches.Count == 0 && profile.RegistryFiles.Count == 0)
        {
            throw new InvalidOperationException($"Рецепт «{profile.Name}» пуст (нет ссылок/запуска/reg): {path}");
        }

        return profile;
    }

    // Профиль → переносимый шаблон: убираем машинно-специфичные поля.
    private static AppProfile CloneAsTemplate(AppProfile profile)
    {
        var clone = JsonSerializer.Deserialize<AppProfile>(JsonSerializer.Serialize(profile, Json), Json)!;
        clone.ConfigDirectory = "";
        clone.PortableRoot = "{configDir}";
        return clone;
    }

    private static IEnumerable<string> SafeEnumerateJson(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.json").ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string SafeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "recipe" : safe;
    }
}

public sealed class LinkerSettings
{
    public string SharedRecipesPath { get; set; } = "";

    // Папки, в которых каталог ищет пакеты (.portable\manifest.json). Задаются админом
    // («Добавить папку») и пополняются сами при сборке/открытии пакета. У каждого
    // сервера своя раскладка — поэтому список настраиваемый, а не зашит в код.
    public List<string> CatalogRoots { get; set; } = [];

    private static string SettingsPath =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            "linker.settings.json");

    public static LinkerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<LinkerSettings>(File.ReadAllText(SettingsPath)) ?? new LinkerSettings();
            }
        }
        catch
        {
            // битый файл настроек не должен мешать запуску
        }

        return new LinkerSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // не критично
        }
    }
}
