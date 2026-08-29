using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

class MinimalAiApp : Form {
    private WebView2 webView;  // основний контейнер рендерингу Chromium
    private TextBox urlInput;  // для введення адрес
    private Panel topBar;
    private Button btnMin, btnMax, btnClose; // робимо кнопки полями класу

    // Win32 API для перетягування вікна за верхній рядок
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    // дозволяємо змінювати розміри вікна мишкою за краї (resizing) при відсутності рамки Windows
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) {  // прибираємо стандартну білу рамку Windows, зберігаючи тіні вікна та ресайз
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST) {   // обробка реакції миші на краї та кнопки (вмикає Snap Layouts)
            base.WndProc(ref m);
            var screenPoint = new System.Drawing.Point(m.LParam.ToInt32() & 0xffff, m.LParam.ToInt32() >> 16);
            var clientPoint = this.PointToClient(screenPoint);
            const int b = 240; // ширина ділянки drag мишкою (в пікселях)

            if (this.WindowState == FormWindowState.Normal) {
                if (clientPoint.Y <= b) {
                    if (clientPoint.X <= b) { m.Result = (IntPtr)13; return; } // HTTOPLEFT
                    if (clientPoint.X >= Width - b) { m.Result = (IntPtr)14; return; } // HTTOPRIGHT
                    m.Result = (IntPtr)12; return; // HTTOP
                }
                if (clientPoint.Y >= Height - b) {
                    if (clientPoint.X <= b) { m.Result = (IntPtr)16; return; } // HTBOTTOMLEFT
                    if (clientPoint.X >= Width - b) { m.Result = (IntPtr)17; return; } // HTBOTTOMRIGHT
                    m.Result = (IntPtr)15; return; // HTBOTTOM
                }
                if (clientPoint.X <= b) { m.Result = (IntPtr)10; return; } // HTLEFT
                if (clientPoint.X >= Width - b) { m.Result = (IntPtr)11; return; } // HTRIGHT
            }

            if (topBar != null && topBar.Bounds.Contains(clientPoint)) {   // перевіряємо зону заголовка
                var topPoint = topBar.PointToClient(screenPoint);
                if (btnClose != null && btnClose.Bounds.Contains(topPoint)) { m.Result = (IntPtr)20; return; } // HTCLOSE
                if (btnMax != null && btnMax.Bounds.Contains(topPoint)) { m.Result = (IntPtr)9; return; }   // HTMAXBUTTON -> Вмикає Snap Layouts!
                if (btnMin != null && btnMin.Bounds.Contains(topPoint)) { m.Result = (IntPtr)8; return; }   // HTMINBUTTON
                if (urlInput != null && urlInput.Bounds.Contains(topPoint)) { return; }

                m.Result = (IntPtr)2; return; // HTCAPTION (перетягування вікна)
            }
            return;
        }

        base.WndProc(ref m);
    }

    [STAThread]
    static void Main() {
        // передаємо керування масштабуванням від Windows безпосередньо рушію, щоб рендерити під роздільність дисплея
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MinimalAiApp());
    }

    public MinimalAiApp() {
        // налаштування параметрів головного вікна
        this.Text = "bro";
        this.Width = 1280;
        this.Height = 1280;

        webView = new WebView2 { Dock = DockStyle.Fill };  // ініціюємо WebView2 та розтягуємо його на все вікно без рамок

        this.Padding = new Padding(0);   // занулюємо системні відступи форми
        // темний колір фону для самого WebView2 (фарбує білу смугу)
        webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(20, 20, 22);
        this.BackColor = System.Drawing.Color.FromArgb(22, 22, 22);   // фарбуємо білий фон форми, що виходить за межі
        
        this.FormBorderStyle = FormBorderStyle.None;   // прибираємо стандартний громіздкий системний заголовок Windows
        var topBar = new Panel {   // панель-заголовок висотою 42px (не враховує масштабування екрану)
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = System.Drawing.Color.FromArgb(22, 22, 22),
            Padding = new Padding(8, 2, 4, 2)
        };

        void DragWindow(object? s, MouseEventArgs args) {   // функція перетягування вікна
            if (args.Button == MouseButtons.Left) {
                ReleaseCapture();
                SendMessage(this.Handle, 0xA1, 0x2, 0); // WM_NCLBUTTONDOWN + HT_CAPTION
            }
        }
        topBar.MouseDown += DragWindow;

        // фабрика сучасних кнопок у стилі Windows 11
        Button CreateSysButton(string text, EventHandler onClick, System.Drawing.Color hoverBg) {
            var btn = new Button {
                Text = text,
                Dock = DockStyle.Right,
                Width = 44,
                FlatStyle = FlatStyle.Flat,
                ForeColor = System.Drawing.Color.FromArgb(180, 180, 180),
                Font = new System.Drawing.Font("Segoe UI", 9F),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = hoverBg;
            btn.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(hoverBg.R - 15, hoverBg.G - 15, hoverBg.B - 15);
            btn.Click += onClick;
            return btn;
        }

        // кнопки з плавною підсвіткою при наведенні (червона для хрестика)
        var btnClose = CreateSysButton("✕", (s, a) => this.Close(), System.Drawing.Color.FromArgb(196, 43, 28));
        var btnMax = CreateSysButton("🗗", (s, a) => this.WindowState = (this.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized, System.Drawing.Color.FromArgb(50, 50, 55));
        var btnMin = CreateSysButton("_", (s, a) => this.WindowState = FormWindowState.Minimized, System.Drawing.Color.FromArgb(50, 50, 55));

        btnMin.FlatAppearance.BorderSize = 0;
        btnMin.Click += (s, a) => this.WindowState = FormWindowState.Minimized;

        var dragSpacer = new Panel {  // 120px між рядком і кнопками для хапання і перетягування вікна
            Dock = DockStyle.Right,
            Width = 80,
            BackColor = System.Drawing.Color.Transparent
        };
        dragSpacer.MouseDown += DragWindow;

        urlInput = new TextBox {   // вбудований у Title Bar адресний рядок
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(22, 22, 22),
            ForeColor = System.Drawing.Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.None,
            Font = new System.Drawing.Font("Segoe UI", 9.5F)
        };

        urlInput.KeyDown += (s, args) => {        // перехід за адресою при натисканні Enter
            if (args.KeyCode == Keys.Enter) {
                args.SuppressKeyPress = true;
                string target = urlInput.Text.Trim();
                if (!target.StartsWith("http://") && !target.StartsWith("https://")) {
                    target = "https://" + target;
                }
                webView.CoreWebView2?.Navigate(target);
            }
        };

        // порядок елементів (Docking зліва направо):
        topBar.Controls.Add(urlInput);     // рядок вводу заповнює весь залишок
        topBar.Controls.Add(dragSpacer);   // порожня зона для хвата мишкою
        topBar.Controls.Add(btnMin);      // кнопка згортання в трей
        topBar.Controls.Add(btnMax);
        topBar.Controls.Add(btnClose);

        this.Controls.Add(webView);
        this.Controls.Add(topBar); // панель вгорі

        // підписка на подію старту вікна (з додаванням знаку '?' для зняття попередження Nullable)
        this.Load += InitWebView;
    }

    // попередження CS8622 усунено за допомогою `object?` (дозволяємо null відповідно до сигнатури EventHandler)
    private async void InitWebView(object? sender, EventArgs e) {
        // прапорці оптимізації хроміума:
        // --disk-cache-size=1: жорстко обмежує кешування на диск (захист SSD від перезапису)
        // --media-cache-size=1: вимикає накопичення аудіо/відео кешу
        // --disable-background-networking: вимикає фонові службові запити Google у мережі
        // --disable-component-update: вимикає завантаження фонових модулів Chromium
        var args = "--disk-cache-size=1 " +
                   "--media-cache-size=1 " +
                   "--disable-background-networking " +
                   "--disable-component-update";

        // профіль (Zoom, авторизація, налаштування) у папці поряд із bro.exe, а не в системних папках AppData
        string userDataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bro_profile");
        var options = new CoreWebView2EnvironmentOptions(args);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

        await webView.EnsureCoreWebView2Async(env);   // старт ізольованого середовища

        var core = webView.CoreWebView2;

        // блокуємо телеметрію. створюємо фільтри мережевих запитів
        core.AddWebResourceRequestedFilter("*google-analytics.com*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("*googletagmanager.com*", CoreWebView2WebResourceContext.All);
        
        // повертаємо 404 замість завантаження метрик, заощаджуючи RAM
        core.WebResourceRequested += (s, reqArgs) => {
            reqArgs.Response = core.Environment.CreateWebResourceResponse(null, 404, "Blocked", "");
        };

        core.SourceChanged += (s, args) => {   // синхронізація адреси у рядку при кліках по посиланнях
            urlInput.Text = core.Source;
        };

        core.Navigate("https://gemini.google.com");  // відкриваємо Gemini по-дефолту
    }
}
