namespace ClubPortableLinker;

public sealed record PackageVerificationIssue(string Severity, string Code, string Message, string? Path = null);

public sealed record PackageVerificationReport(
    string PackagePath,
    string? ManifestPath,
    string? ProfileName,
    bool Success,
    IReadOnlyList<PackageVerificationIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
}

public static class PackageVerifier
{
    public static PackageVerificationReport Verify(string packagePath, string? profileName = null)
    {
        var issues = new List<PackageVerificationIssue>();
        var fullPackagePath = Path.GetFullPath(packagePath);
        string? manifestPath = null;
        AppProfile? profile = null;

        try
        {
            manifestPath = ConfigStore.ResolveConfigPath(fullPackagePath);
            if (!File.Exists(manifestPath))
            {
                issues.Add(Error("manifest_missing", "Manifest не найден.", manifestPath));
                return Report(fullPackagePath, manifestPath, profileName, issues);
            }

            var config = ConfigStore.Load(fullPackagePath);
            profile = string.IsNullOrWhiteSpace(profileName)
                ? config.Profiles[0]
                : config.FindProfile(profileName);
        }
        catch (Exception ex)
        {
            issues.Add(Error("manifest_invalid", "Manifest не читается: " + ex.Message, manifestPath ?? fullPackagePath));
            return Report(fullPackagePath, manifestPath, profileName, issues);
        }

        var plan = PortableEngine.CreatePlan(profile);
        foreach (var issue in plan.Issues)
        {
            issues.Add(new PackageVerificationIssue(issue.Severity, issue.Code, issue.Message, issue.Path));
        }

        VerifyLinks(profile, issues);
        VerifyRegistry(profile, issues);
        VerifyBatches(profile, issues);
        VerifyTasks(profile, issues);
        VerifyServices(profile, issues);

        return Report(fullPackagePath, manifestPath, profile.Name, issues);
    }

    public static void WriteText(PackageVerificationReport report, TextWriter output)
    {
        output.WriteLine(report.Success
            ? $"VERIFY OK: {report.ProfileName ?? report.PackagePath}"
            : $"VERIFY FAIL: {report.ProfileName ?? report.PackagePath}");

        output.WriteLine($"Package:  {report.PackagePath}");
        output.WriteLine($"Manifest: {report.ManifestPath ?? "-"}");

        if (report.Issues.Count == 0)
        {
            output.WriteLine("Проблем не найдено.");
            return;
        }

        foreach (var issue in report.Issues)
        {
            var path = string.IsNullOrWhiteSpace(issue.Path) ? "" : $" [{issue.Path}]";
            output.WriteLine($"{issue.Severity.ToUpperInvariant()}: {issue.Code}: {issue.Message}{path}");
        }
    }

    private static void VerifyLinks(AppProfile profile, List<PackageVerificationIssue> issues)
    {
        // AllLinks() = ссылки платформы + ссылки включённых игр (как в движке),
        // иначе вложенные игры платформы не проверялись бы.
        foreach (var link in profile.AllLinks())
        {
            var source = SafeFullPath(PathTokens.Expand(link.Source, profile));
            var target = SafeFullPath(PathTokens.Expand(link.Target, profile));

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                issues.Add(Error("link_path_invalid", $"Некорректная ссылка: {link.Name}", link.Source));
                continue;
            }

            if (!ExpectedTargetExists(link.Kind, target))
            {
                issues.Add(Error("link_target_missing", $"Данные для ссылки не найдены: {link.Name}", target));
            }

            if (!PathExists(source))
            {
                continue;
            }

            if (!IsReparsePoint(source))
            {
                issues.Add(Error("link_source_not_reparse", $"Windows-путь занят обычной папкой/файлом: {link.Name}", source));
                continue;
            }

            var resolvedTarget = ResolveLinkTarget(source);
            if (string.IsNullOrWhiteSpace(resolvedTarget))
            {
                issues.Add(Warning("link_target_unknown", $"Не удалось прочитать target ссылки: {link.Name}", source));
                continue;
            }

            if (!SamePath(resolvedTarget, target))
            {
                issues.Add(Error("link_target_mismatch", $"Ссылка ведет не туда: {link.Name}. Сейчас: {resolvedTarget}", source));
            }
        }
    }

    private static void VerifyRegistry(AppProfile profile, List<PackageVerificationIssue> issues)
    {
        foreach (var file in profile.AllRegistryFiles())
        {
            var path = SafeFullPath(PathTokens.Expand(file, profile));
            if (!File.Exists(path))
            {
                issues.Add(Error("registry_missing", "Reg-файл из manifest не найден.", path));
            }
        }
    }

    private static void VerifyBatches(AppProfile profile, List<PackageVerificationIssue> issues)
    {
        foreach (var batch in profile.AllBatches())
        {
            var batchPath = SafeFullPath(PathTokens.Expand(batch.Path, profile));
            var targetExe = SafeFullPath(PathTokens.Expand(batch.TargetExe, profile));

            if (!File.Exists(batchPath))
            {
                issues.Add(Warning("batch_missing", $"Командный файл ещё не создан: {batch.Name}", batchPath));
            }

            if (!File.Exists(targetExe))
            {
                issues.Add(Error("launcher_missing", $"Запускатель не найден: {batch.Name}", targetExe));
            }
        }
    }

    private static void VerifyTasks(AppProfile profile, List<PackageVerificationIssue> issues)
    {
        foreach (var task in profile.Tasks)
        {
            var xmlPath = SafeFullPath(PathTokens.Expand(task.XmlPath, profile));
            if (!File.Exists(xmlPath))
            {
                issues.Add(Error("task_xml_missing", $"XML задачи не найден: {task.Name}", xmlPath));
            }
        }
    }

    private static void VerifyServices(AppProfile profile, List<PackageVerificationIssue> issues)
    {
        foreach (var service in profile.Services)
        {
            var binaryPath = SafeFullPath(PathTokens.Expand(service.BinaryPath, profile));
            if (!File.Exists(binaryPath))
            {
                issues.Add(Error("service_binary_missing", $"Файл службы не найден: {service.Name}", binaryPath));
            }
        }
    }

    private static PackageVerificationReport Report(
        string packagePath,
        string? manifestPath,
        string? profileName,
        List<PackageVerificationIssue> issues)
    {
        var success = !issues.Any(issue => issue.Severity.Equals("error", StringComparison.OrdinalIgnoreCase));
        return new PackageVerificationReport(packagePath, manifestPath, profileName, success, issues);
    }

    private static bool ExpectedTargetExists(LinkKind kind, string target)
    {
        return kind is LinkKind.Junction or LinkKind.SymlinkDir
            ? Directory.Exists(target)
            : File.Exists(target);
    }

    private static bool PathExists(string path)
    {
        return Directory.Exists(path) || File.Exists(path);
    }

    private static bool IsReparsePoint(string path)
    {
        return PathExists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            var info = Directory.Exists(path)
                ? new DirectoryInfo(path) as FileSystemInfo
                : new FileInfo(path);

            var target = info.ResolveLinkTarget(false)?.FullName ?? info.LinkTarget;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            return Path.GetFullPath(target);
        }
        catch
        {
            return null;
        }
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

    private static bool SamePath(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd('\\'),
            Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static PackageVerificationIssue Error(string code, string message, string? path = null)
    {
        return new PackageVerificationIssue("error", code, message, path);
    }

    private static PackageVerificationIssue Warning(string code, string message, string? path = null)
    {
        return new PackageVerificationIssue("warning", code, message, path);
    }
}
