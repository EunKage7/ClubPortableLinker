namespace ClubPortableLinker;

public static class PathTokens
{
    public static string Expand(string value, AppProfile profile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        // ?? "" на КАЖДОМ поле: правленный руками манифест/рецепт с "...Root": null
        // раньше ронял CLI с бесполезным NRE («Object reference not set...»).
        var result = value
            .Replace("{portableRoot}", (profile.PortableRoot ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            .Replace("{configDir}", (profile.ConfigDirectory ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            .Replace("{clientResources}", (profile.ClientResourcesRoot ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            .Replace("{sharedResources}", (profile.SharedResourcesRoot ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            .Replace("{appDir}", AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        return Environment.ExpandEnvironmentVariables(result);
    }
}
