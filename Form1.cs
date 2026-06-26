using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Security.Principal;

namespace ClubPortableLinker;

public partial class Form1 : Form
{
    // Глубокая фиолетово-сливовая база + тёплый оранжево-янтарный акцент (комплементарная пара).
    private static readonly Color Bg = Color.FromArgb(14, 9, 26);
    private static readonly Color BgTop = Color.FromArgb(28, 17, 52);
    private static readonly Color BgBottom = Color.FromArgb(10, 6, 20);
    private static readonly Color HeaderBg = Color.FromArgb(22, 13, 42);
    private static readonly Color HeaderTop = Color.FromArgb(38, 22, 70);
    private static readonly Color HeaderBottom = Color.FromArgb(20, 12, 38);
    private static readonly Color Surface = Color.FromArgb(34, 22, 58);
    private static readonly Color SurfaceTop = Color.FromArgb(44, 29, 74);
    private static readonly Color SurfaceBottom = Color.FromArgb(28, 18, 50);
    private static readonly Color Surface2 = Color.FromArgb(46, 31, 78);
    private static readonly Color Border = Color.FromArgb(74, 52, 122);
    private static readonly Color TextPrimary = Color.FromArgb(245, 241, 252);
    private static readonly Color TextMuted = Color.FromArgb(165, 154, 196);
    private static readonly Color Accent = Color.FromArgb(255, 106, 66);
    private static readonly Color Accent2 = Color.FromArgb(255, 190, 92);
    private static readonly Color AccentPink = Color.FromArgb(236, 72, 153);
    private static readonly Color Success = Color.FromArgb(52, 226, 196);
    private static readonly Color Warning = Color.FromArgb(251, 191, 36);

    private readonly TextBox _appName = new();
    private readonly TextBox _portableRoot = new();
    private readonly TextBox _clientResources = new();
    private readonly TextBox _sharedResources = new();
    private readonly TextBox _installerPath = new();
    private readonly TextBox _mainFolder = new();
    private readonly TextBox _packageFolder = new();
    private readonly TextBox _gameName = new();
    private readonly TextBox _gameFolder = new();
    private readonly ComboBox _builds = new();
    private readonly ListBox _packageCatalog = new();
    private readonly ListBox _recipeList = new();
    private readonly ListBox _platformList = new();
    private List<LauncherStatus> _platformsBound = new();
    private readonly LinkerSettings _settings = LinkerSettings.Load();
    private Label? _statusLabel;
    private readonly CheckedListBox _games = new();
    private List<GameModule> _gamesBound = new();
    // RichTextBox: журнал подсвечивает строки по смыслу (ошибки/успех/предупреждения) —
    // в простом TextBox это невозможно.
    private readonly RichTextBox _log = new();
    private readonly string _logFile = BuildLogFilePath();
    // Живые пилюли шапки: статус игрового диска (его физически переключают!) и
    // индикатор «операция выполняется».
    private PillChip? _diskPill;
    private PillChip? _busyPill;
    private System.Windows.Forms.Timer? _diskTimer;
    private string _baseTitle = "";
    // Каталог пакетов: полный список (для строки-фильтра).
    private readonly List<string> _catalogAll = [];
    private readonly TextBox _catalogFilter = new();
    private readonly List<Button> _navButtons = [];
    private TabControl? _tabs;
    private string _lastAppNameForDestination = "Epic";
    private bool _updatingDestination;
    private PortableConfig? _config;
    private InstallSnapshot? _snapshot;
    private RegistryKeySnapshot? _regSnapshot;
    private bool _busy;
    private bool _loadingPackage;
    private Panel? _zipOverlay;
    private Label? _zipBarLabel;
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 120 };

    public Form1()
    {
        InitializeComponent();
        BuildUi();

        // Поля по умолчанию пустые — без примеров-заглушек. Подсказки — в PlaceholderText.
        _appName.Text = "";
        _portableRoot.Text = "";
        _packageFolder.Text = "";
        _clientResources.Text = DetectClientResourcesRoot();
        _sharedResources.Text = "";
        _gameName.Text = "";
        _lastAppNameForDestination = "";

        _appName.PlaceholderText = "Название, напр. Steam, BlueStacks";
        _portableRoot.PlaceholderText = @"Куда собрать, напр. D:\Programs\Steam";
        _packageFolder.PlaceholderText = @"Папка готового пакета (с .portable\manifest.json)";
        _sharedResources.PlaceholderText = @"Сетевая папка (только для RAGE MP), напр. \\SERVER\RAGEMP";
        _gameName.PlaceholderText = "Название игры внутри платформы";
        _gameFolder.PlaceholderText = @"Папка, куда платформа ставит игру, напр. D:\OnlineGames\<игра>";
        // Имя игры авто-подставляется из имени папки установки — и идёт в токены reg-поиска.
        _gameFolder.TextChanged += (_, _) =>
        {
            var leaf = Path.GetFileName(_gameFolder.Text.Trim().TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                _gameName.Text = leaf;
            }
        };
        _appName.TextChanged += (_, _) => UpdatePortableDestinationName();
        _builds.SelectedIndexChanged += (_, _) => RefreshGamesList();
        // Ручная смена пути пакета сбрасывает загруженный конфиг — иначе кнопки
        // «Пакета» работали бы по СТАРОМУ пакету (stale _config). Сама загрузка
        // (LoadPackage) ставит текст под guard'ом и не сбрасывает.
        _packageFolder.TextChanged += (_, _) => { if (!_loadingPackage) { _config = null; } };
    }

    // Тёмная рамка заголовка окна (вместо светлой системной) — под общую тему.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var useDark = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Win10 2004+); 19 — ранние сборки.
            if (DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // DWM недоступен (старая ОС) — не критично.
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void BuildUi()
    {
        Font = new Font("Segoe UI", 10F);
        Padding = Padding.Empty;
        AutoScroll = false;
        BackColor = Bg;
        StartPosition = FormStartPosition.CenterScreen;
        RestoreWindowGeometry();
        try
        {
            Icon = CreateAppIcon();
        }
        catch
        {
            // Иконка не должна блокировать запуск рабочего инструмента.
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Padding.Empty,
            BackColor = Bg
        };
        // 86px: на 78 подзаголовок («Portable-пакеты, junction…») срезался по
        // нижней кромке шапки. Теперь обе строки + квадрат-лого помещаются с запасом.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Bg
        };
        // Сайдбар — фикс (кнопки навигации фикс-ширины), а центр и журнал РЕЗИНОВЫЕ
        // (Percent): на любом мониторе/DPI пропорция держится, а не фиксированные
        // пиксели журнала съедают рабочий центр. Раньше центр ужимался до ~590px и
        // кнопки/карточки резались.
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 74));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));

        _tabs = BuildTabs();
        workspace.Controls.Add(BuildSidebar(), 0, 0);
        workspace.Controls.Add(BuildTabHost(_tabs), 1, 0);
        workspace.Controls.Add(BuildLogPanel(), 2, 0);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(workspace, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Controls.Add(root);
        WireDropTarget(this);
        WireDropTarget(root);
        SelectTab(0);
    }

    // Размер/позиция окна между запусками: админ настраивает окно под свой монитор
    // один раз. Восстанавливаем только если окно попадает на видимый экран.
    private void RestoreWindowGeometry()
    {
        if (_settings.WindowWidth < 400 || _settings.WindowHeight < 300)
        {
            return;
        }

        var bounds = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
        var visible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds));
        if (!visible)
        {
            return; // монитор отключили — не открываемся за пределами экрана
        }

        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (_settings.WindowMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.WindowX = b.X;
            _settings.WindowY = b.Y;
            _settings.WindowWidth = b.Width;
            _settings.WindowHeight = b.Height;
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            _settings.Save();
        }
        catch
        {
            // сохранение геометрии не должно мешать закрытию
        }

        // Ручные таймер/тултип не в components — гасим явно, чтобы не текли хэндлы.
        try
        {
            _diskTimer?.Stop();
            _diskTimer?.Dispose();
            _tips.Dispose();
        }
        catch
        {
            // освобождение ресурсов не должно мешать закрытию
        }

        base.OnFormClosing(e);
    }

    // Стартовые сканы — в OnShown, а не в конструкторе:
    //  • ScanPlatforms раньше не вызывался вовсе (вкладка 0 уже выбрана —
    //    SelectedIndexChanged не срабатывает) — список платформ был пуст до ручного
    //    скана или ухода-возврата на вкладку;
    //  • ScanPackageCatalog из конструктора гонялся с созданием ручки окна — если
    //    фоновый скан успевал раньше, результат молча выбрасывался (каталог «0 пакетов»).
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScanPlatforms();
        ScanPackageCatalog(false);
    }

    private TabControl BuildTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.FlatButtons,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(1, 1),
            Padding = Point.Empty,
            Font = new Font("Segoe UI", 9F)
        };
        // 4 вкладки: ① Создать (внутри — пикер «Платформы») · ② Применить/Проверить ·
        // ③ Игры и reg · ④ Библиотека (Каталог пакетов + Шаблоны).
        tabs.TabPages.Add(NewTab("① Создать пакет", BuildAutoPanel()));
        tabs.TabPages.Add(NewTab("② Применить / Проверить", BuildApplyPanel()));
        tabs.TabPages.Add(NewTab("③ Игры и reg", BuildGameRegistryPanel()));
        tabs.TabPages.Add(NewTab("④ Библиотека", BuildLibraryPanel()));
        tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateNavigation();
            if (tabs.SelectedIndex == 0)
            {
                ScanPlatforms();              // пикер платформ на вкладке «Создать»
            }
            else if (tabs.SelectedIndex == 3)
            {
                ScanPackageCatalog(false);    // Библиотека: каталог пакетов
                RefreshRecipes();             //            + шаблоны
            }
        };
        return tabs;
    }

    // TabControl рисует системную (светлую) рамку вокруг страниц. Прячем её:
    // кладём в тёмную панель и растягиваем TabControl чуть БОЛЬШЕ панели, чтобы
    // его рамка ушла за пределы видимой области и обрезалась.
    private static Control BuildTabHost(TabControl tabs)
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        tabs.Dock = DockStyle.None;
        host.Controls.Add(tabs);
        // Сдвигаем рамку TabControl за пределы видимой области (её page-border
        // вкладок имеет внутренний отступ ~3px, поэтому запас берём с избытком).
        void Reflow() => tabs.Bounds = new Rectangle(-8, -8, host.ClientSize.Width + 16, host.ClientSize.Height + 16);
        host.SizeChanged += (_, _) => Reflow();
        Reflow();
        return host;
    }

    private Control BuildHeader()
    {
        var header = new GradientGrid
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            GradTop = HeaderTop,
            GradBottom = HeaderBottom,
            // Симметричный вертикальный паддинг: и плитка-лого, и блок текста
            // делят одну центральную линию, поэтому центрируются на одном Y.
            Padding = new Padding(22, 6, 18, 6)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // AutoSize: колонка сама подгоняется под суммарную ширину пилюль (они тоже
        // саморазмерные). Раньше фикс-500px не вмещали все чипы на FHD — крайний
        // («ClientResources»/имя ПК) резался.
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        header.Controls.Add(new LogoMark { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 0, 8, 0) }, 0, 0);

        var title = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Padding = new Padding(10, 0, 0, 0)
        };
        // 50/50 + заголовок прижат к низу своей строки, подзаголовок к верху своей:
        // обе строки «обнимают» центральную линию, поэтому пара текста центрируется
        // ровно по середине — на одном уровне с центром квадратной плитки-лого.
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        title.Controls.Add(new Label
        {
            Text = "Club Portable Linker",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 14F),
            ForeColor = TextPrimary,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        title.Controls.Add(new Label
        {
            Text = "Portable-пакеты, junction и реестр для клубных CCBOOT-систем",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F),
            ForeColor = TextMuted,
            // AutoEllipsis: на узком окне подзаголовок не переносится на 2-ю строку
            // (которая срезалась по нижней кромке шапки), а ужимается в «…».
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);
        header.Controls.Add(title, 1, 0);

        var pills = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        var admin = IsCurrentProcessAdministrator();
        var hasClientResources = !string.IsNullOrWhiteSpace(DetectClientResourcesRoot());

        // Кнопка справки — открывает GUIDE.md рядом с программой (правый край).
        var help = SecondaryButton("?");
        help.Width = 36;
        help.Height = 32;
        help.Margin = new Padding(9, 6, 0, 0);
        help.Click += (_, _) => OpenGuide();
        _tips.SetToolTip(help, "Открыть гайд (GUIDE.md): сборка, применение, игры и reg");
        pills.Controls.Add(help);

        pills.Controls.Add(StatusPill(Environment.MachineName, Accent2));
        pills.Controls.Add(StatusPill(admin ? "admin" : "без admin", admin ? Success : Warning));
        // Маркер по смыслу: зелёный при наличии, янтарный — когда ClientResources не найдены.
        pills.Controls.Add(StatusPill(hasClientResources ? "ClientResources OK" : "ClientResources нет", hasClientResources ? Success : Warning));

        // Игровой диск — ЖИВАЯ пилюля: в клубе D:/E: физически переключают, и пропавший
        // диск — главная причина «всё сломалось». Обновляется таймером.
        _diskPill = new PillChip("диск…", TextMuted)
        {
            AutoFit = true,
            Height = 32,
            Margin = new Padding(9, 6, 0, 0)
        };
        _diskPill.FitWidth();
        _tips.SetToolTip(_diskPill, "Игровой диск (несистемный, с макс. свободным местом). Сюда собираются пакеты и ставятся игры.");
        pills.Controls.Add(_diskPill);
        UpdateDiskPill();
        _diskTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        _diskTimer.Tick += (_, _) => UpdateDiskPill();
        _diskTimer.Start();

        // Индикатор «операция выполняется» — виден из любой вкладки.
        _busyPill = new PillChip("⏳ выполняется…", Accent)
        {
            AutoFit = true,
            Height = 32,
            Margin = new Padding(9, 6, 0, 0),
            Visible = false
        };
        _busyPill.FitWidth();
        pills.Controls.Add(_busyPill);

        header.Controls.Add(pills, 2, 0);
        return header;
    }

    private void OpenGuide()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var guide = Path.Combine(dir, "GUIDE.md");
            if (File.Exists(guide))
            {
                Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
                return;
            }

            MessageBox.Show(this,
                "GUIDE.md не найден рядом с программой.\n\nКоротко: «① Создать пакет» — собрать портабл (данные переезжают на игровой диск, на C: остаются ссылки); «② Применить/Проверить» — применить и проверить готовый пакет; «③ Игры и reg» — затащить регистрацию игры (D:\\Games) в пакет; «④ Библиотека» — каталог пакетов и рецепты. В клубе запуск — Run.cmd из корня пакета.",
                "Справка", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: не удалось открыть гайд — " + ex.Message);
        }
    }

    private void UpdateDiskPill()
    {
        if (_diskPill is null)
        {
            return;
        }

        try
        {
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var best = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Where(d => !string.Equals(d.Name, sysRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
            if (best is null)
            {
                _diskPill.Text = "игрового диска НЕТ";
                _diskPill.Marker = Color.FromArgb(255, 128, 140);
            }
            else
            {
                _diskPill.Text = $"{best.Name.TrimEnd('\\')} {best.AvailableFreeSpace / (1024L * 1024 * 1024)} ГБ свободно";
                _diskPill.Marker = Success;
            }
        }
        catch
        {
            // статусная пилюля не должна ничего ронять
        }
    }

    private Control BuildSidebar()
    {
        var sidebar = new GradientGrid
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            GradTop = Color.FromArgb(30, 18, 58),
            GradBottom = Color.FromArgb(16, 10, 32),
            Padding = new Padding(14, 16, 14, 14)
        };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));

        sidebar.Controls.Add(new Label
        {
            Text = "РАБОЧИЕ РЕЖИМЫ",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 9F),
            ForeColor = Accent2,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        nav.Controls.Add(NavButton("① Создать пакет", 0));
        nav.Controls.Add(NavButton("② Применить / Проверить", 1));
        nav.Controls.Add(NavButton("③ Игры и reg", 2));
        nav.Controls.Add(NavButton("④ Библиотека", 3));
        nav.Controls.Add(NavButton("Журнал", -1));
        sidebar.Controls.Add(nav, 0, 1);

        sidebar.Controls.Add(BuildStatusCard(), 0, 2);
        return sidebar;
    }

    private Control BuildFooter()
    {
        return new Label
        {
            Text = "Подсказка: сначала смотрите план/проверку, потом применяйте. Готовый запуск для клуба — Run.cmd в корне portable-папки.",
            Dock = DockStyle.Fill,
            BackColor = HeaderBg,
            ForeColor = TextMuted,
            Padding = new Padding(22, 0, 12, 0),
            // На узком окне подсказка обрезается видимым «…», а не молча по краю.
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static TabPage NewTab(string title, Control content)
    {
        content.Dock = DockStyle.Fill;
        var page = new TabPage(title)
        {
            BackColor = Bg,
            Padding = new Padding(16)
        };
        page.Controls.Add(content);
        return page;
    }

    private Panel BuildLogPanel()
    {
        var panel = new GradientPanel
        {
            Dock = DockStyle.Fill,
            GradTop = Color.FromArgb(30, 18, 58),
            GradBottom = Color.FromArgb(16, 10, 32),
            Padding = new Padding(16, 14, 16, 16)
        };

        // Консольная подложка с тёмно-фиолетовым тоном (а не чёрным).
        var console = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(15, 9, 30),
            Padding = new Padding(10, 8, 6, 8)
        };

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = RichTextBoxScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.WordWrap = true;
        _log.Font = new Font("Consolas", 9.4F);
        _log.BackColor = Color.FromArgb(15, 9, 30);
        _log.ForeColor = Color.FromArgb(214, 226, 250);
        _log.BorderStyle = BorderStyle.None;

        console.Controls.Add(_log);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.Transparent
        };
        var headerLabel = new Label
        {
            Text = "● Журнал действий",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 11F),
            ForeColor = TextPrimary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };
        // Компактные кнопки-иконки: место в шапке журнала ограничено (правая колонка 320px).
        var fullLogButton = SecondaryButton("⤢");
        fullLogButton.Dock = DockStyle.Right;
        fullLogButton.Width = 42;
        fullLogButton.Click += (_, _) => OpenFullLog();
        _tips.SetToolTip(fullLogButton, "Открыть журнал в большом окне");
        var copyLogButton = SecondaryButton("⧉");
        copyLogButton.Dock = DockStyle.Right;
        copyLogButton.Width = 42;
        copyLogButton.Click += (_, _) =>
        {
            try
            {
                if (_log.TextLength > 0) { Clipboard.SetText(_log.Text); Append("Журнал скопирован в буфер."); }
            }
            catch { /* буфер занят другим приложением — не критично */ }
        };
        _tips.SetToolTip(copyLogButton, "Скопировать весь журнал в буфер обмена");
        var clearLogButton = SecondaryButton("✕");
        clearLogButton.Dock = DockStyle.Right;
        clearLogButton.Width = 42;
        clearLogButton.Click += (_, _) => _log.Clear();
        _tips.SetToolTip(clearLogButton, "Очистить журнал на экране (файл лога сохраняется)");
        // Fill добавляем первым (докуется последним и занимает остаток); правые кнопки —
        // в порядке clear, copy, full: докуются с конца списка, поэтому справа налево
        // получится [⤢][⧉][✕].
        header.Controls.Add(headerLabel);
        header.Controls.Add(clearLogButton);
        header.Controls.Add(copyLogButton);
        header.Controls.Add(fullLogButton);

        panel.Controls.Add(console);
        panel.Controls.Add(header);
        return panel;
    }

    private Control BuildAutoPanel()
    {
        // Фиксированная высота контента + хост с авто-прокруткой: на низких экранах
        // (ноутбуки 1366×768) появляется вертикальный скролл, а не прячутся кнопки.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 168 + 318 + 168 + 280,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Bg
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 318));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));

        // --- Общие поля: что собираем и куда ---
        var common = NewGrid(3, 2);
        common.RowStyles[0].Height = 44;
        common.RowStyles[1].Height = 44;
        common.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        common.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        common.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));

        var browsePortable = SmallButton("...");
        browsePortable.Click += (_, _) => BrowsePortableDestination();

        common.Controls.Add(Label("Название"), 0, 0);
        common.Controls.Add(Fill(_appName), 1, 0);
        common.Controls.Add(Label("Куда собрать"), 0, 1);
        common.Controls.Add(Fill(_portableRoot), 1, 1);
        common.Controls.Add(browsePortable, 2, 1);
        root.Controls.Add(Card("Что и куда собираем", common), 0, 0);

        // --- Два варианта «магической» сборки ---
        var variants = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg
        };
        variants.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        variants.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        variants.Controls.Add(BuildInstallerVariant(), 0, 0);
        variants.Controls.Add(BuildFolderVariant(), 1, 0);
        root.Controls.Add(variants, 0, 1);

        // --- Дополнительно: клубные ресурсы ---
        var advanced = NewGrid(3, 2);
        advanced.RowStyles[0].Height = 44;
        advanced.RowStyles[1].Height = 44;
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));

        var browseClientResources = SmallButton("...");
        browseClientResources.Click += (_, _) => BrowseFolder(_clientResources);
        var browseSharedResources = SmallButton("...");
        browseSharedResources.Click += (_, _) => BrowseFolder(_sharedResources);

        advanced.Controls.Add(Label("ClientResources"), 0, 0);
        advanced.Controls.Add(Fill(_clientResources), 1, 0);
        advanced.Controls.Add(browseClientResources, 2, 0);
        advanced.Controls.Add(Label("Сетевая папка"), 0, 1);
        advanced.Controls.Add(Fill(_sharedResources), 1, 1);
        advanced.Controls.Add(browseSharedResources, 2, 1);
        root.Controls.Add(Card("Дополнительно — клубные ресурсы (RAGE:MP и т.п.)", advanced), 0, 2);

        // Пикер известных платформ прямо здесь: выбрал лаунчер → подставит источник.
        root.Controls.Add(BuildPlatformsPanel(), 0, 3);

        return new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Bg, Controls = { root } };
    }

    private Control BuildInstallerVariant()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Surface
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var browseInstaller = SmallButton("...");
        browseInstaller.Click += (_, _) => BrowseInstaller();

        var step1 = MagicButton("Шаг 1 — снимок и запустить установщик");
        step1.Click += (_, _) => StartInstallerWorkflow();
        _tips.SetToolTip(step1, "Делает снимок системы (папки+реестр) и запускает указанный установщик. Установите программу, затем нажмите Шаг 2.");
        var step2 = MagicButton("Шаг 2 — собрать портабл");
        step2.BackColor = Surface2;
        step2.HoverBackColor = Border;
        step2.GradientEnd = Color.Empty;
        step2.BorderColor = Accent;
        step2.BorderThickness = 1.4f;
        step2.Click += (_, _) => FinishInstallerWorkflow();
        _tips.SetToolTip(step2, "Сравнивает с снимком из Шага 1, находит новые папки и ветки реестра и собирает портабл.");

        grid.Controls.Add(Fill(_installerPath), 0, 0);
        grid.Controls.Add(browseInstaller, 1, 0);
        grid.Controls.Add(step1, 0, 1);
        grid.SetColumnSpan(step1, 2);
        grid.Controls.Add(step2, 0, 2);
        grid.SetColumnSpan(step2, 2);
        grid.Controls.Add(new Label
        {
            Text = "Для новой установки. Снимок до установки + сравнение после — так линкер сам найдёт папки и ветки реестра.",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.3F),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 3);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 3)!, 2);

        return Card("Способ A — поставить с нуля", grid);
    }

    private Control BuildFolderVariant()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Surface
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var browseMain = SmallButton("...");
        browseMain.Click += (_, _) => BrowseFolder(_mainFolder);

        var magic = MagicButton("Собрать портабл автоматически");
        magic.Click += (_, _) => BuildFromInstalledFolder();
        _tips.SetToolTip(magic, "Прога уже установлена: укажи её главную папку — линкер сам найдёт данные, layout и ветки реестра, перенесёт в портабл и поставит junction. НЕОБРАТИМО.");

        grid.Controls.Add(Fill(_mainFolder), 0, 0);
        grid.Controls.Add(browseMain, 1, 0);
        grid.Controls.Add(magic, 0, 1);
        grid.SetColumnSpan(magic, 2);
        grid.Controls.Add(new Label
        {
            Text = "Программа уже стоит на C:. Укажите её главную папку — линкер сам найдёт данные, layout и известные ветки реестра.",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.3F),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 2);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 2)!, 2);
        grid.Controls.Add(new Label
        {
            Text = "Полный захват произвольного реестра — через Способ A (снимок до/после).",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F),
            TextAlign = ContentAlignment.TopLeft
        }, 0, 3);
        grid.SetColumnSpan(grid.GetControlFromPosition(0, 3)!, 2);

        return Card("Способ B — уже установлено", grid);
    }

    private GroupBox BuildApplyPanel()
    {
        var box = new GroupBox
        {
            Text = "Применить или проверить готовую portable-папку",
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = TextPrimary,
            BackColor = Surface
        };

        var layout = NewGrid(4, 5);
        // Прижимаем строки к верху, иначе при Dock=Fill последняя строка
        // («Что делает запуск») уезжает в низ группы, оставляя пустоту.
        layout.Dock = DockStyle.Top;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        // Ряды кнопок («Действие», «Точечно») делаем выше — кнопки могут
        // переноситься на 2 строки при узком окне, и их не должно обрезать.
        layout.RowStyles.Clear();
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));  // Portable-папка
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));  // Сборка
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); // Действие (до 3 строк кнопок)
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); // Точечно (до 3 строк)
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));  // Что делает запуск
        layout.Height = 46 + 46 + 132 + 132 + 72;

        var browsePackage = SmallButton("...");
        browsePackage.Click += (_, _) => BrowseFolder(_packageFolder);

        var openPackage = MainButton("Открыть папку");
        openPackage.Width = 150;
        openPackage.Click += (_, _) => LoadPackage();

        _builds.DropDownStyle = ComboBoxStyle.DropDownList;
        _builds.Dock = DockStyle.Fill;
        _builds.FlatStyle = FlatStyle.Flat;
        _builds.BackColor = Surface2;
        _builds.ForeColor = TextPrimary;

        // Основные действия — крупным акцентным стилем.
        var primary = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Surface
        };
        var all = MainButton("⚠ Сделать всё");
        all.Width = 150;
        // Необратимое действие — выделяем предупреждающим (янтарным) цветом,
        // чтобы визуально отличалось от безопасных кнопок.
        all.BackColor = Warning;
        all.HoverBackColor = Color.FromArgb(234, 160, 20);
        all.ForeColor = Color.FromArgb(28, 20, 6);
        all.Click += (_, _) => Run(true, OperationMode.All);
        _tips.SetToolTip(all, "Применить пакет полностью: перенести данные, поставить junction-ссылки, импортировать reg, поднять службы/задачи и пересобрать Run.cmd. НЕОБРАТИМО — спросит подтверждение.");
        var check = MainButton("Проверить пакет");
        check.Width = 160;
        check.Click += (_, _) => InspectPackage();
        _tips.SetToolTip(check, "Проверить целостность пакета. Ничего не меняет. В конце — итог: исправен или сколько проблем.");
        var openFolder = MainButton("Открыть папку");
        openFolder.Width = 160;
        openFolder.Click += (_, _) => OpenPackageFolder();
        _tips.SetToolTip(openFolder, "Открыть папку пакета в Проводнике (там лежат Run.cmd и Stop.cmd).");
        var zipBtn = MainButton("Запаковать в ZIP");
        zipBtn.Width = 170;
        zipBtn.Click += (_, _) => ExportZipFromUi();
        _tips.SetToolTip(zipBtn, "Упаковать готовый пакет в ZIP для переноса на другой ПК.");
        var shortcutBtn = MainButton("Создать ярлык");
        shortcutBtn.Width = 150;
        shortcutBtn.Click += (_, _) => CreateLauncherShortcut();
        _tips.SetToolTip(shortcutBtn, "Создать на рабочем столе ярлык на Run.cmd с иконкой программы (для конкретного расположения пакета).");
        primary.Controls.AddRange(new Control[] { all, check, openFolder, zipBtn, shortcutBtn });

        // Точечные действия — вторичным (приглушённым) стилем.
        var secondary = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Surface
        };
        var plan = SecondaryButton("План");
        plan.Width = 90;
        plan.Click += (_, _) => Run(false, OperationMode.All);
        _tips.SetToolTip(plan, "Показать, что будет сделано, НИЧЕГО не меняя на диске (безопасно — посмотреть заранее).");
        var links = SecondaryButton("Ссылки");
        links.Width = 100;
        links.Click += (_, _) => Run(true, OperationMode.Links);
        _tips.SetToolTip(links, "Только применить junction-ссылки (перенос данных + ссылки на старые пути). НЕОБРАТИМО.");
        var regs = SecondaryButton("Реестр");
        regs.Width = 100;
        regs.Click += (_, _) => Run(true, OperationMode.Registry);
        _tips.SetToolTip(regs, "Только импортировать reg-файлы пакета в реестр.");
        var rebuild = SecondaryButton("Пересобрать Run.cmd");
        rebuild.Width = 180;
        rebuild.Click += (_, _) => Run(true, OperationMode.Batches);
        _tips.SetToolTip(rebuild, "Перегенерировать Run.cmd/Stop.cmd пакета. НЕ запускает программу.");
        var regFolder = SecondaryButton("Открыть reg");
        regFolder.Width = 130;
        regFolder.Click += (_, _) => OpenRegistryFolder();
        _tips.SetToolTip(regFolder, "Открыть папку с reg-файлами пакета (можно положить свой .reg).");
        secondary.Controls.AddRange(new Control[] { plan, links, regs, rebuild, regFolder });

        layout.Controls.Add(Label("Portable-папка"), 0, 0);
        layout.Controls.Add(Fill(_packageFolder), 1, 0);
        layout.Controls.Add(browsePackage, 2, 0);
        layout.Controls.Add(openPackage, 3, 0);

        layout.Controls.Add(Label("Сборка"), 0, 1);
        layout.Controls.Add(_builds, 1, 1);

        layout.Controls.Add(Label("Действие"), 0, 2);
        layout.Controls.Add(primary, 1, 2);
        layout.SetColumnSpan(primary, 3);

        layout.Controls.Add(Label("Точечно"), 0, 3);
        layout.Controls.Add(secondary, 1, 3);
        layout.SetColumnSpan(secondary, 3);

        var hint = new Label
        {
            Text = "Run.cmd сам восстанавливает ссылки, импортирует все .reg из .portable\\Registry/Registry/Reg, поднимает задачи/службы и только потом запускает программу.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMuted
        };
        layout.Controls.Add(Label("Что делает запуск"), 0, 4);
        layout.Controls.Add(hint, 1, 4);
        layout.SetColumnSpan(hint, 3);

        box.Controls.Add(layout);
        return box;
    }

    private GroupBox BuildGameRegistryPanel()
    {
        var box = new GroupBox
        {
            Text = "Reg-файлы игр внутри платформы",
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = TextPrimary,
            BackColor = Surface
        };

        var layout = NewGrid(4, 4);
        layout.Dock = DockStyle.Top;
        layout.Height = 44 * 3 + 72 + 6;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));

        var snapshot = MainButton("1. Снимок reg");
        snapshot.Width = 150;
        snapshot.Click += (_, _) => StartGameRegistryCapture();
        _tips.SetToolTip(snapshot, "Снимок реестра ДО установки игры. Нажми это перед тем, как ставить игру внутри платформы.");

        var finish = MainButton("2. Сохранить reg игры");
        finish.Width = 190;
        finish.Click += (_, _) => FinishGameRegistryCapture();
        _tips.SetToolTip(finish, "После установки игры: сравнить с снимком, найти НОВЫЕ ветки реестра и сохранить их как reg этой игры (имя — в поле «Название игры»).");

        var refresh = MainButton("Обновить reg платформы");
        refresh.Width = 190;
        refresh.Click += (_, _) => RefreshPlatformRegistry();
        _tips.SetToolTip(refresh, "Перезахватить reg самой платформы (не игры) в её текущем состоянии.");

        var openReg = MainButton("Папка reg игр");
        openReg.Width = 150;
        openReg.Click += (_, _) => OpenGameRegistryFolder();
        _tips.SetToolTip(openReg, "Открыть папку с reg-файлами игр пакета (можно положить свой .reg вручную).");

        var rebuildRun = MainButton("Пересобрать Run.cmd");
        rebuildRun.Width = 180;
        rebuildRun.Click += (_, _) => Run(true, OperationMode.Batches);
        _tips.SetToolTip(rebuildRun, "Перегенерировать Run.cmd/Stop.cmd пакета. НЕ запускает программу.");

        var browseGameFolder = SmallButton("...");
        // Не растягиваем на всю широкую колонку — компактная кнопка слева.
        browseGameFolder.Dock = DockStyle.None;
        browseGameFolder.Anchor = AnchorStyles.Left;
        browseGameFolder.Width = 46;
        browseGameFolder.Click += (_, _) => BrowseGameFolder();
        _tips.SetToolTip(browseGameFolder, "Указать папку, куда платформа ставит игру (напр. D:\\OnlineGames\\<игра>). Имя игры подставится из имени папки.");

        // Папка игры — из имени папки авто-подставляется «Название игры» (оно же — токен reg-поиска).
        layout.Controls.Add(Label("Папка игры"), 0, 0);
        layout.Controls.Add(Fill(_gameFolder), 1, 0);
        layout.Controls.Add(browseGameFolder, 2, 0);

        layout.Controls.Add(Label("Название игры"), 0, 1);
        layout.Controls.Add(Fill(_gameName), 1, 1);
        layout.Controls.Add(snapshot, 2, 1);
        layout.Controls.Add(finish, 3, 1);

        var hint = new Label
        {
            Text = "Укажите папку игры (имя подставится). Снимок reg → установите игру → сохранить reg игры. Reg попадут в .portable\\Registry\\Games.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMuted
        };
        layout.Controls.Add(hint, 1, 2);
        layout.Controls.Add(refresh, 3, 2);

        var hint2 = new Label
        {
            Text = "Для лаунчеров типа Epic/EA/RSI: сначала откройте portable-папку платформы, выберите сборку, сделайте снимок reg, установите игру, затем сохраните reg игры. При запуске платформы reg применится автоматически.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMuted
        };
        layout.Controls.Add(Label("Автоимпорт"), 0, 3);
        layout.Controls.Add(hint2, 1, 3);
        layout.Controls.Add(openReg, 2, 3);
        layout.Controls.Add(rebuildRun, 3, 3);

        // Хост: сверху — действия (фикс. высота), снизу — список игр платформы.
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, layout.Height));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Dock = DockStyle.Fill;
        host.Controls.Add(layout, 0, 0);
        host.Controls.Add(BuildGamesListSection(), 0, 1);

        box.Controls.Add(host);
        return box;
    }

    // Список вложенных игр выбранной платформы: галочка = игра включена
    // (её ссылки/reg идут в сборку и self-heal Run.cmd). Управляется кнопкой.
    private Control BuildGamesListSection()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Surface,
            Padding = new Padding(2, 6, 2, 2)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(new Label
        {
            Text = "Игры внутри платформы (галочка — включена в сборку)",
            Dock = DockStyle.Fill,
            ForeColor = Accent2,
            Font = new Font("Segoe UI Semibold", 9F),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var save = MainButton("Сохранить состояние игр");
        save.Width = 220;
        save.Click += (_, _) => SaveGamesState();
        _tips.SetToolTip(save, "Сохранить, какие игры из списка ниже (галочки) включены в сборку и попадут в Run.cmd.");
        grid.Controls.Add(save, 1, 0);

        _games.Dock = DockStyle.Fill;
        _games.BackColor = Bg;
        _games.ForeColor = TextPrimary;
        _games.BorderStyle = BorderStyle.None;
        _games.CheckOnClick = true;
        _games.Font = new Font("Segoe UI", 9.5F);
        _games.IntegralHeight = false;
        grid.Controls.Add(_games, 0, 1);
        grid.SetColumnSpan(_games, 2);

        return grid;
    }

    // Перечитать список игр выбранной платформы в чек-лист.
    private void RefreshGamesList()
    {
        _games.Items.Clear();
        _gamesBound = new List<GameModule>();
        if (_config is null)
        {
            return;
        }

        var name = _builds.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        AppProfile profile;
        try
        {
            profile = _config.FindProfile(name);
        }
        catch
        {
            return;
        }

        _gamesBound = profile.Games;
        foreach (var game in _gamesBound)
        {
            var idx = _games.Items.Add(game.Name);
            _games.SetItemChecked(idx, game.Enabled);
        }
    }

    private void SaveGamesState()
    {
        try
        {
            if (_config is null || _gamesBound.Count == 0)
            {
                Append("Нет игр для сохранения. Откройте платформу с вложенными играми.");
                return;
            }

            for (var i = 0; i < _gamesBound.Count && i < _games.Items.Count; i++)
            {
                _gamesBound[i].Enabled = _games.GetItemChecked(i);
            }

            var name = _builds.SelectedItem?.ToString();
            var profile = _config.FindProfile(name!);
            var config = _config;
            Append("⏳ Сохраняю состояние игр и пересобираю Run.cmd…");
            RunBusy(
                () =>
                {
                    ConfigStore.SavePackage(profile.ConfigDirectory, config);
                    PortableEngine.Execute(profile, new ExecutionOptions(true, OperationMode.Batches), Append);
                    return profile.Games.Count(g => g.Enabled);
                },
                enabled => Append($"Состояние игр сохранено. Включено: {enabled} из {_gamesBound.Count}."));
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    // «④ Библиотека» = каталог найденных пакетов (сверху) + шаблоны (снизу) в одной вкладке.
    private Control BuildLibraryPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Bg
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
        layout.Controls.Add(BuildPackageCatalogPanel(), 0, 0);
        layout.Controls.Add(BuildRecipesPanel(), 0, 1);
        return layout;
    }

    private GroupBox BuildPackageCatalogPanel()
    {
        var box = new GroupBox
        {
            Text = "Каталог пакетов",
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = TextPrimary,
            BackColor = Surface
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        layout.Controls.Add(new Label
        {
            Text = "Ищет пакеты (.portable\\manifest.json) в папках поиска: добавьте свою кнопкой «Добавить папку» (у каждого сервера своя). Папки собранных/открытых пакетов запоминаются сами. Двойной клик — открыть.",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        // Строка-фильтр: на сервере пакетов десятки — листать список дольше, чем набрать
        // пару букв. Фильтр по подстроке пути, без учёта регистра.
        _catalogFilter.Dock = DockStyle.Fill;
        _catalogFilter.BackColor = Bg;
        _catalogFilter.ForeColor = TextPrimary;
        _catalogFilter.BorderStyle = BorderStyle.FixedSingle;
        _catalogFilter.PlaceholderText = "Фильтр: часть имени или пути пакета…";
        _catalogFilter.Margin = new Padding(0, 4, 0, 4);
        _catalogFilter.TextChanged += (_, _) => ApplyCatalogFilter();
        layout.Controls.Add(_catalogFilter, 0, 1);

        _packageCatalog.Dock = DockStyle.Fill;
        _packageCatalog.BackColor = Bg;
        _packageCatalog.ForeColor = TextPrimary;
        _packageCatalog.BorderStyle = BorderStyle.None;
        _packageCatalog.Font = new Font("Consolas", 9.5F);
        _packageCatalog.DoubleClick += (_, _) => OpenSelectedCatalogPackage();
        layout.Controls.Add(_packageCatalog, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Surface
        };
        var addFolder = MainButton("Добавить папку");
        addFolder.Width = 160;
        addFolder.Click += (_, _) => AddCatalogFolder();
        _tips.SetToolTip(addFolder, "Указать папку, где искать пакеты на ЭТОМ сервере (у каждого своя). Запоминается; линкер сканит внутри неё. Папки сборок запоминаются и сами.");
        var scan = MainButton("Сканировать");
        scan.Width = 130;
        scan.Click += (_, _) => ScanPackageCatalog(true);
        _tips.SetToolTip(scan, "Пересканировать папки поиска и обновить список пакетов сейчас.");
        var open = MainButton("Открыть");
        open.Width = 110;
        open.Click += (_, _) => OpenSelectedCatalogPackage();
        _tips.SetToolTip(open, "Открыть выбранный пакет на вкладке «Применить/Проверить» (или двойной клик по строке).");
        var verify = MainButton("Проверить");
        verify.Width = 120;
        verify.Click += (_, _) =>
        {
            if (OpenSelectedCatalogPackage())
            {
                InspectPackage();
            }
        };
        _tips.SetToolTip(verify, "Открыть выбранный пакет и сразу проверить его целостность.");
        var verifyAll = SecondaryButton("Проверить все");
        verifyAll.Width = 130;
        verifyAll.Click += (_, _) => VerifyAllCatalogPackages();
        _tips.SetToolTip(verifyAll, "Проверить целостность ВСЕХ пакетов из списка (junction/reg/манифест) и показать итог в журнале. Только чтение.");
        actions.Controls.AddRange(new Control[] { addFolder, scan, open, verify, verifyAll });
        layout.Controls.Add(actions, 0, 3);

        box.Controls.Add(layout);
        return box;
    }

    // Перезаполняет видимый список из полного (_catalogAll) с учётом строки-фильтра.
    private void ApplyCatalogFilter()
    {
        var filter = _catalogFilter.Text.Trim();
        _packageCatalog.BeginUpdate();
        try
        {
            _packageCatalog.Items.Clear();
            foreach (var item in _catalogAll)
            {
                if (filter.Length == 0 || item.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    _packageCatalog.Items.Add(item);
                }
            }
        }
        finally
        {
            _packageCatalog.EndUpdate();
        }
    }

    private void AddCatalogFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Папка, где искать готовые пакеты на этом сервере (например корень с вашими сборками)"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        AddCatalogRoot(dlg.SelectedPath);
        Append("Папка поиска добавлена: " + dlg.SelectedPath);
        ScanPackageCatalog(true);
    }

    // Добавить папку в список поиска каталога (без шума). Вызывается кнопкой и
    // автоматически при сборке/открытии пакета — линкер «запоминает» расположения.
    private void AddCatalogRoot(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        folder = folder.TrimEnd('\\');
        if (!_settings.CatalogRoots.Any(r => string.Equals(r.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.CatalogRoots.Add(folder);
            _settings.Save();
        }
    }

    private GroupBox BuildRecipesPanel()
    {
        var box = new GroupBox
        {
            Text = "Шаблоны сборок (рецепты) — переносимые заготовки",
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = TextPrimary,
            BackColor = Surface
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        layout.Controls.Add(new Label
        {
            Text = "Рецепт = переносимый шаблон сборки приложения. Хранится в папке recipes\\ рядом с программой и едет вместе с линкером. «Создать из пакета» сохраняет открытый пакет как рецепт, «Применить рецепт» — создаёт из него пакет в выбранной папке.",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _recipeList.Dock = DockStyle.Fill;
        _recipeList.BackColor = Bg;
        _recipeList.ForeColor = TextPrimary;
        _recipeList.BorderStyle = BorderStyle.None;
        _recipeList.Font = new Font("Consolas", 9.5F);
        _recipeList.DoubleClick += (_, _) => ApplyRecipe();
        layout.Controls.Add(_recipeList, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Surface
        };
        var createBtn = MainButton("Создать из пакета");
        createBtn.Width = 180;
        createBtn.Click += (_, _) => CreateRecipeFromPackage();
        _tips.SetToolTip(createBtn, "Сохранить открытый пакет (вкладка «Применить/Проверить») как рецепт-шаблон.");
        var applyBtn = MainButton("Применить рецепт");
        applyBtn.Width = 170;
        applyBtn.Click += (_, _) => ApplyRecipe();
        _tips.SetToolTip(applyBtn, "Создать из выбранного рецепта новый пакет в указанной папке. Потом — «Сделать всё».");
        var folderBtn = SecondaryButton("Папка шаблонов");
        folderBtn.Width = 150;
        folderBtn.Click += (_, _) => OpenRecipesFolder();
        _tips.SetToolTip(folderBtn, "Открыть локальную папку recipes рядом с программой.");
        var pullBtn = SecondaryButton("Из общей папки");
        pullBtn.Width = 150;
        pullBtn.Click += (_, _) => UpdateRecipesFromShared();
        _tips.SetToolTip(pullBtn, "Подтянуть шаблоны из общей сетевой папки (один источник на клуб). Старые версии уйдут в recipes\\_history. Путь запоминается.");
        actions.Controls.AddRange(new Control[] { createBtn, applyBtn, folderBtn, pullBtn });
        layout.Controls.Add(actions, 0, 2);

        box.Controls.Add(layout);
        return box;
    }

    private void RefreshRecipes()
    {
        // Только локальные рецепты (recipes\ рядом с программой). Чтение в фоне.
        Task.Run(() => RecipeStore.List(null)).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                if (task.IsFaulted)
                {
                    Append("ОШИБКА чтения рецептов: " + task.Exception?.GetBaseException().Message);
                    return;
                }

                BeginInvoke(() =>
                {
                    _recipeList.Items.Clear();
                    foreach (var recipe in task.Result)
                    {
                        _recipeList.Items.Add(recipe);
                    }
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private void CreateRecipeFromPackage()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_packageFolder.Text))
            {
                Warn("Сначала откройте пакет на вкладке «Применить/Проверить».");
                return;
            }

            var profile = RequireSelectedProfile();
            var path = RecipeStore.SaveFromProfile(profile); // локально, рядом с программой
            Append("✔ Рецепт сохранён: " + path);
            RefreshRecipes();
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void ApplyRecipe()
    {
        try
        {
            if (_recipeList.SelectedItem is not RecipeInfo info)
            {
                Warn("Выберите рецепт в списке.");
                return;
            }

            var profile = RecipeStore.Load(info.Path);
            using var dlg = new FolderBrowserDialog
            {
                Description = $"Куда создать пакет «{profile.Name}» из рецепта"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var destRoot = dlg.SelectedPath;
            ConfigStore.SavePackage(destRoot, new PortableConfig { Profiles = [profile] });
            _packageFolder.Text = destRoot;
            LoadPackage();
            SelectTab(1);
            Append($"✔ Из рецепта «{profile.Name}» создан пакет: {destRoot}");
            Append("  Теперь нажмите «Сделать всё» — линкер перенесёт данные приложения и поставит ссылки.");
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void OpenRecipesFolder()
    {
        try
        {
            Directory.CreateDirectory(RecipeStore.LocalDir);
            Process.Start(new ProcessStartInfo { FileName = RecipeStore.LocalDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    // CLI --update-recipes --shared в UI: тянет шаблоны из общей сетевой папки
    // (один источник на клуб) в локальную recipes; старые версии → recipes\_history.
    private void UpdateRecipesFromShared()
    {
        try
        {
            var shared = _settings.SharedRecipesPath;
            if (string.IsNullOrWhiteSpace(shared) || !Directory.Exists(shared))
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Общая папка с шаблонами (один источник на клуб), напр. \\\\SERVER\\recipes",
                    SelectedPath = Directory.Exists(shared) ? shared : ""
                };
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                shared = dlg.SelectedPath;
            }

            Append($"⏳ Тяну шаблоны из общей папки: {shared}…");
            RunBusy(
                () => RecipeStore.UpdateFromShared(shared),
                count =>
                {
                    _settings.SharedRecipesPath = shared;
                    _settings.Save();
                    Append($"✔ Обновлено шаблонов из общей папки: {count} (прежние версии — в recipes\\_history).");
                    RefreshRecipes();
                });
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private Control BuildPlatformsPanel()
    {
        var box = new GroupBox
        {
            Text = "Платформы-лаунчеры (изучены линкером)",
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            Font = new Font("Segoe UI Semibold", 10F),
            ForeColor = TextPrimary,
            BackColor = Surface
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        layout.Controls.Add(new Label
        {
            Text = "Лаунчеры, которые линкер собирает спец-правилами (ссылки + ветки реестра сами). ✓ — найден на этом ПК. «Найти и собрать» подставит источник на вкладку «① Создать пакет». «Сохранить как шаблон» — заготовка появится на вкладке «Шаблоны». Двойной клик — найти и собрать.",
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _platformList.Dock = DockStyle.Fill;
        _platformList.BackColor = Bg;
        _platformList.ForeColor = TextPrimary;
        _platformList.BorderStyle = BorderStyle.None;
        _platformList.Font = new Font("Consolas", 9.5F);
        _platformList.DoubleClick += (_, _) => PlatformFindAndBuild();
        layout.Controls.Add(_platformList, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Surface
        };
        var scanBtn = MainButton("Сканировать ПК");
        scanBtn.Width = 160;
        scanBtn.Click += (_, _) => ScanPlatforms();
        _tips.SetToolTip(scanBtn, "Проверить, какие из известных лаунчеров установлены на этом ПК (по типовым папкам).");
        var buildBtn = MainButton("Найти и собрать");
        buildBtn.Width = 160;
        buildBtn.Click += (_, _) => PlatformFindAndBuild();
        _tips.SetToolTip(buildBtn, "Подставить найденную папку выбранного лаунчера на вкладку «Сборка» — там укажи «Куда собрать» и нажми сборку.");
        var recipeBtn = SecondaryButton("Сохранить как шаблон");
        recipeBtn.Width = 190;
        recipeBtn.Click += (_, _) => PlatformSaveAsRecipe();
        _tips.SetToolTip(recipeBtn, "Сделать из выбранного (найденного) лаунчера переносимый шаблон — появится на вкладке «Шаблоны». Без переноса данных.");
        actions.Controls.AddRange(new Control[] { scanBtn, buildBtn, recipeBtn });
        layout.Controls.Add(actions, 0, 2);

        box.Controls.Add(layout);
        return box;
    }

    private static string FormatPlatform(LauncherStatus s) =>
        s.Installed
            ? $"✓  {s.Launcher.Name}  —  {s.Folder}"
            : $"—  {s.Launcher.Name}   ({s.Launcher.Hint})  [не найден]";

    private void ScanPlatforms()
    {
        Task.Run(LauncherCatalog.DetectAll).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                if (task.IsFaulted)
                {
                    Append("ОШИБКА скана платформ: " + task.Exception?.GetBaseException().Message);
                    return;
                }

                BeginInvoke(() =>
                {
                    _platformsBound = task.Result.ToList();
                    var sel = _platformList.SelectedIndex;
                    _platformList.BeginUpdate();
                    _platformList.Items.Clear();
                    foreach (var status in _platformsBound)
                    {
                        _platformList.Items.Add(FormatPlatform(status));
                    }

                    _platformList.EndUpdate();
                    if (sel >= 0 && sel < _platformList.Items.Count)
                    {
                        _platformList.SelectedIndex = sel;
                    }

                    var found = _platformsBound.Count(s => s.Installed);
                    Append($"Платформы: всего {_platformsBound.Count}, найдено на этом ПК — {found}.");
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private LauncherStatus? SelectedPlatform()
    {
        var idx = _platformList.SelectedIndex;
        return idx >= 0 && idx < _platformsBound.Count ? _platformsBound[idx] : null;
    }

    private void PlatformFindAndBuild()
    {
        var status = SelectedPlatform();
        if (status is null)
        {
            Warn("Выберите платформу в списке.");
            return;
        }

        _appName.Text = status.Launcher.Name;
        if (status.Installed)
        {
            _mainFolder.Text = status.Folder!;
            SelectTab(0);
            Append($"Подставлено: «{status.Launcher.Name}», источник {status.Folder}. Укажите «Куда собрать» и нажмите сборку из готовой папки.");
        }
        else
        {
            SelectTab(0);
            Append($"«{status.Launcher.Name}» не найден в типовых папках. Укажите главную папку вручную на вкладке «Сборка».");
        }
    }

    private void PlatformSaveAsRecipe()
    {
        var status = SelectedPlatform();
        if (status is null)
        {
            Warn("Выберите платформу в списке.");
            return;
        }

        if (!status.Installed)
        {
            Warn($"«{status.Launcher.Name}» не найден на этом ПК — рецепт делается из установленного лаунчера.");
            return;
        }

        var name = status.Launcher.Name;
        var source = status.Folder!;
        Append($"⏳ Готовлю рецепт «{name}» из {source}…");
        RunBusy(() =>
        {
            // Собираем профиль БЕЗ применения (данные не переносим) во временную папку,
            // затем токенизируем пути и сохраняем как рецепт.
            var temp = Path.Combine(Path.GetTempPath(), "CPL_recipe_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = AutoPortableBuilder.BuildFromInstalledFolder(new AutoBuildRequest
                {
                    AppName = name,
                    PortableRoot = temp,
                    MainFolder = source,
                    ApplyAfterBuild = false
                }, _ => { });

                TokenizeProfilePaths(result.Profile);
                return RecipeStore.SaveFromProfile(result.Profile);
            }
            finally
            {
                try { if (Directory.Exists(temp)) { Directory.Delete(temp, true); } } catch { }
            }
        },
        path =>
        {
            Append($"✔ Рецепт сохранён: {path}");
            RefreshRecipes();
        });
    }

    // Заменяет абсолютные системные префиксы на env-токены — рецепт становится
    // переносимым между машинами/пользователями (как готовые Epic.json и т.п.).
    private static void TokenizeProfilePaths(AppProfile profile)
    {
        foreach (var link in profile.Links)
        {
            link.Source = TokenizePath(link.Source);
        }

        foreach (var batch in profile.Batches)
        {
            batch.TargetExe = TokenizePath(batch.TargetExe);
            batch.WorkingDirectory = TokenizePath(batch.WorkingDirectory);
        }
    }

    private static string TokenizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        // Порядок важен: x86 раньше обычного Program Files.
        (string Folder, string Token)[] map =
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "%ProgramFiles(x86)%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "%ProgramFiles%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%ProgramData%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        ];

        foreach (var (folder, token) in map)
        {
            if (!string.IsNullOrEmpty(folder) &&
                path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                return token + path[folder.Length..];
            }
        }

        return path;
    }

    private static TableLayoutPanel NewGrid(int columns, int rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            RowCount = rows,
            BackColor = Surface,
            Padding = new Padding(2)
        };

        for (var i = 0; i < rows; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, i == rows - 1 ? 72 : 44));
        }

        return grid;
    }

    private static Control Card(string title, Control content)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(16),
            Margin = new Padding(0, 0, 10, 12)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Surface
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 11.5F),
            ForeColor = TextPrimary,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        content.Dock = DockStyle.Fill;
        layout.Controls.Add(content, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private static Control InfoBlock(string title, string text)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(8, 2, 8, 2)
        };
        panel.Controls.Add(new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.3F),
            TextAlign = ContentAlignment.TopLeft
        });
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Accent2,
            Font = new Font("Segoe UI Semibold", 9.5F),
            TextAlign = ContentAlignment.MiddleLeft
        });
        return panel;
    }

    private static Label Label(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = TextMuted
        };
    }

    private static RoundedButton SmallButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            Width = 42,
            Height = 30,
            Dock = DockStyle.Fill,
            Radius = 7,
            BackColor = Surface2,
            HoverBackColor = Border,
            ForeColor = TextPrimary,
            BorderColor = Border,
            HoverBorderColor = Accent,
            BorderThickness = 1f,
            Font = new Font("Segoe UI", 10F)
        };
    }

    private static RoundedButton MainButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            Width = 112,
            Height = 34,
            Radius = 9,
            Margin = new Padding(4, 3, 4, 3),
            BackColor = Accent,
            HoverBackColor = Accent2,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
    }

    private static RoundedButton SecondaryButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            Width = 112,
            Height = 34,
            Radius = 9,
            Margin = new Padding(4, 3, 4, 3),
            BackColor = Surface2,
            HoverBackColor = Border,
            ForeColor = TextPrimary,
            BorderColor = Border,
            HoverBorderColor = Accent,
            BorderThickness = 1.2f,
            Font = new Font("Segoe UI Semibold", 9.5F)
        };
    }

    private static RoundedButton MagicButton(string text)
    {
        return new RoundedButton
        {
            Text = text,
            // 60px + шрифт 11pt: длинные тексты («Шаг 1 — снимок и запустить
            // установщик») влезают в строку без обрезки в «…» на узком окне.
            Height = 60,
            Dock = DockStyle.Fill,
            Radius = 12,
            Margin = new Padding(2, 6, 2, 4),
            BackColor = Accent,
            HoverBackColor = Accent2,
            GradientEnd = Accent2,
            ForeColor = Color.White,
            Multiline = true,
            Font = new Font("Segoe UI Semibold", 11F)
        };
    }

    private static Control Fill(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Segoe UI", 10F);
        box.BackColor = Surface2;
        box.ForeColor = TextPrimary;
        return box;
    }

    private Button NavButton(string text, int tabIndex)
    {
        var button = new RoundedButton
        {
            Text = text,
            Width = 180,
            Height = 42,
            Tag = tabIndex,
            Radius = 11,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 6, 0),
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.FromArgb(26, 16, 50),
            HoverBackColor = Color.FromArgb(40, 26, 72),
            EraseColor = Color.FromArgb(23, 14, 45),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI Semibold", 10F)
        };
        button.Click += (_, _) =>
        {
            if (tabIndex >= 0)
            {
                SelectTab(tabIndex);
                return;
            }

            _log.Focus();
            Append("Журнал активен.");
        };
        _navButtons.Add(button);
        return button;
    }

    private static Control StatusPill(string text, Color marker)
    {
        // Ширину считаем по РЕАЛЬНОМУ измерению текста текущим шрифтом, а не по
        // «len*8» — та формула не знала про DPI и резала текст на 125/150%.
        var pill = new PillChip(text, marker)
        {
            AutoFit = true,
            Height = 32,
            Margin = new Padding(9, 6, 0, 0)
        };
        pill.FitWidth();
        return pill;
    }

    private sealed class PillChip : Control
    {
        private Color _marker;

        // Маркер и текст меняются на лету (живые пилюли: игровой диск, busy-индикатор).
        public Color Marker
        {
            get => _marker;
            set { _marker = value; Invalidate(); }
        }

        // Подгонять ширину под текст текущим шрифтом. Для живых пилюль (диск/busy)
        // ширина пересчитывается при каждой смене текста — иначе длинный текст
        // («1863 ГБ свободно») упирался в фикс-ширину и резался.
        public bool AutoFit { get; set; }

        public void FitWidth()
        {
            var w = TextRenderer.MeasureText(Text, Font).Width + 44;
            Width = Math.Max(96, w);
            Parent?.PerformLayout();
        }

        public PillChip(string text, Color marker)
        {
            _marker = marker;
            Text = text;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = new Font("Segoe UI Semibold", 9F);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (AutoFit)
            {
                FitWidth();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedRect(rect, Height / 2);
            using (var fill = new LinearGradientBrush(rect, Surface2, Surface, LinearGradientMode.Vertical))
            {
                g.FillPath(fill, path);
            }
            using (var pen = new Pen(Color.FromArgb(120, _marker.R, _marker.G, _marker.B), 1.2f))
            {
                g.DrawPath(pen, path);
            }

            var dotR = 7;
            var dotY = (Height - dotR) / 2;
            using (var glowPath = new GraphicsPath())
            {
                glowPath.AddEllipse(11 - 4, dotY - 4, dotR + 8, dotR + 8);
                using var glow = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(160, _marker),
                    SurroundColors = [Color.FromArgb(0, _marker)]
                };
                g.FillPath(glow, glowPath);
            }
            using (var dot = new SolidBrush(_marker))
            {
                g.FillEllipse(dot, 11, dotY, dotR, dotR);
            }

            var textRect = new Rectangle(26, 0, Width - 30, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    private Control BuildStatusCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            AccentTop = Success,
            AccentBottom = Color.FromArgb(80, 200, 255),
            Padding = new Padding(20, 14, 12, 14),
            Margin = new Padding(0, 8, 0, 0)
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F),
            TextAlign = ContentAlignment.MiddleLeft
        };
        UpdateStatusCard();
        card.Controls.Add(_statusLabel);
        return card;
    }

    private void UpdateStatusCard()
    {
        if (_statusLabel is null)
        {
            return;
        }

        _statusLabel.Text =
            "Система: " + (IsCurrentProcessAdministrator() ? "готово" : "нужен admin") + Environment.NewLine +
            "ClientResources: " + (string.IsNullOrWhiteSpace(_clientResources.Text) ? "автопоиск" : _clientResources.Text) + Environment.NewLine +
            "Пакетов в каталоге: " + _catalogAll.Count;
    }

    private void SelectTab(int index)
    {
        if (_tabs is null || index < 0 || index >= _tabs.TabPages.Count)
        {
            return;
        }

        _tabs.SelectedIndex = index;
        UpdateNavigation();
    }

    // Только для dev-рендера (--render-ui): переключить вкладку из Program.
    public void SelectTabForRender(int index) => SelectTab(index);

    // Только для dev-рендера (--render-ui ... hover): выставить hover на всех
    // кнопках, чтобы проверить отрисовку их углов в наведённом состоянии.
    public void DevForceHoverForRender()
    {
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (k is RoundedButton rb)
                {
                    rb.ForceHoverForRender();
                }

                Walk(k);
            }
        }

        Walk(this);
    }

    // Только для dev-рендера (--render-icon): нарисовать иконку в реальных мелких
    // размерах (16/24/32/48) на шахматном фоне, чтобы оценить чёткость.
    public static void RenderIconStripForDev(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64 };
        var pad = 8;
        var width = sizes.Sum() + pad * (sizes.Length + 1);
        var height = sizes.Max() + pad * 2;
        using var strip = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(strip))
        {
            g.Clear(Color.FromArgb(60, 60, 70));
            var x = pad;
            foreach (var size in sizes)
            {
                var y = pad + (sizes.Max() - size) / 2;
                DrawLogo(g, new Rectangle(x, y, size, size));
                x += size + pad;
            }
        }
        strip.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void UpdateNavigation()
    {
        if (_tabs is null)
        {
            return;
        }

        foreach (var button in _navButtons)
        {
            var index = button.Tag is int value ? value : -1;
            var active = index == _tabs.SelectedIndex;
            button.ForeColor = active ? Color.White : TextMuted;
            if (button is RoundedButton rounded)
            {
                rounded.BackColor = active ? Accent : Color.FromArgb(26, 16, 50);
                rounded.HoverBackColor = active ? Accent : Color.FromArgb(40, 26, 72);
                rounded.GradientEnd = active ? Accent2 : Color.Empty;
                rounded.BorderColor = Color.Empty;
                rounded.HoverBorderColor = active ? Color.Empty : Color.FromArgb(120, Accent);
                rounded.BorderThickness = active ? 0f : 1f;
                rounded.Glow = active;
                rounded.GlowColor = Accent;
                rounded.Invalidate();
            }
        }
    }

    // Имена каталогов, в которые не лезем при поиске пакетов (скорость + нет смысла).
    private static readonly string[] CatalogSkipDirs =
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "$WinREAgent",
        "Program Files", "Program Files (x86)", "ProgramData", "AppData",
        "node_modules", "Recovery", "PerfLogs", "WindowsApps"
    };

    private void ScanPackageCatalog(bool verbose)
    {
        // Папки поиска — добавленные админом + разумные дефолты. Скан внутри них в фоне.
        var roots = BuildCatalogRoots();
        Task.Run(() => FindPackagesInRoots(roots)).ContinueWith(task =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                if (task.IsFaulted)
                {
                    Append("ОШИБКА сканирования каталога: " + task.Exception?.GetBaseException().Message);
                    return;
                }

                BeginInvoke(() =>
                {
                    _catalogAll.Clear();
                    _catalogAll.AddRange(task.Result);
                    ApplyCatalogFilter();

                    UpdateStatusCard();
                    if (verbose)
                    {
                        Append($"Каталог обновлён. Найдено пакетов: {_catalogAll.Count}." +
                            (_packageCatalog.Items.Count != _catalogAll.Count
                                ? $" Показано по фильтру: {_packageCatalog.Items.Count}."
                                : ""));
                    }
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { } // окно закрылось между проверкой и Invoke
        });
    }

    private List<string> BuildCatalogRoots()
    {
        var roots = new List<string>(_settings.CatalogRoots);
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            roots.Add(exeDir);
        }

        // Диск-агностично: типовые папки пакетов на ВСЕХ фиксированных дисках
        // (C:/D:/E:/F:…), а не только C:. Так каталог сам находит пакеты на игровых
        // дисках, не требуя «Добавить папку» вручную.
        try
        {
            foreach (var drv in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                roots.Add(Path.Combine(drv.Name, "Programs"));
                roots.Add(Path.Combine(drv.Name, "Portable"));
                roots.Add(Path.Combine(drv.Name, "ClubPortable"));
            }
        }
        catch
        {
            // недоступные диски — пропускаем
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Ищет папки с .portable\manifest.json ВНУТРИ указанных корней (папки поиска).
    // Глубина побольше (корни уже целевые), пропуск системных папок и junction.
    private static List<string> FindPackagesInRoots(List<string> roots)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = 80000; // потолок просмотренных папок, чтобы скан не «убегал»

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            ScanForManifest(root, 0, found, seen, ref budget, maxDepth: 7);
            if (budget <= 0)
            {
                break;
            }
        }

        return found;
    }

    private static void ScanForManifest(string dir, int depth, List<string> found, HashSet<string> seen, ref int budget, int maxDepth)
    {
        if (budget <= 0 || depth > maxDepth)
        {
            return;
        }

        budget--;

        try
        {
            if (File.Exists(ConfigStore.GetManifestPath(dir)) && seen.Add(Path.GetFullPath(dir).TrimEnd('\\')))
            {
                found.Add(Path.GetFullPath(dir).TrimEnd('\\'));
                return; // внутрь самого пакета углубляться не нужно
            }
        }
        catch
        {
            // нет доступа — пропускаем
        }

        if (depth >= maxDepth)
        {
            return;
        }

        IEnumerable<string> subs;
        try
        {
            subs = Directory.EnumerateDirectories(dir);
        }
        catch
        {
            return;
        }

        foreach (var sub in subs)
        {
            if (budget <= 0)
            {
                return;
            }

            var name = Path.GetFileName(sub);
            if (name.StartsWith(".", StringComparison.Ordinal) ||
                CatalogSkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue; // не идём по junction/symlink — иначе уйдём в цель ссылки/в цикл
                }
            }
            catch
            {
                continue;
            }

            ScanForManifest(sub, depth + 1, found, seen, ref budget, maxDepth);
        }
    }

    // Health-check всех пакетов каталога разом (read-only). Тяжёлую проверку
    // (обход дисков) делаем в фоне, в UI — только сводка.
    private void VerifyAllCatalogPackages()
    {
        var paths = _packageCatalog.Items.Cast<object>().Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList();
        if (paths.Count == 0)
        {
            Warn("Каталог пуст — сначала «Сканировать» или «Добавить папку».");
            return;
        }

        Append($"⏳ Проверяю все пакеты каталога: {paths.Count}…");
        RunBusy<(int Ok, int Bad)>(
            () =>
            {
                var ok = 0; var bad = 0;
                foreach (var p in paths)
                {
                    try
                    {
                        var report = PackageVerifier.Verify(p);
                        if (report.HasErrors)
                        {
                            bad++;
                            Append($"  ✖ {Path.GetFileName(p.TrimEnd('\\'))}: ПРОБЛЕМЫ ({report.Issues.Count(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))})");
                            foreach (var i in report.Issues.Where(i => i.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).Take(4))
                            {
                                Append($"      {i.Code}: {i.Message}");
                            }
                        }
                        else
                        {
                            ok++;
                            Append($"  ✔ {Path.GetFileName(p.TrimEnd('\\'))}: OK");
                        }
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        Append($"  ✖ {p}: {ex.Message}");
                    }
                }

                return (ok, bad);
            },
            res => Append(res.Bad == 0
                ? $"✅ Все пакеты целы: {res.Ok}/{paths.Count}."
                : $"⚠️ Итог: OK {res.Ok}, с проблемами {res.Bad} из {paths.Count}."));
    }

    private bool OpenSelectedCatalogPackage()
    {
        if (_packageCatalog.SelectedItem is not string packagePath)
        {
            Append("Каталог: сначала выберите пакет.");
            return false;
        }

        _packageFolder.Text = packagePath;
        LoadPackage();
        SelectTab(1);
        // Открытие удалось только если конфиг реально загрузился: иначе «Проверить»
        // отработал бы по ПРЕДЫДУЩЕМУ открытому пакету и дал вердикт не о том.
        return _config is not null;
    }

    private void WireDropTarget(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, args) =>
        {
            if (args.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                args.Effect = DragDropEffects.Copy;
            }
        };
        control.DragDrop += (_, args) =>
        {
            if (args.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                HandleDroppedPath(files[0]);
            }
        };
    }

    private void HandleDroppedPath(string path)
    {
        if (Directory.Exists(path))
        {
            if (ConfigStore.HasHiddenManifest(path) || File.Exists(Path.Combine(path, "profiles.json")))
            {
                _packageFolder.Text = path;
                LoadPackage();
                SelectTab(1);
                return;
            }

            _mainFolder.Text = path;
            if (string.IsNullOrWhiteSpace(_appName.Text))
            {
                _appName.Text = new DirectoryInfo(path).Name;
            }

            Append("Папка добавлена как источник: " + path);
            SelectTab(0);
            return;
        }

        if (!File.Exists(path))
        {
            return;
        }

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _installerPath.Text = path;
        if (string.IsNullOrWhiteSpace(_appName.Text))
        {
            _appName.Text = Path.GetFileNameWithoutExtension(path);
        }

        Append("Установщик добавлен: " + path);
        SelectTab(0);
    }

    private static bool IsCurrentProcessAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static readonly int[] IconSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static Icon CreateAppIcon()
    {
        using var stream = new MemoryStream();
        WriteIco(stream, IconSizes);
        stream.Position = 0;
        return new Icon(stream);
    }

    // Сборка многоразмерного ICO: каждый размер рисуется отдельно (16×16 остаётся
    // чётким, а не мутным даунскейлом). Формат кадра ГИБРИДНЫЙ:
    //   • <256 — классический 32bpp BMP/DIB. Его корректно рисуют ВСЕ контексты
    //     Windows: проводник (мелкие/крупные), диалог «Свойства», панель задач,
    //     Alt-Tab. Раньше эти кадры были PNG-in-ICO — часть декодеров шелла рисовала
    //     их мусором, отсюда «битый» лого в Свойствах и мелких видах.
    //   • 256 — PNG (стандарт для крупного размера, компактно, поддержан везде).
    private static void WriteIco(Stream output, int[] sizes)
    {
        var frames = new List<byte[]>(sizes.Length);
        foreach (var size in sizes)
        {
            using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                DrawLogo(graphics, new Rectangle(0, 0, size, size));
            }

            if (size >= 256)
            {
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                frames.Add(ms.ToArray());
            }
            else
            {
                frames.Add(BuildDibFrame(bitmap));
            }
        }

        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write((ushort)0);            // reserved
        writer.Write((ushort)1);            // type = icon
        writer.Write((ushort)sizes.Length); // image count

        var offset = 6 + 16 * sizes.Length; // после ICONDIR + всех ICONDIRENTRY
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            writer.Write((byte)(size >= 256 ? 0 : size)); // width (0 = 256)
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0);   // palette
            writer.Write((byte)0);   // reserved
            writer.Write((ushort)1); // color planes
            writer.Write((ushort)32);// bits per pixel
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame);
        }
    }

    // 32bpp BMP/DIB-кадр иконки: BITMAPINFOHEADER (biHeight = 2×size под XOR+AND),
    // далее пиксели BGRA СНИЗУ-ВВЕРХ, далее 1bpp AND-маска (нули = «брать пиксель»,
    // прозрачность углов берётся из альфа-канала). Это формат, который понимают все
    // контексты Windows без исключений.
    private static byte[] BuildDibFrame(Bitmap bitmap)
    {
        var size = bitmap.Width;
        var stride = size * 4;
        var xor = new byte[stride * size];

        var data = bitmap.LockBits(
            new Rectangle(0, 0, size, size),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var rowBuf = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, rowBuf, 0, stride);
                // Bitmap идёт сверху-вниз, DIB — снизу-вверх: строка y -> (size-1-y).
                Buffer.BlockCopy(rowBuf, 0, xor, (size - 1 - y) * stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var maskRowBytes = ((size + 31) / 32) * 4; // 1bpp, выравнивание строки на 4 байта
        var andSize = maskRowBytes * size;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);                    // biSize (BITMAPINFOHEADER)
        w.Write(size);                  // biWidth
        w.Write(size * 2);              // biHeight = XOR + AND
        w.Write((ushort)1);             // biPlanes
        w.Write((ushort)32);            // biBitCount
        w.Write(0);                     // biCompression = BI_RGB
        w.Write(xor.Length + andSize);  // biSizeImage
        w.Write(0); w.Write(0);         // biXPelsPerMeter / biYPelsPerMeter
        w.Write(0); w.Write(0);         // biClrUsed / biClrImportant
        w.Write(xor);                   // BGRA, снизу-вверх
        w.Write(new byte[andSize]);     // AND-маска = все нули
        w.Flush();
        return ms.ToArray();
    }

    // Только для dev-флага --make-ico: записать многоразмерный .ico на диск.
    public static void WriteIcoFile(string path)
    {
        using var fs = File.Create(path);
        WriteIco(fs, IconSizes);
    }

    private static void DrawLogo(Graphics graphics, Rectangle bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var radius = Math.Max(6, (int)(bounds.Width * 0.22f));
        using var clip = RoundedRect(bounds, radius);

        // Тёплая плитка: насыщенный оранжево-янтарный градиент сверху-вниз.
        using (var tile = new LinearGradientBrush(
                   new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(1, bounds.Height)),
                   Color.FromArgb(255, 196, 99),
                   Color.FromArgb(243, 96, 52),
                   LinearGradientMode.Vertical))
        {
            graphics.FillPath(tile, clip);
        }

        var oldClip = graphics.Clip;
        graphics.SetClip(clip);

        // Мягкий диагональный блик сверху для объёма.
        var glowRect = new Rectangle(bounds.X, bounds.Y, bounds.Width, (int)(bounds.Height * 0.62f));
        using (var glow = new LinearGradientBrush(
                   new Rectangle(glowRect.X, glowRect.Y, glowRect.Width, Math.Max(1, glowRect.Height)),
                   Color.FromArgb(85, 255, 255, 255),
                   Color.FromArgb(0, 255, 255, 255),
                   LinearGradientMode.Vertical))
        {
            graphics.FillRectangle(glow, glowRect);
        }

        // Глиф «портабл-хаб со связями»: центральный узел (portable-пакет)
        // и три отвода-ссылки к внешним узлам — на разные пути Windows и вложенные игры.
        DrawLinkGraph(graphics, bounds);

        graphics.Clip = oldClip;

        // Тонкая светлая окантовка плитки.
        using var borderPen = new Pen(Color.FromArgb(95, 255, 255, 255), Math.Max(1f, bounds.Width / 48f));
        graphics.DrawPath(borderPen, clip);
    }

    private static void DrawLinkGraph(Graphics graphics, Rectangle bounds)
    {
        // Два чётких взаимосвязанных звена цепи — однозначный символ «линкера».
        using var pen = new Pen(Color.White, Math.Max(2.5f, bounds.Width * 0.092f))
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        // Чуть больше разнесены, чтобы читались как ДВА взаимосвязанных звена,
        // а не сливались в один «магнифаер»-блоб на мелких размерах.
        DrawChainLink(graphics, bounds, pen, -0.135f, -0.135f);
        DrawChainLink(graphics, bounds, pen, 0.135f, 0.135f);
    }

    private static void DrawChainLink(Graphics graphics, Rectangle bounds, Pen pen, float offsetX, float offsetY)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(
            bounds.X + bounds.Width * (0.5f + offsetX),
            bounds.Y + bounds.Height * (0.5f + offsetY));
        graphics.RotateTransform(-40f);

        var w = bounds.Width * 0.5f;
        var h = bounds.Height * 0.24f;
        using var linkPath = RoundedRect(
            Rectangle.Round(new RectangleF(-w / 2f, -h / 2f, w, h)),
            Math.Max(4, (int)(h / 2f)));
        graphics.DrawPath(pen, linkPath);
        graphics.Restore(state);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class LogoMark : Control
    {
        public LogoMark()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Рисуем строго внутри контрола — свечение не «срезается» о край.
            e.Graphics.SetClip(ClientRectangle);

            // Плитка строго КВАДРАТНАЯ и по центру. Оставляем поля под свечение,
            // чтобы ни плитка, ни ореол не упирались в границу (раньше нижний край
            // глоу жёстко обрезался и плитка выглядела «обрезанной»).
            var side = Math.Max(1, Math.Min(Width, Height));
            var box = Math.Max(1, (int)(side * 0.66f));
            var x = (Width - box) / 2;
            var y = (Height - box) / 2;
            var cx = x + box / 2f;
            var cy = y + box / 2f;

            // Тёплое свечение — радиус меньше половины контрола, чтобы оно
            // полностью затухало ДО края (без жёсткой линии обреза).
            var glowR = Math.Min(box * 0.62f, Math.Min(Width, Height) / 2f - 1f);
            if (glowR > 1f)
            {
                using var glowPath = new GraphicsPath();
                glowPath.AddEllipse(cx - glowR, cy - glowR, glowR * 2, glowR * 2);
                using var glow = new PathGradientBrush(glowPath)
                {
                    CenterColor = Color.FromArgb(110, 255, 140, 70),
                    SurroundColors = [Color.FromArgb(0, 255, 140, 70)]
                };
                e.Graphics.FillPath(glow, glowPath);
            }

            DrawLogo(e.Graphics, new Rectangle(x, y, box, box));
        }
    }

    // Панель/таблица с вертикальным градиентом, заметным сквозь прозрачные подписи.
    private sealed class GradientPanel : Panel
    {
        public Color GradTop { get; set; } = Color.Black;
        public Color GradBottom { get; set; } = Color.Black;

        public GradientPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) { base.OnPaintBackground(e); return; }
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), GradTop, GradBottom, LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    private sealed class GradientGrid : TableLayoutPanel
    {
        public Color GradTop { get; set; } = Color.Black;
        public Color GradBottom { get; set; } = Color.Black;

        public GradientGrid()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) { base.OnPaintBackground(e); return; }
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), GradTop, GradBottom, LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    // Карточка: мягкая тень, скруглённые углы, лёгкий градиент тела и акцентная полоса слева.
    private sealed class CardPanel : Panel
    {
        public Color AccentTop { get; set; } = Accent;
        public Color AccentBottom { get; set; } = Accent2;

        public CardPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var backdrop = Parent?.BackColor ?? Bg;
            using (var fill = new SolidBrush(backdrop))
            {
                g.FillRectangle(fill, ClientRectangle);
            }

            var body = new Rectangle(2, 1, Math.Max(1, Width - 5), Math.Max(1, Height - 5));

            // Тонкая КОМПАКТНАЯ тень под нижним краем (без широкого размытого ореола,
            // который «расплывал» границу карточки на тёмном фоне).
            using (var shadowPath = RoundedRect(new Rectangle(body.X + 1, body.Y + 2, body.Width - 2, body.Height), 16))
            using (var shadowPen = new Pen(Color.FromArgb(45, 0, 0, 0), 1.5f))
            {
                g.DrawPath(shadowPen, shadowPath);
            }

            using var path = RoundedRect(body, 16);
            using (var bodyBrush = new LinearGradientBrush(body, SurfaceTop, SurfaceBottom, LinearGradientMode.Vertical))
            {
                g.FillPath(bodyBrush, path);
            }

            // Акцентная вертикальная полоска слева.
            var barRect = new Rectangle(body.X + 9, body.Y + 14, 4, Math.Min(46, body.Height - 28));
            using (var barPath = RoundedRect(barRect, 2))
            using (var barBrush = new LinearGradientBrush(barRect, AccentTop, AccentBottom, LinearGradientMode.Vertical))
            {
                g.FillPath(barBrush, barPath);
            }

            // Чёткая граница (чуть плотнее, чтобы край был ясным, а не размытым).
            using var border = new Pen(Color.FromArgb(180, Border.R, Border.G, Border.B), 1.4f);
            g.DrawPath(border, path);
        }
    }

    private sealed class RoundedButton : Button
    {
        private bool _hover;

        public int Radius { get; set; } = 8;
        public Color HoverBackColor { get; set; } = Color.Empty;
        public Color BorderColor { get; set; } = Color.Empty;
        public Color HoverBorderColor { get; set; } = Color.Empty;
        public float BorderThickness { get; set; }
        public Color GradientEnd { get; set; } = Color.Empty;
        public Color EraseColor { get; set; } = Color.Empty;
        public bool Glow { get; set; }
        public Color GlowColor { get; set; } = Accent;
        // Перенос на 2 строки вместо обрезки в «…» (для крупных кнопок-действий
        // с длинным текстом на узком окне).
        public bool Multiline { get; set; }

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            Cursor = Cursors.Hand; // кликабельность видна сразу
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        // Только для dev-рендера: показать кнопку в наведённом состоянии.
        public void ForceHoverForRender()
        {
            _hover = true;
            Invalidate();
        }

        // Углы скруглённой кнопки больше НЕ вырезаем через Region — он давал
        // «лесенку» по краю и тёмные квадраты в углах при наведении (частичная
        // перерисовка кнопки поверх прозрачного родителя оставляла мусор). Вместо
        // этого кнопка при каждой отрисовке сама дорисовывает ТОЧНЫЙ фон под собой:
        // если она лежит на градиентной шапке/сайдбаре — берём тот же градиент
        // срезом по своей позиции, и углы сливаются с фоном пиксель-в-пиксель при
        // любых цветах. Иначе — сплошной фон родителя.
        private void PaintBackdrop(Graphics g)
        {
            Color top = Color.Empty, bottom = Color.Empty;
            var ancHeight = 0;
            Control? anc = null;
            for (var c = Parent; c != null; c = c.Parent)
            {
                if (c is GradientGrid gg) { top = gg.GradTop; bottom = gg.GradBottom; ancHeight = gg.Height; anc = gg; break; }
                if (c is GradientPanel gp) { top = gp.GradTop; bottom = gp.GradBottom; ancHeight = gp.Height; anc = gp; break; }
            }

            if (anc != null && ancHeight > 0 && IsHandleCreated && anc.IsHandleCreated)
            {
                // Y кнопки в координатах градиентного предка — чтобы взять ровно
                // тот кусок вертикального градиента, что проходит под кнопкой.
                var originY = anc.PointToClient(PointToScreen(Point.Empty)).Y;
                using var brush = new LinearGradientBrush(
                    new Rectangle(0, -originY, Math.Max(1, Width), ancHeight),
                    top, bottom, LinearGradientMode.Vertical);
                g.FillRectangle(brush, ClientRectangle);
                return;
            }

            var flat = EraseColor != Color.Empty ? EraseColor : (Parent?.BackColor ?? BackColor);
            if (flat.A == 0) { flat = Bg; }
            using var solid = new SolidBrush(flat);
            g.FillRectangle(solid, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            PaintBackdrop(g);

            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            var radius = Math.Min(Radius, Math.Max(1, Math.Min(rect.Width, rect.Height) / 2));
            using var path = RoundedRect(rect, radius);

            var fillColor = _hover && HoverBackColor != Color.Empty ? HoverBackColor : BackColor;
            if (GradientEnd != Color.Empty)
            {
                using var fill = new LinearGradientBrush(rect, fillColor, GradientEnd, LinearGradientMode.ForwardDiagonal);
                g.FillPath(fill, path);
            }
            else
            {
                using var fill = new SolidBrush(fillColor);
                g.FillPath(fill, path);
            }

            // Глянцевый верхний блик для «живой» подсветки.
            if (Glow)
            {
                var glossRect = new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(1, rect.Height / 2));
                using var clip = RoundedRect(rect, radius);
                var oldClip = g.Clip;
                g.SetClip(clip);
                using (var gloss = new LinearGradientBrush(glossRect,
                           Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                {
                    g.FillRectangle(gloss, glossRect);
                }
                g.Clip = oldClip;
            }

            var borderColor = _hover && HoverBorderColor != Color.Empty ? HoverBorderColor : BorderColor;
            if (borderColor != Color.Empty && BorderThickness > 0)
            {
                using var pen = new Pen(borderColor, BorderThickness);
                g.DrawPath(pen, path);
            }

            var flags = TextFormatFlags.VerticalCenter;
            if (Multiline)
            {
                flags |= TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter;
            }
            else
            {
                flags |= TextFormatFlags.EndEllipsis | TextFormatFlags.WordEllipsis;
                flags |= TextAlign == ContentAlignment.MiddleLeft
                    ? TextFormatFlags.Left
                    : TextFormatFlags.HorizontalCenter;
            }

            var textRect = new Rectangle(
                rect.X + Padding.Left,
                rect.Y,
                Math.Max(1, rect.Width - Padding.Left - Padding.Right),
                rect.Height);
            TextRenderer.DrawText(g, Text, Font, textRect, ForeColor, flags);
        }
    }

    private void BrowseInstaller()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Установщики (*.exe;*.msi)|*.exe;*.msi|Все файлы (*.*)|*.*",
            FileName = _installerPath.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _installerPath.Text = dialog.FileName;
        }
    }

    // Лучший «игровой» диск для портабла: фиксированный НЕ системный том с макс.
    // свободным местом (обычно D:/E:). Нет такого — системный. Чтобы по умолчанию
    // пакеты собирались на отдельный диск, а не на C:.
    private static string PreferredGameDisk()
    {
        try
        {
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var best = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Where(d => !string.Equals(d.Name, sysRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
            return best?.Name ?? sysRoot;
        }
        catch
        {
            return @"C:\";
        }
    }

    // Проверяет, что заданы Название и «Куда собрать», ДО запуска установщика/снимка
    // и ДО необратимого подтверждения (раньше пустое назначение всплывало ошибкой
    // только в конце — после установки/подтверждения). Если назначение пустое, но имя
    // есть — подставляет дефолт на игровом диске.
    private bool EnsureBuildTargetReady()
    {
        if (string.IsNullOrWhiteSpace(_appName.Text))
        {
            Warn("Впишите «Название» программы — по нему называется папка пакета.");
            _appName.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(_portableRoot.Text))
        {
            _updatingDestination = true;
            _portableRoot.Text = Path.Combine(PreferredGameDisk(), "Programs", SafeFolderName(_appName.Text));
            _updatingDestination = false;
            Append("«Куда собрать» не было указано — подставил игровой диск: " + _portableRoot.Text);
        }

        return true;
    }

    private void BrowsePortableDestination()
    {
        var current = _portableRoot.Text.Trim();
        var selectedPath = Directory.Exists(current)
            ? current
            : Directory.Exists(Path.GetDirectoryName(current))
                ? Path.GetDirectoryName(current)!
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите базовую папку. Линкер добавит подпапку с названием программы.",
            SelectedPath = selectedPath
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _updatingDestination = true;
        _portableRoot.Text = BuildPackageDestination(dialog.SelectedPath, _appName.Text);
        _lastAppNameForDestination = SafeFolderName(_appName.Text);
        _updatingDestination = false;
    }

    private void BrowseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(target.Text)
                ? target.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    // Выбор папки игры: имя игры подставится из имени папки (через TextChanged).
    private void BrowseGameFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Папка, куда платформа ставит игру — имя подставится из названия папки",
            SelectedPath = Directory.Exists(_gameFolder.Text.Trim()) ? _gameFolder.Text.Trim() : ""
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _gameFolder.Text = dialog.SelectedPath;
        }
    }

    private void UpdatePortableDestinationName()
    {
        if (_updatingDestination)
        {
            return;
        }

        var raw = _appName.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var nextName = SafeFolderName(raw);
        var current = _portableRoot.Text.Trim();

        // Назначение пустое → подставляем игровой диск (D:/E:), чтобы по умолчанию
        // всё собиралось НЕ на системный C:, а на отдельный том.
        if (string.IsNullOrWhiteSpace(current))
        {
            _updatingDestination = true;
            _portableRoot.Text = Path.Combine(PreferredGameDisk(), "Programs", nextName);
            _updatingDestination = false;
            _lastAppNameForDestination = nextName;
            return;
        }

        var trimmed = current.TrimEnd('\\', '/');
        var last = Path.GetFileName(trimmed);
        if (!last.Equals(_lastAppNameForDestination, StringComparison.OrdinalIgnoreCase))
        {
            _lastAppNameForDestination = nextName;
            return;
        }

        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        _updatingDestination = true;
        _portableRoot.Text = Path.Combine(parent, nextName);
        _updatingDestination = false;
        _lastAppNameForDestination = nextName;
    }

    private static string BuildPackageDestination(string selectedPath, string appName)
    {
        var fullPath = Path.GetFullPath(selectedPath);
        if (ConfigStore.HasHiddenManifest(fullPath))
        {
            return fullPath;
        }

        var safeName = SafeFolderName(appName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            return fullPath;
        }

        var last = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        return last.Equals(safeName, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, safeName);
    }

    private static string SafeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "PortableApp" : safe;
    }

    private void StartInstallerWorkflow()
    {
        // Раннее: проверяем назначение/имя ДО снимка и запуска установщика,
        // чтобы пользователь не установил программу впустую (раньше ошибка вылезала
        // только на Шаге 2).
        if (string.IsNullOrWhiteSpace(_installerPath.Text))
        {
            Warn("Укажите путь к установщику (кнопка «...») или URL.");
            return;
        }

        if (!EnsureBuildTargetReady())
        {
            return;
        }

        var input = _installerPath.Text.Trim();
        var appName = _appName.Text.Trim();
        Append("⏳ Готовлю установщик и снимок папок…");

        // Скачивание установщика и снимок ФС — в фоне, чтобы окно не зависало.
        RunBusy<(string Installer, InstallSnapshot Snapshot)>(
            () =>
            {
                var installer = ResolveInstallerPath(input, appName);
                if (!File.Exists(installer))
                {
                    throw new FileNotFoundException("Установщик не найден.", installer);
                }

                var snapshot = AutoPortableBuilder.CaptureSnapshot(Append);
                return (installer, snapshot);
            },
            result =>
            {
                _installerPath.Text = result.Installer;
                _snapshot = result.Snapshot;
                Process.Start(new ProcessStartInfo
                {
                    FileName = result.Installer,
                    UseShellExecute = true
                });
                Append("Установщик запущен. Установите программу как обычно, потом нажмите «2. Собрать после установки».");
            });
    }

    // Без обращения к контролам (вызывается из фонового потока): имя берётся
    // параметром, скачанный путь возвращается, а _installerPath.Text ставится
    // вызывающим в onSuccess (UI-поток).
    private string ResolveInstallerPath(string input, string appName)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return input;
        }

        var downloads = Path.Combine(Path.GetTempPath(), "ClubPortableLinker", "Downloads");
        Directory.CreateDirectory(downloads);

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            fileName = SafeFileName(string.IsNullOrWhiteSpace(appName) ? "installer" : appName) + "-installer.exe";
        }

        var target = Path.Combine(downloads, fileName);
        Append("Скачиваю установщик: " + uri);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = http.GetAsync(uri).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using (var inputStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        using (var outputStream = File.Create(target))
        {
            inputStream.CopyTo(outputStream);
        }

        Append("Установщик скачан: " + target);
        return target;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "installer" : safe;
    }

    private void FinishInstallerWorkflow()
    {
        try
        {
            if (_snapshot is null)
            {
                Append("ОШИБКА: Сначала нажмите «Шаг 1 — снимок и запустить установщик».");
                return;
            }

            if (!EnsureBuildTargetReady())
            {
                return;
            }

            if (!TryConfirmAutoApply())
            {
                return;
            }

            var snapshot = _snapshot;
            var request = BuildRequest();
            Append("⏳ Собираю портабл из установленной программы…");
            RunBusy(() => AutoPortableBuilder.BuildFromSnapshot(snapshot, request, Append), OpenResult);
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void BuildFromInstalledFolder()
    {
        var main = _mainFolder.Text.Trim();
        if (!Directory.Exists(main))
        {
            Append("ОШИБКА: Укажите главную папку уже установленной программы.");
            return;
        }

        if (!EnsureBuildTargetReady())
        {
            return;
        }

        // Назначение не должно быть внутри исходной папки — иначе копирование/перенос
        // пошёл бы сам в себя.
        string dest;
        string srcFull;
        try
        {
            // GetFullPath кидает на вводе вида "\\SERVER" (неполный UNC) — без catch
            // исключение в click-handler валит приложение в стандартный диалог WinForms.
            dest = Path.GetFullPath(_portableRoot.Text.Trim()).TrimEnd('\\');
            srcFull = Path.GetFullPath(main).TrimEnd('\\');
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: некорректный путь — " + ex.Message);
            return;
        }
        if (dest.Equals(srcFull, StringComparison.OrdinalIgnoreCase) ||
            dest.StartsWith(srcFull + "\\", StringComparison.OrdinalIgnoreCase))
        {
            Warn("«Куда собрать» не должно быть внутри исходной папки. Выберите папку вне неё (лучше на игровом диске D:).");
            return;
        }

        // Папка назначения уже занята чужими данными (не наш пакет)? Предупреждаем —
        // чтобы не смешать сборку с посторонним содержимым.
        bool destHasForeignData;
        try
        {
            destHasForeignData = Directory.Exists(dest) && !ConfigStore.HasHiddenManifest(dest) &&
                Directory.EnumerateFileSystemEntries(dest).Any();
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: нет доступа к папке назначения — " + ex.Message);
            return;
        }

        if (destHasForeignData)
        {
            var ans = MessageBox.Show(this,
                $"Папка «{dest}» уже содержит файлы и это не готовый пакет.\nСборка добавит данные сюда. Продолжить?",
                "Папка не пуста", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ans != DialogResult.Yes)
            {
                Append("Отменено: выберите пустую папку для сборки.");
                return;
            }
        }

        if (!TryConfirmAutoApply())
        {
            return;
        }

        var request = BuildRequest();
        Append("⏳ Собираю портабл из готовой папки…");
        RunBusy(() => AutoPortableBuilder.BuildFromInstalledFolder(request, Append), OpenResult);
    }

    // Запускает тяжёлую операцию в фоне, чтобы окно не зависало, и показывает курсор ожидания.
    private async void RunBusy<T>(Func<T> work, Action<T>? onSuccess = null, Action? onFinally = null)
    {
        if (_busy)
        {
            Append("Подождите — предыдущая операция ещё выполняется.");
            // Вызывающий мог уже показать оверлей прогресса ДО RunBusy — без onFinally
            // он остался бы на экране навсегда (закрыть его некому).
            onFinally?.Invoke();
            return;
        }

        _busy = true;
        Cursor = Cursors.WaitCursor;
        SetBusyIndicator(true);
        try
        {
            var result = await Task.Run(work);
            onSuccess?.Invoke(result);
        }
        catch (OperationCanceledException ex)
        {
            Append(ex.Message);
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
        finally
        {
            _busy = false;
            Cursor = Cursors.Default;
            SetBusyIndicator(false);
            onFinally?.Invoke();
        }
    }

    // Видимый признак фоновой операции: пилюля в шапке + суффикс в заголовке окна
    // (виден в панели задач даже из другого приложения).
    private void SetBusyIndicator(bool busy)
    {
        if (string.IsNullOrEmpty(_baseTitle))
        {
            _baseTitle = Text;
        }

        if (_busyPill is not null)
        {
            _busyPill.Visible = busy;
        }

        Text = busy ? _baseTitle + " — выполняется…" : _baseTitle;
    }

    private bool TryConfirmAutoApply()
    {
        var result = MessageBox.Show(
            this,
            "Данные будут ПЕРЕМЕЩЕНЫ (не скопированы) в portable-папку, а на прежних путях останутся junction-ссылки.\n\n" +
            "Это делается один раз при сборке/применении. Откат вручную нетривиален.\n\n" +
            "Продолжить?",
            "Подтверждение — необратимое действие",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        return result == DialogResult.Yes;
    }

    private AutoBuildRequest BuildRequest()
    {
        return new AutoBuildRequest
        {
            AppName = _appName.Text.Trim(),
            PortableRoot = _portableRoot.Text.Trim(),
            MainFolder = _mainFolder.Text.Trim(),
            ClientResourcesRoot = _clientResources.Text.Trim(),
            SharedResourcesRoot = _sharedResources.Text.Trim(),
            ApplyAfterBuild = true
        };
    }

    private void OpenResult(PackageBuildResult result)
    {
        _packageFolder.Text = result.PackageFolder;
        LoadPackage();
        Append($"Portable-папка готова: {result.PackageFolder}");
    }

    private void LoadPackage()
    {
        _loadingPackage = true;
        try
        {
            var packageInput = _packageFolder.Text.Trim();
            _config = null; // при неудачной загрузке не должен остаться СТАРЫЙ пакет
            _config = ConfigStore.Load(packageInput);
            var packageRoot = ConfigStore.ResolvePortableRoot(packageInput);
            _packageFolder.Text = packageRoot;

            // Запоминаем папку, где лежит пакет — каталог потом найдёт его и соседей.
            AddCatalogRoot(Path.GetDirectoryName(packageRoot.TrimEnd('\\')));

            if (!ConfigStore.HasHiddenManifest(packageRoot))
            {
                var manifestPath = ConfigStore.SavePackage(packageRoot, _config);
                Append($"Legacy profiles.json перенесен в скрытый manifest: {manifestPath}");
            }

            _builds.Items.Clear();
            foreach (var build in _config.Profiles)
            {
                _builds.Items.Add(build.Name);
            }

            if (_builds.Items.Count > 0)
            {
                _builds.SelectedIndex = 0;
            }

            Append($"Открыто сборок: {_config.Profiles.Count}.");
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
        finally
        {
            _loadingPackage = false;
        }
    }

    private void Run(bool apply, OperationMode mode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_packageFolder.Text))
            {
                Warn("Сначала укажите portable-папку пакета (поле «Portable-папка»).");
                return;
            }

            _config ??= ConfigStore.Load(_packageFolder.Text.Trim());
            var name = _builds.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                if (_config.Profiles.Count == 0)
                {
                    Warn("В этой папке нет собранных пакетов. Откройте корректную portable-папку.");
                    return;
                }

                name = _config.Profiles[0].Name;
            }

            var build = _config.FindProfile(name);

            // Необратимые действия (перенос данных + junction) — спрашиваем подтверждение.
            if (apply && mode is OperationMode.All or OperationMode.Links)
            {
                if (!TryConfirmAutoApply())
                {
                    Append("Отменено пользователем.");
                    return;
                }
            }

            var options = new ExecutionOptions(apply, mode);
            ShowZipProgress(apply ? "Применяю изменения…" : "Считаю план…");
            RunBusy(
                () => PortableEngine.Execute(build, options, Append),
                result => Append(result.Success ? "✔ Готово." : "✖ Завершено с ошибками — смотрите журнал (строки «ОШИБКА»)."),
                HideZipProgress);
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void StartGameRegistryCapture()
    {
        // Мастер «добавить игру»: проверки + понятная пошаговая инструкция, затем снимок.
        // Гард до показа модального окна: иначе двойной клик открывал два диалога,
        // и второй снимок мог наполовину перезаписать _regSnapshot.
        if (_busy)
        {
            Append("Подождите — операция уже выполняется.");
            return;
        }

        if (_config is null || string.IsNullOrWhiteSpace(_packageFolder.Text))
        {
            Warn("Сначала откройте портабл-платформу на вкладке «Пакет» (кнопка «Открыть пакет»).");
            return;
        }

        if (string.IsNullOrWhiteSpace(_gameName.Text))
        {
            Warn("Впишите «Название игры» — под этим именем сохранятся её reg-файлы.");
            _gameName.Focus();
            return;
        }

        var step = MessageBox.Show(
            this,
            $"Мастер добавления игры «{_gameName.Text.Trim()}» в платформу.\n\n" +
            "1. Сейчас сделаю СНИМОК реестра (до установки).\n" +
            "2. Затем установите игру внутри платформы (Epic/Steam/EA…).\n" +
            "3. После установки нажмите «2. Сохранить reg игры» — линкер найдёт новые ветки реестра, " +
            "сохранит их и пропишет в Run.cmd (импорт при каждом старте).\n\n" +
            "Файлы игры: если ставите В папку лаунчера — едут сами; на отдельный диск — добавьте ссылку кнопкой «Ссылки».\n\n" +
            "Сделать снимок сейчас?",
            "Мастер: добавить игру",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (step != DialogResult.OK)
        {
            return;
        }

        Append("⏳ Снимок реестра до установки…");
        RunBusy(
            () => RegistryGameCollector.Capture(Append),
            snapshot =>
            {
                _regSnapshot = snapshot;
                Append("✔ Снимок сделан. Установите игру внутри платформы, затем нажмите «2. Сохранить reg игры».");
            });
    }

    private void FinishGameRegistryCapture()
    {
        var packageInput = _packageFolder.Text.Trim();
        var profileName = _builds.SelectedItem?.ToString() ?? "";
        var gameName = _gameName.Text.Trim();
        var gameFolder = _gameFolder.Text.Trim();
        var snapshot = _regSnapshot;
        if (snapshot is null)
        {
            // Без снимка захват идёт только по токенам имени/папки — это рабочий режим
            // для УЖЕ установленной игры, но пользователь должен понимать, что diff
            // «до/после установки» не используется (мог просто забыть Шаг 1).
            Append("Внимание: снимок reg (Шаг 1) не делался — ищу только по имени/папке игры, без сравнения «до/после».");
        }

        Append("⏳ Сохраняю reg игры и пересобираю Run.cmd…");

        // Тяжёлый reg-экспорт + пересборку Run.cmd делаем в одном фоновом проходе
        // (без вложенного RunBusy), а обновление UI — в onSuccess.
        RunBusy(
            () =>
            {
                var count = RegistryGameCollector.CaptureGameRegistry(
                    packageInput, profileName, gameName, gameFolder, snapshot, Append);

                var config = ConfigStore.Load(packageInput);
                if (string.IsNullOrWhiteSpace(profileName) && config.Profiles.Count == 0)
                {
                    throw new InvalidOperationException("В пакете нет собранных сборок — откройте корректную portable-папку.");
                }

                var profile = string.IsNullOrWhiteSpace(profileName)
                    ? config.Profiles[0]
                    : config.FindProfile(profileName);
                PortableEngine.Execute(profile, new ExecutionOptions(true, OperationMode.Batches), Append);
                return count;
            },
            count =>
            {
                // Снимок одноразовый: если оставить, захват СЛЕДУЮЩЕЙ игры посчитает
                // diff против старого снимка и утащит ключи прошлой игры в новую.
                _regSnapshot = null;
                LoadPackage();
                Append($"Reg-захват игры завершен. Файлов: {count}.");
            });
    }

    private void RefreshPlatformRegistry()
    {
        try
        {
            _config ??= ConfigStore.Load(_packageFolder.Text.Trim());
            var name = _builds.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Сначала откройте portable-папку и выберите сборку.");
            }

            var profile = _config.FindProfile(name);
            var config = _config;
            Append("⏳ Обновляю reg платформы…");
            RunBusy(
                () =>
                {
                    var count = RegistryGameCollector.RefreshExistingRegistryFiles(profile, Append);
                    ConfigStore.SavePackage(profile.ConfigDirectory, config);
                    return count;
                },
                count => Append($"Обновление reg платформы завершено. Файлов: {count}."));
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void InspectPackage()
    {
        try
        {
            var packageInput = _packageFolder.Text.Trim();
            var selected = _builds.SelectedItem?.ToString();
            var profile = RequireSelectedProfile();
            Append("⏳ Проверяю пакет…");
            ShowZipProgress("Проверка пакета…");

            // Проверка делает рекурсивный обход дисков — выполняем в фоне.
            // profile/selected/packageInput захвачены заранее; в фоне только
            // Append (маршалится) и файловые операции, без доступа к контролам.
            RunBusy<int>(
                () =>
                {
                    var problems = 0;
                    var report = PackageVerifier.Verify(packageInput, selected);
                    if (report.HasErrors)
                    {
                        problems++;
                    }

                    using (var writer = new StringWriter())
                    {
                        PackageVerifier.WriteText(report, writer);
                        foreach (var line in writer.ToString()
                                     .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries))
                        {
                            Append(line);
                        }
                    }

                    var portableRoot = PathTokens.Expand(profile.PortableRoot, profile);
                    Append($"Проверка пакета: {portableRoot}");
                    Append($"  ссылок: {profile.AllLinks().Count()}, reg в manifest: {profile.AllRegistryFiles().Count()}, батников: {profile.AllBatches().Count()}, задач: {profile.Tasks.Count}, служб: {profile.Services.Count}");

                    foreach (var link in profile.AllLinks())
                    {
                        var source = PathTokens.Expand(link.Source, profile);
                        var target = PathTokens.Expand(link.Target, profile);
                        var exists = Directory.Exists(source) || File.Exists(source);
                        var isLink = exists && File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint);
                        if (!isLink)
                        {
                            problems++;
                        }

                        Append($"  link {(isLink ? "OK" : exists ? "НЕ ссылка" : "нет")}: {source} -> {target}");
                    }

                    var regCount = new[] { ".portable\\Registry", "Registry", "Reg" }
                        .Select(folder => Path.Combine(portableRoot, folder))
                        .Where(Directory.Exists)
                        .Sum(folder => Directory.GetFiles(folder, "*.reg", SearchOption.AllDirectories).Length);
                    Append($"  reg-файлов на диске: {regCount}");

                    foreach (var batch in profile.AllBatches())
                    {
                        var path = PathTokens.Expand(batch.Path, profile);
                        if (!File.Exists(path))
                        {
                            problems++;
                            Append($"  batch нет: {path}");
                            continue;
                        }

                        var text = File.ReadAllText(path);
                        var regImport = text.Contains("reg import", StringComparison.OrdinalIgnoreCase) ? "reg OK" : "reg НЕТ";
                        // Новый Run.cmd ставит ссылки через `call :relink`, старый — через mklink.
                        var hasLinks = text.Contains("mklink", StringComparison.OrdinalIgnoreCase)
                            || text.Contains(":relink", StringComparison.OrdinalIgnoreCase);
                        var links = hasLinks ? "links OK" : "links НЕТ";
                        var resources = text.Contains("ClientResources", StringComparison.OrdinalIgnoreCase) ? "ClientResources OK" : "ClientResources НЕТ";
                        Append($"  batch OK: {path} ({regImport}, {links}, {resources})");
                    }

                    return problems;
                },
                problems => Append(problems == 0
                    ? "✔ Итог: пакет исправен, проблем не найдено."
                    : $"⚠ Итог: возможных проблем — {problems}. Это нормально, если ссылки ещё не применялись (нажмите «Сделать всё»). Смотрите строки выше."),
                HideZipProgress);
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void OpenRegistryFolder()
    {
        try
        {
            var profile = RequireSelectedProfile();
            var folder = Path.Combine(PathTokens.Expand(profile.PortableRoot, profile), ConfigStore.PortableDirectoryName, "Registry");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void OpenGameRegistryFolder()
    {
        try
        {
            var profile = RequireSelectedProfile();
            var folder = Path.Combine(PathTokens.Expand(profile.PortableRoot, profile), ConfigStore.PortableDirectoryName, "Registry", "Games");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void OpenPackageFolder()
    {
        try
        {
            var profile = RequireSelectedProfile();
            var folder = PathTokens.Expand(profile.PortableRoot, profile);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void ExportZipFromUi()
    {
        try
        {
            var profile = RequireSelectedProfile();
            var root = PathTokens.Expand(profile.PortableRoot, profile);
            if (!Directory.Exists(root))
            {
                Append("Пакет не найден: " + root);
                return;
            }

            var defaultName = (Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : "package") + "_Portable.zip";
            using var dlg = new SaveFileDialog
            {
                Title = "Сохранить portable-пакет как ZIP",
                Filter = "ZIP-архив (*.zip)|*.zip",
                FileName = defaultName,
                InitialDirectory = Path.GetDirectoryName(root.TrimEnd('\\')) ?? root
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            var outZip = dlg.FileName;
            Append("⏳ Пакую в ZIP… (большой пакет займёт время, окно не виснет)");
            ShowZipProgress("Упаковка в ZIP…");
            RunBusy<string>(
                () =>
                {
                    PackageArchiver.CreateZip(root, outZip, Append, UpdateZipProgress);
                    return outZip;
                },
                zip =>
                {
                    Append("✔ ZIP готов: " + zip);
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(zip)!, UseShellExecute = true });
                    }
                    catch
                    {
                        // открыть папку не критично
                    }
                },
                HideZipProgress);
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: " + ex.Message);
        }
    }

    private void CreateLauncherShortcut()
    {
        try
        {
            var profile = RequireSelectedProfile();
            var root = PathTokens.Expand(profile.PortableRoot, profile);
            var runCmd = Path.Combine(root, "Run.cmd");
            if (!File.Exists(runCmd))
            {
                Warn("Run.cmd не найден в пакете — сначала соберите/примените пакет.");
                return;
            }

            // Иконку берём из exe лаунчера (она в нём зашита). Нет exe — Windows даст дефолтную.
            var mainBatch = profile.AllBatches().FirstOrDefault(b =>
                    Path.GetFileName(PathTokens.Expand(b.Path, profile)).Equals("Run.cmd", StringComparison.OrdinalIgnoreCase))
                ?? profile.AllBatches().FirstOrDefault();
            var iconExe = mainBatch is null ? "" : PathTokens.Expand(mainBatch.TargetExe, profile);

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnkPath = Path.Combine(desktop, SafeFolderName(profile.Name) + ".lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                Warn("Не удалось создать ярлык: WScript.Shell недоступен.");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(lnkPath);
            shortcut.TargetPath = runCmd;
            shortcut.WorkingDirectory = root;
            if (!string.IsNullOrWhiteSpace(iconExe) && File.Exists(iconExe))
            {
                shortcut.IconLocation = iconExe + ",0";
            }

            shortcut.Description = profile.Name + " (portable)";
            shortcut.Save();

            Append($"✔ Ярлык на рабочем столе: {lnkPath}"
                + (File.Exists(iconExe) ? $" (иконка из {Path.GetFileName(iconExe)})" : " (иконка по умолчанию)"));
            Append("  Если перенесёшь пакет в другое место — пересоздай ярлык (он хранит абсолютный путь).");
        }
        catch (Exception ex)
        {
            Append("ОШИБКА: не удалось создать ярлык — " + ex.Message);
        }
    }

    private AppProfile RequireSelectedProfile()
    {
        _config ??= ConfigStore.Load(_packageFolder.Text.Trim());
        var name = _builds.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            if (_config.Profiles.Count == 1)
            {
                name = _config.Profiles[0].Name;
            }
            else
            {
                throw new InvalidOperationException("Сначала откройте portable-папку и выберите сборку.");
            }
        }

        return _config.FindProfile(name);
    }

    private static string? _clientResourcesRootCache;

    private static string DetectClientResourcesRoot()
    {
        // Кэш: обход всех готовых дисков (вкл. сетевые/съёмные — IsReady у «подвисшего»
        // сетевого тома блокирует на секунды) выполнялся ДВАЖДЫ в UI-потоке на старте.
        if (_clientResourcesRootCache is not null)
        {
            return _clientResourcesRootCache;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            var candidate = Path.Combine(drive.RootDirectory.FullName, "ClientResources");
            if (File.Exists(Path.Combine(candidate, "Autorun", "cmdow.exe")) ||
                Directory.Exists(Path.Combine(candidate, "Autorun")))
            {
                return _clientResourcesRootCache = candidate.TrimEnd('\\');
            }
        }

        return _clientResourcesRootCache = "";
    }

    private void Warn(string message)
    {
        Append(message);
        MessageBox.Show(this, message, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void Append(string message)
    {
        // Колбэк приходит из ФОНОВЫХ операций (сборка/zip/проверка): если окно уже
        // закрыли, Invoke на убитой ручке кидает ObjectDisposedException (краш при
        // выходе), а при уничтоженной ручке InvokeRequired=false и AppendText
        // выполнился бы прямо из фонового потока. Просто молчим.
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                Invoke(new Action<string>(Append), message);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // окно закрылось между проверкой и Invoke — терять строку лога не страшно
            }

            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        // Подсветка по смыслу: ошибки видно сразу, не вычитывая весь журнал.
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = ClassifyLogColor(message);
        _log.AppendText(line);
        _log.SelectionColor = _log.ForeColor;
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();

        if (!string.IsNullOrEmpty(_logFile))
        {
            try
            {
                File.AppendAllText(_logFile, line);
            }
            catch
            {
                // запись в файл лога не критична
            }
        }
    }

    private Color ClassifyLogColor(string message)
    {
        if (message.Contains("ОШИБКА", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Отказ", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(255, 128, 140); // мягкий красный — читаем на тёмном
        }

        if (message.StartsWith("✔", StringComparison.Ordinal) ||
            message.Contains("VERIFY OK", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("успеш", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Готово", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("готов:", StringComparison.OrdinalIgnoreCase))
        {
            return Success;
        }

        if (message.Contains("Внимание", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("⚠", StringComparison.Ordinal) ||
            message.Contains("ПРОПУСК", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Пропущен", StringComparison.OrdinalIgnoreCase))
        {
            return Accent2;
        }

        if (message.StartsWith("⏳", StringComparison.Ordinal))
        {
            return TextMuted;
        }

        return _log.ForeColor;
    }

    private static string BuildLogFilePath()
    {
        try
        {
            var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var dir = Path.Combine(baseDir, "Logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch
        {
            return "";
        }
    }

    private void ShowZipProgress(string text)
    {
        if (_zipOverlay == null)
        {
            var overlay = new Panel
            {
                Size = new Size(480, 80),
                BackColor = Color.FromArgb(44, 26, 80),
                Padding = new Padding(16, 12, 16, 14),
                Visible = false
            };
            var label = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = TextPrimary,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 10.5F),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = text
            };
            var bar = new ProgressBar
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 22
            };
            var spacer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            // Fill добавляем первым (докуется последним), затем bottom и top.
            overlay.Controls.Add(spacer);
            overlay.Controls.Add(bar);
            overlay.Controls.Add(label);

            _zipOverlay = overlay;
            _zipBarLabel = label;
            Controls.Add(overlay);
            Resize += (_, _) => { if (_zipOverlay is { Visible: true }) PositionZipOverlay(); };
        }

        _zipBarLabel!.Text = text;
        PositionZipOverlay();
        _zipOverlay!.Visible = true;
        _zipOverlay.BringToFront();
    }

    private void PositionZipOverlay()
    {
        if (_zipOverlay == null)
        {
            return;
        }

        _zipOverlay.Location = new Point(
            Math.Max(0, (ClientSize.Width - _zipOverlay.Width) / 2),
            Math.Max(0, ClientSize.Height - _zipOverlay.Height - 48));
    }

    private void UpdateZipProgress(int done, int total)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                Invoke(new Action<int, int>(UpdateZipProgress), done, total);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
            }

            return;
        }

        if (_zipBarLabel != null)
        {
            _zipBarLabel.Text = total > 0
                ? $"Упаковка в ZIP… файл {done} из {total}"
                : "Упаковка в ZIP…";
        }
    }

    private void HideZipProgress()
    {
        if (_zipOverlay != null)
        {
            _zipOverlay.Visible = false;
        }
    }

    private void OpenFullLog()
    {
        var win = new Form
        {
            Text = "Журнал — полный",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(960, 640),
            MinimumSize = new Size(480, 320),
            BackColor = Bg
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9.6F),
            BackColor = Color.FromArgb(15, 9, 30),
            ForeColor = Color.FromArgb(214, 226, 250),
            BorderStyle = BorderStyle.None,
            Text = _log.Text
        };

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Surface,
            Padding = new Padding(8)
        };
        var copy = MainButton("Копировать всё");
        copy.Width = 150;
        copy.Click += (_, _) =>
        {
            try { if (box.Text.Length > 0) { Clipboard.SetText(box.Text); } } catch { }
        };
        var save = MainButton("Сохранить в файл");
        save.Width = 170;
        save.Click += (_, _) =>
        {
            using var d = new SaveFileDialog { Filter = "Журнал (*.log;*.txt)|*.log;*.txt", FileName = "ClubPortableLinker.log" };
            if (d.ShowDialog(win) == DialogResult.OK)
            {
                try { File.WriteAllText(d.FileName, box.Text); }
                catch (Exception ex) { MessageBox.Show(win, ex.Message); }
            }
        };
        var openDir = MainButton("Папка лога");
        openDir.Width = 130;
        openDir.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(_logFile))
                {
                    Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(_logFile)!, UseShellExecute = true });
                }
            }
            catch { }
        };
        bar.Controls.AddRange(new Control[] { copy, save, openDir });

        win.Controls.Add(box);
        win.Controls.Add(bar);
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
        win.Show(this);
    }
}
