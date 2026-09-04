using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

class NonClientPanel : Panel { // спецкласи заголовка, що передають системні події миші батьківській формі (HTTRANSPARENT)
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0084) { // WM_NCHITTEST: перевірка типу ділянки під курсором
            m.Result = (IntPtr)(-1); // HTTRANSPARENT: робить панель «прозорою» для системних подій миші
            return;
        }
        base.WndProc(ref m);
    }
}

class NonClientButton : Button {
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0084) { // WM_NCHITTEST: перевірка типу ділянки під курсором
            m.Result = (IntPtr)(-1); // HTTRANSPARENT: дозволяє формі самостійно керувати кліками та наведенням
            return;
        }
        base.WndProc(ref m);
    }
}

class MinimalAiApp : Form {
    private WebView2 webView;  // основний контейнер рендерингу Chromium
    private TextBox urlInput;  // поле для введення веб-адрес
    private NonClientPanel? topBar;  // верхня панель-заголовок
    private NonClientButton? btnMin;  // кнопка згортання вікна
    private NonClientButton? btnMax;  // кнопка розгортання / відновлення вікна
    private NonClientButton? btnClose;  // кнопка закриття застосунку

    private const int BORDER_WIDTH = 2; // ширина ділянки захоплення для зміни розміру вікна мишкою
    private Rectangle normalBounds; // збережені розміри та координати вікна до його розгортання

    // змінні збереження стану
    private string zoomFile = "";
    private string lastFile = "";
    private string boundsFile = "";
    private System.Windows.Forms.Timer zoomTimer = new System.Windows.Forms.Timer { Interval = 600 };

        [StructLayout(LayoutKind.Sequential)]
    private struct POINT {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const int WM_GETMINMAXINFO = 0x0024; // запит обмежень максимального розміру та позиції вікна

    // Win32 API для переміщення вікна мишкою
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    // системні повідомлення Win32 для обробки меж вікна, перетягування та наведення
    private const int WM_NCCALCSIZE   = 0x0083;  // розрахунок розміру клієнтської ділянки
    private const int WM_NCHITTEST    = 0x0084;  // запит про тип ділянки під вказівником миші
    private const int WM_NCMOUSEMOVE    = 0x00A0; // рух курсора по неклієнтській ділянці (кнопках)
    private const int WM_NCLBUTTONDOWN  = 0x00A1; // натискання лівої кнопки миші на неклієнтській ділянці
    private const int WM_NCLBUTTONUP    = 0x00A2; // відпускання лівої кнопки миші на неклієнтській ділянці
    private const int WM_NCMOUSELEAVE = 0x02A2;  // вихід курсора за межі неклієнтської ділянки

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_GETMINMAXINFO) { // обмежуємо розмір розгорнутого вікна межами робочої зони дисплея
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            var screen = Screen.FromHandle(this.Handle);
            var workArea = screen.WorkingArea;
            var bounds = screen.Bounds;

            mmi.ptMaxPosition.x = workArea.Left - bounds.Left;
            mmi.ptMaxPosition.y = workArea.Top - bounds.Top;
            mmi.ptMaxSize.x = workArea.Width;
            mmi.ptMaxSize.y = workArea.Height;

            Marshal.StructureToPtr(mmi, m.LParam, true);
            return;
        }
        
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) {  // прибираємо стандартну білу рамку Windows, розширюючи робочу ділянку
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST) {   // обробка реакції на краї, заголовок та кнопки (вмикає меню Snap Layouts)
            int x = unchecked((short)(long)m.LParam);    // розпакування координати X (зі знаком для кількох моніторів)
            int y = unchecked((short)((long)m.LParam >> 16)); // розпакування координати Y (зі знаком для кількох моніторів)
            var screenPoint = new Point(x, y); // крапка на екрані
            var clientPoint = this.PointToClient(screenPoint); // переведення екранних координат у координати форми
         
            if (topBar != null && topBar.Bounds.Contains(clientPoint)) { // спочатку перевіряємо системні кнопки (вони мають пріоритет над верхньою межею ресайзу)
                var topPoint = topBar.PointToClient(screenPoint);
                if (btnClose != null && btnClose.Bounds.Contains(topPoint)) { m.Result = (IntPtr)20; return; } // HTCLOSE: кнопка закриття
                if (btnMax != null && btnMax.Bounds.Contains(topPoint))     { m.Result = (IntPtr)9;  return; } // HTMAXBUTTON: кнопка розгортання та Snap Layouts
                if (btnMin != null && btnMin.Bounds.Contains(topPoint))     { m.Result = (IntPtr)8;  return; } // HTMINBUTTON: кнопка згортання
                if (urlInput != null && urlInput.RectangleToScreen(urlInput.ClientRectangle).Contains(screenPoint)) { return; } // введення тексту в адресному рядку
            }
         
            if (this.WindowState == FormWindowState.Normal) {  // визначення меж для зміни розміру вікна (лише у звичайному стані)
                if (clientPoint.Y <= BORDER_WIDTH) {
                    if (clientPoint.X <= BORDER_WIDTH) { m.Result = (IntPtr)13; return; } // HTTOPLEFT: верхній лівий кут
                    if (clientPoint.X >= this.ClientSize.Width - BORDER_WIDTH) { m.Result = (IntPtr)14; return; } // HTTOPRIGHT: верхній правий кут
                    m.Result = (IntPtr)12; return; // HTTOP: верхня межа
                }
                if (clientPoint.Y >= this.ClientSize.Height - BORDER_WIDTH) {
                    if (clientPoint.X <= BORDER_WIDTH) { m.Result = (IntPtr)16; return; } // HTBOTTOMLEFT: нижній лівий кут
                    if (clientPoint.X >= this.ClientSize.Width - BORDER_WIDTH) { m.Result = (IntPtr)17; return; } // HTBOTTOMRIGHT: нижній правий кут
                    m.Result = (IntPtr)15; return; // HTBOTTOM: нижня межа
                }
                if (clientPoint.X <= BORDER_WIDTH) { m.Result = (IntPtr)10; return; } // HTLEFT: ліва межа
                if (clientPoint.X >= this.ClientSize.Width - BORDER_WIDTH) { m.Result = (IntPtr)11; return; } // HTRIGHT: права межа
            }
          
            if (topBar != null && topBar.Bounds.Contains(clientPoint)) {  // решта верхньої панелі - заголовок для перетягування
                m.Result = (IntPtr)2; return; // HTCAPTION: заголовок для нативного перетягування
            }
            m.Result = (IntPtr)1;  // HTCLIENT: робоча ділянка вмісту форми (WebView2)
            return;
        }
      
        if (m.Msg == WM_NCLBUTTONDOWN) {  // блокуємо стандартне малювання системних білих кнопок Windows при натисканні
            int hit = m.WParam.ToInt32();
            if (hit == 9 || hit == 8 || hit == 20) { // HTMAXBUTTON, HTMINBUTTON, HTCLOSE
                return; // не передаємо в base.WndProc, щоб уникнути появи білого квадрата
            }
        }
     
        if (m.Msg == WM_NCLBUTTONUP) {   // обробляємо клік на кнопках заголовка самостійно
            int hit = m.WParam.ToInt32();
            if (hit == 9) { // HTMAXBUTTON
                ToggleMaximize();
                return;
            }
            if (hit == 8) { // HTMINBUTTON
                this.WindowState = FormWindowState.Minimized;
                return;
            }
            if (hit == 20) { // HTCLOSE
                this.Close();
                return;
            }
        }

        if (m.Msg == WM_NCMOUSEMOVE) { // підсвітка кнопок при наведенні курсора
            int x = unchecked((short)(long)m.LParam);
            int y = unchecked((short)((long)m.LParam >> 16));
            UpdateButtonHover(new Point(x, y));
            base.WndProc(ref m);
            return;
        }

        if (m.Msg == WM_NCMOUSELEAVE || m.Msg == 0x0200) { // скидання підсвітки кнопок при виході курсора за межі
            ResetButtonHover();
            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    private void ResetButtonHover() { // повернення фонового кольору кнопок до початкового
        if (topBar == null) return;
        Color defaultBg = topBar.BackColor;
        if (btnClose != null) btnClose.BackColor = defaultBg;
        if (btnMax != null)   btnMax.BackColor   = defaultBg;
        if (btnMin != null)   btnMin.BackColor   = defaultBg;
    }

    private void UpdateButtonHover(Point screenPoint) { // зміна фонового кольору кнопки при наведенні на неї
        if (topBar == null) return;
        Point topPoint = topBar.PointToClient(screenPoint);
        Color defaultBg = topBar.BackColor;

        if (btnClose != null) btnClose.BackColor = btnClose.Bounds.Contains(topPoint) ? Color.FromArgb(196, 43, 28) : defaultBg;
        if (btnMax != null)   btnMax.BackColor   = btnMax.Bounds.Contains(topPoint)   ? Color.FromArgb(50, 50, 55)   : defaultBg;
        if (btnMin != null)   btnMin.BackColor   = btnMin.Bounds.Contains(topPoint)   ? Color.FromArgb(50, 50, 55)   : defaultBg;
    }

    private void ToggleMaximize() { // перемикання розгортання вікна зі збереженням попередніх розмірів
        if (this.WindowState == FormWindowState.Maximized) {
            this.WindowState = FormWindowState.Normal; // повертаємо у звичайний стан
            if (!normalBounds.IsEmpty) {
                this.Bounds = normalBounds; // відновлюємо точний розмір і координати, що були до розгортання
            }
        } else {
            normalBounds = this.Bounds; // фіксуємо поточні розміри перед розгортанням на весь екран
            this.WindowState = FormWindowState.Maximized;
        }
    }

    protected override void OnResizeEnd(EventArgs e) { // збереження розмірів після завершення розтягування мишкою
        base.OnResizeEnd(e);
        if (this.WindowState == FormWindowState.Normal) {
            normalBounds = this.Bounds;  // оновлюємо збережені межі
        }
    }

    protected override void OnLocationChanged(EventArgs e) { // збереження нової позиції після переміщення вікна
        base.OnLocationChanged(e);
        if (this.WindowState == FormWindowState.Normal) {
            normalBounds = this.Bounds;  // оновлюємо збережені координати
        }
    }

    [STAThread]
    static void Main() {
        // передаємо керування масштабуванням від Windows безпосередньо рушію для чіткого рендерингу
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MinimalAiApp());
    }

    public MinimalAiApp() {   // налаштування параметрів головного вікна
      
        this.Text = "bro";
        this.Width = 1080;
        this.Height = 1080;
        this.normalBounds = this.Bounds; // фіксуємо початковий розмір і розташування форми

        webView = new WebView2 { Dock = DockStyle.Fill };  // ініціюємо WebView2 та розтягуємо на всю робочу площу

        // відступи створюють смугу навколо форми для зміни розміру вікна мишкою
        this.Padding = new Padding(BORDER_WIDTH, 0, BORDER_WIDTH, BORDER_WIDTH);
        webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(22, 22, 22); // темний колір фону WebView2
        this.BackColor = System.Drawing.Color.FromArgb(22, 22, 22);   // темний колір фону самої форми
        this.FormBorderStyle = FormBorderStyle.Sizable;   // прибираємо стандартний системний заголовок Windows

        topBar = new NonClientPanel {   // панель-заголовок висотою 42px
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = System.Drawing.Color.FromArgb(22, 22, 22),
            Padding = new Padding(14, 0, 0, 0) // без відступів по вертикалі і справа, щоб кнопки були на всю висоту і впритул до краю
        };

        void DragWindow(object? s, MouseEventArgs args) {   // допоміжна функція перетягування вікна за заголовок
            if (args.Button == MouseButtons.Left) {
                ReleaseCapture(); // звільняємо захоплення миші
                SendMessage(this.Handle, 0xA1, 0x2, 0); // WM_NCLBUTTONDOWN + HTCAPTION
            }
        }
        topBar.MouseDown += DragWindow;

        // фабрика створення кнопок керування вікном
        NonClientButton CreateSysButton(string text, EventHandler onClick, System.Drawing.Color hoverBg) {
            var btn = new NonClientButton {
                Text = text,
                Dock = DockStyle.Right,
                Width = 56,
                FlatStyle = FlatStyle.Flat,
                ForeColor = System.Drawing.Color.FromArgb(180, 180, 180),
                Font = new System.Drawing.Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0; // прибираємо рамку кнопки
            btn.FlatAppearance.MouseOverBackColor = hoverBg; // колір підсвітки при наведенні
            btn.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(hoverBg.R - 12, hoverBg.G - 12, hoverBg.B - 12); // колір при натисканні
            btn.Click += onClick;
            return btn;
        }

        // створення кнопок (червоний фон для закриття, темно-сірий для інших)
        btnClose = CreateSysButton("✕", (s, a) => this.Close(), System.Drawing.Color.FromArgb(196, 43, 28));
        btnMax = CreateSysButton("🗗", (s, a) => ToggleMaximize(), System.Drawing.Color.FromArgb(50, 50, 55));
        btnMin = CreateSysButton("_", (s, a) => this.WindowState = FormWindowState.Minimized, System.Drawing.Color.FromArgb(50, 50, 55));

        var dragSpacer = new NonClientPanel {  // проміжок між рядком адреси та кнопками для зручного перетягування
            Dock = DockStyle.Right,
            Width = 200,
            BackColor = System.Drawing.Color.FromArgb(60, 60, 60)
        };
        dragSpacer.MouseDown += DragWindow;

        var urlContainer = new NonClientPanel {  // створюємо контейнер для адресного рядка з відступом зверху
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(22, 22, 22),
            Padding = new Padding(0, 1, 0, 0)     // відступ зверху (ліворуч, зверху, праворуч, знизу)
        };

        urlInput = new TextBox {   // вбудований у заголовок адресний рядок
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(22, 22, 22),
            ForeColor = System.Drawing.Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new System.Drawing.Font("Segoe UI", 10.5F)
        };
        urlContainer.Controls.Add(urlInput);  // кладемо urlInput всередину контейнера:

        urlInput.KeyDown += (s, args) => {    // перехід за адресою при натисканні Enter
            if (args.KeyCode == Keys.Enter) {
                args.SuppressKeyPress = true; // вимикаємо системний звуковий сигнал клавіші Enter
                string target = urlInput.Text.Trim();
                if (!target.StartsWith("http://") && !target.StartsWith("https://")) {
                    target = "https://" + target; // додаємо протокол за замовчуванням
                }
                webView.CoreWebView2?.Navigate(target);
            }
        };

        // порядок розташування елементів у верхній панелі
        topBar.Controls.Add(urlContainer);  // контейнер займає весь вільний простір
        topBar.Controls.Add(dragSpacer);   // ділянка для перетягування
        topBar.Controls.Add(btnMin);      // кнопка згортання
        topBar.Controls.Add(btnMax);      // кнопка розгортання
        topBar.Controls.Add(btnClose);     // кнопка закриття

        this.Controls.Add(webView); // додаємо контейнер веб-сторінки
        this.Controls.Add(topBar);  // додаємо панель заголовка

        this.Load += InitWebView; // підписка на запуск ініціалізації середовища
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        if (btnMax != null) {
            btnMax.Text = (this.WindowState == FormWindowState.Maximized) ? "❐" : "🗗"; // оновлюємо піктограму на кнопці
        }

        // при розгортанні на весь екран прибираємо відступи, щоб вміст займав увесь дисплей
        this.Padding = (this.WindowState == FormWindowState.Maximized) 
            ? new Padding(0) 
            : new Padding(BORDER_WIDTH, 0, BORDER_WIDTH, BORDER_WIDTH);
    }

    // ініціалізація та налаштування веб-рушія
    private async void InitWebView(object? sender, EventArgs e) {
        try {
            var args =    // прапорці оптимізації рушія Chromium для WebView2:
                // блокування логування та дампів
                "--disable-logging " +
                "--log-level=3 " +
                "--disable-crash-reporter " +
                "--disable-breakpad " +
                // вимкнення дискового кешу перенаправляє файли кешу у системну "чорну діру" Windows, лише Cookies та LocalStorage залишаться у папці bro_profile
                "--disk-cache-dir=NUL " + 
                "--media-cache-size=1 " +      // вимикаємо накопичення аудіо- та відеокешу на ssd
                // зниження фонової активності та телеметрії
                "--disable-background-networking " +   // вимикаємо фонові службові запити Google
                "--disable-component-update " +        // вимикаємо завантаження фонових модулів Chromium
                "--disable-sync " +
                "--no-first-run " +
                "--disable-features=OptimizationHints,AutofillServerCommunication,EdgeFeedback,msSmartScreenProtection";
    
            // профіль (авторизація, налаштування) у теці поряд із виконуваним файлом
            string userDataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bro_profile");
            var options = new CoreWebView2EnvironmentOptions(args);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    
            await webView.EnsureCoreWebView2Async(env);   // старт ізольованого середовища
    
            zoomFile = Path.Combine(userDataFolder, "zoom.txt");  // читання та застосування збереженого рівня масштабування сторінки
            lastFile = Path.Combine(userDataFolder, "last.txt");
            boundsFile = Path.Combine(userDataFolder, "bounds.txt");
    
            var core = webView.CoreWebView2;
                
            // прибираємо білий спалах перед завантаженням сторінки
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
    
            // блокування аналітики та телеметрії (з фіксом MemoryStream)
            core.AddWebResourceRequestedFilter("*google-analytics.com*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("*googletagmanager.com*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("*doubleclick.net*", CoreWebView2WebResourceContext.All);
                
            core.WebResourceRequested += (s, reqArgs) => {   // повертаємо статус 404 замість завантаження трекерів для економії ресурсів
                reqArgs.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 404, "Blocked", "");
            };
    
            core.SourceChanged += (s, args) => { urlInput.Text = core.Source; }; // синхронізуємо адресу у рядку вводу при переходах за посиланнями
    
            // відкладений запис масштабу (Debounce) при його зміні (Ctrl + коліщатко миші)
            zoomTimer.Tick += (s, a) => {
                zoomTimer.Stop();
                try { File.WriteAllText(zoomFile, webView.ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            };
            webView.ZoomFactorChanged += (s, a) => { zoomTimer.Stop(); zoomTimer.Start(); };
    
            RestoreState(); // відновлюємо розмір вікна, масштаб та останню адресу
        }
        catch (Exception ex) {
            MessageBox.Show($"Помилка ініціалізації рушія:\n{ex.Message}", "Критична помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            this.Close();
        }
    }

    private void RestoreState() {  // функція відновлення стану при запуску
        if (webView?.CoreWebView2 == null) return;  // захист від NullReferenceException
        try {        // відновлення збереженого масштабу
            if (File.Exists(zoomFile) && double.TryParse(File.ReadAllText(zoomFile), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double z)) {
                if (z >= 0.25 && z <= 5.0) webView.ZoomFactor = z; 
            }
        } catch { }

        try {     // відновлення позиції та розміру вікна
            if (File.Exists(boundsFile)) {
                var parts = File.ReadAllText(boundsFile).Split(',');
                if (parts.Length == 5) {
                    this.normalBounds = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                    this.Bounds = this.normalBounds;
                    this.WindowState = (FormWindowState)int.Parse(parts[4]);
                }
            }
        } catch { }

        string startUrl = "https://gemini.google.com";  // початкова сторінка за замовчуванням
        try {     // відновлення останньої адреси
            if (File.Exists(lastFile)) {
                string saved = File.ReadAllText(lastFile).Trim();
                if (Uri.TryCreate(saved, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) {
                    startUrl = saved;
                }
            }
        } catch { }

        webView.CoreWebView2.Navigate(startUrl);
    }

    private void SaveState() {
        if (webView?.CoreWebView2 != null) {   // перевіряємо, чи встиг ініціалізуватися рушій перед закриттям
            try { File.WriteAllText(zoomFile, webView.ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }   // зберігаємо масштаб, якщо таймер не встиг спрацювати
            try {    // зберігаємо останню адресу
                string src = webView.CoreWebView2.Source ?? "";
                if (src.StartsWith("http://") || src.StartsWith("https://")) {
                    File.WriteAllText(lastFile, src); 
                }
            } catch { }
        }

        try {       // зберігаємо координати та розміри вікна незалежно від WebView2
            var state = this.WindowState == FormWindowState.Minimized ? FormWindowState.Normal : this.WindowState;
            string b = $"{normalBounds.Left},{normalBounds.Top},{normalBounds.Width},{normalBounds.Height},{(int)state}";
            File.WriteAllText(boundsFile, b);
        } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {  // при закритті додатку
        SaveState();          // записуємо стан на диск
        zoomTimer?.Stop();    // зупиняємо таймери
        zoomTimer?.Dispose(); 
        webView?.Dispose();   // миттєво вбиваємо процеси Chromium
        base.OnFormClosed(e);
    }
}
