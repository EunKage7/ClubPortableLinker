namespace ClubPortableLinker;

// Каталог «изученных» платформ-лаунчеров: единый список того, что линкер умеет
// собирать спец-правилами (AutoPortableBuilder.Add*Rules). Используется UI-вкладкой
// «Платформы» для показа, авто-поиска установки на ПК и быстрой сборки/рецепта.
//
// Name ДОЛЖЕН попадать в соответствующий Is*-детектор (по подстроке), чтобы при
// сборке сработало нужное спец-правило. DefaultDirs — типовые папки установки
// (первая существующая считается «найденной» и подставляется как источник).
public sealed record KnownLauncher(string Name, string Hint, string[] DefaultDirs);

public sealed record LauncherStatus(KnownLauncher Launcher, string? Folder)
{
    public bool Installed => !string.IsNullOrEmpty(Folder);
}

public static class LauncherCatalog
{
    private static string PF(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }.Concat(parts).ToArray());

    private static string PFx86(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Concat(parts).ToArray());

    private static string PD(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }.Concat(parts).ToArray());

    private static string LAD(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }.Concat(parts).ToArray());

    // Полный список изученных платформ. Порядок — как удобно видеть в окне.
    public static IReadOnlyList<KnownLauncher> All { get; } =
    [
        new("Steam", "Valve Steam (steam.exe)", [PFx86("Steam"), PF("Steam")]),
        new("Epic", "Epic Games Launcher", [PF("Epic Games"), PFx86("Epic Games")]),
        new("Battle.net", "Blizzard Battle.net", [PFx86("Battle.net"), PF("Battle.net")]),
        new("EA App", "EA Desktop (бывш. Origin)", [PF("Electronic Arts", "EA Desktop"), PFx86("Electronic Arts", "EA Desktop")]),
        new("Ubisoft Connect", "Ubisoft Connect (Uplay)", [PFx86("Ubisoft", "Ubisoft Game Launcher"), PF("Ubisoft", "Ubisoft Game Launcher")]),
        new("GOG Galaxy", "GOG Galaxy (GalaxyClient.exe)", [PFx86("GOG Galaxy"), PF("GOG Galaxy")]),
        new("Rockstar Games", "Rockstar Games Launcher", [PF("Rockstar Games", "Launcher"), PFx86("Rockstar Games", "Launcher")]),
        new("Riot Client", "Riot Games (Valorant/LoL)", [PF("Riot Games"), PFx86("Riot Games")]),
        new("FACEIT", "FACEIT Anti-Cheat", [PF("FACEIT AC"), PFx86("FACEIT AC")]),
        new("BlueStacks", "BlueStacks (Android-эмулятор)", [PF("BlueStacks_nxt"), PFx86("BlueStacks_nxt")]),
        new("RSI Launcher", "Star Citizen (RSI)", [PF("Roberts Space Industries", "RSI Launcher")]),
        new("RAGE MP", "RAGE Multiplayer (GTA RP)", [@"C:\RAGEMP", @"C:\RAGE MP"]),
        new("Wargaming Game Center", "World of Tanks/Warships (wgc.exe)", [PFx86("Wargaming.net", "GameCenter"), PF("Wargaming.net", "GameCenter")]),
        new("Lesta Game Center", "Мир танков/кораблей (lgc.exe)", [PF("Lesta", "GameCenter"), PFx86("Lesta", "GameCenter"), PF("Lesta Games", "GameCenter")]),
        new("BattleState (Tarkov)", "Escape from Tarkov (BsgLauncher)", [@"C:\Battlestate Games\BsgLauncher", PF("Battlestate Games", "BsgLauncher")]),
        new("VK Play", "VK Play (Warface, STALCRAFT…)", [LAD("VKPlay"), LAD("GameCenter"), PF("VK", "VK Play")]),
        new("4game", "4game / Innova (L2, Aion, BDO)", [PFx86("4game"), PF("4game"), LAD("4game")]),
        new("DaVinci Resolve", "Blackmagic DaVinci Resolve (Resolve.exe)", [PF("Blackmagic Design", "DaVinci Resolve"), PFx86("Blackmagic Design", "DaVinci Resolve")]),
    ];

    public static string? TryLocate(KnownLauncher launcher)
    {
        foreach (var dir in launcher.DefaultDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    return dir;
                }
            }
            catch
            {
                // недоступный путь — пропускаем
            }
        }

        return null;
    }

    public static IReadOnlyList<LauncherStatus> DetectAll() =>
        All.Select(l => new LauncherStatus(l, TryLocate(l))).ToList();
}
