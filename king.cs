using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

class MinimalAiApp : Form {
    private WebView2 webView;  // основний контейнер рендерингу Chromium
    private TextBox urlInput;  // для введення адрес

    // Win32 API для перетягування вікна за верхній рядок
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    // дозволяємо змінювати розміри вікна мишкою за краї (resizing) при відсутності рамки Windows
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.Style |= 0x40000; // WS_THICKFRAME (зони зміни розміру вікна)
            return cp;
        }
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
        var btnMin = CreateSysButton("—", (s, a) => this.WindowState = FormWindowState.Minimized, System.Drawing.Color.FromArgb(50, 50, 55));

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

        // профіль у тимчасовій оперативній папці Temp, а не в системних папках AppData
        string userDataFolder = Path.Combine(Path.GetTempPath(), "MinimalAiApp_Profile");
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

        // синхронізація адреси у рядку при кліках по посиланнях
        core.SourceChanged += (s, args) => {
            urlInput.Text = core.Source;
        };

        // кастомний CSS
        string myCustomCss = @"
            /* гнучка ширина: не менше 800px, відносно 85% екрана, але не більше 1200px */
            .conversation-container, 
            main .main-content, 
            .response-container-content,
            message-content {
                width: clamp(800px, 85vw, 1200px) !important;
                max-width: 95% !important;
            }

            /* блоки коду: робимо їх гнучкими + дозволяємо змінювати розмір вручну мишкою */
            code-block, pre {
                width: 100% !important;
                resize: horizontal !important; /* можна потягнути за куток коду і розширити */
                overflow: auto !important;
            }
        ";

        // JS-скрипт, який буде вбудовуватися у сторінку в момент створення DOM-дерева
        string injectScript = $@"
            document.addEventListener('DOMContentLoaded', () => {{
                let style = document.createElement('style');
                style.innerHTML = `{myCustomCss}`;
                document.head.appendChild(style);
            }});
        ";

        // автоматично ін'єктуємо CSS при кожному переході чи оновленні сторінки
        await core.AddScriptToExecuteOnDocumentCreatedAsync(injectScript);

        // відкриваємо Gemini
        core.Navigate("https://gemini.google.com");
    }
}
