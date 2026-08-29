using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// спеціальні класи елементів заголовка, що передають системні події миші батьківській формі (HTTRANSPARENT)
class NonClientPanel : Panel {
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

    private const int BORDER_WIDTH = 10; // ширина ділянки захоплення для зміни розміру вікна мишкою
    private Rectangle normalBounds; // збережені розміри та координати вікна до його розгортання

    // Win32 API для переміщення вікна мишкою
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    // системні повідомлення Win32 для обробки меж вікна, перетягування та наведення
    private const int WM_NCCALCSIZE   = 0x0083; // розрахунок розміру клієнтської ділянки
    private const int WM_NCHITTEST    = 0x0084; // запит про тип ділянки під вказівником миші
    private const int WM_NCMOUSELEAVE = 0x02A2; // вихід курсора за межі неклієнтської ділянки

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) {  // прибираємо стандартну білу рамку Windows, розширюючи робочу ділянку
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST) {   // обробка реакції на краї, заголовок та кнопки (вмикає меню Snap Layouts)
            int x = unchecked((short)(long)m.LParam);    // розпакування координати X (зі знаком для кількох моніторів)
            int y = unchecked((short)((long)m.LParam >> 16)); // розпакування координати Y (зі знаком для кількох моніторів)
            var screenPoint = new Point(x, y); // точка на екрані
            var clientPoint = this.PointToClient(screenPoint); // переведення екранних координат у координати форми

            if (this.WindowState == FormWindowState.Normal) {   // визначення меж для зміни розміру (лише у звичайному стані)
                if (clientPoint.Y <= BORDER_WIDTH) { // верхній край та верхні кути
                    if (clientPoint.X <= BORDER_WIDTH) { m.Result = (IntPtr)13; return; } // HTTOPLEFT: верхній лівий кут
                    if (clientPoint.X >= this.ClientSize.Width - BORDER_WIDTH) { m.Result = (IntPtr)14; return; } // HTTOPRIGHT: верхній правий кут
                    m.Result = (IntPtr)12; return; // HTTOP: верхня межа
                }
                if (clientPoint.Y >= this.ClientSize.Height - BORDER_WIDTH) { // нижній край та нижні кути
                    if (clientPoint.X <= BORDER_WIDTH) { m.Result = (IntPtr)16; return; } // HTBOTTOMLEFT: нижній лівий кут
                    if (clientPoint.X >= this.ClientSize.Width - BORDER_WIDTH) { m.Result = (IntPtr)17; return; } // HTBOTTOMRIGHT: нижній правий кут
                    m.Result = (IntPtr)15; return; // HTBOTTOM: нижня межа
                }
                if (clientPoint.X <= BORDER_WIDTH) { m.Result = (IntPtr)10; return; } // HTLEFT: ліва межа
                if (clientPoint.X >= this.ClientSize.Width - BORDER_WIDTH) { m.Result = (IntPtr)11; return; } // HTRIGHT: права межа
            }

            if (topBar != null && topBar.Bounds.Contains(clientPoint)) {   // перевірка елементів у верхній панелі
                var topPoint = topBar.PointToClient(screenPoint); // координати всередині верхньої панелі
                if (btnClose != null && btnClose.Bounds.Contains(topPoint)) { m.Result = (IntPtr)20; return; } // HTCLOSE: системна зона кнопки закриття
                if (btnMax != null && btnMax.Bounds.Contains(topPoint))     { m.Result = (IntPtr)9;  return; } // HTMAXBUTTON: вмикає меню Snap Layouts у Windows 11
                if (btnMin != null && btnMin.Bounds.Contains(topPoint))     { m.Result = (IntPtr)8;  return; } // HTMINBUTTON: системна зона кнопки згортання
                if (urlInput != null && urlInput.Bounds.Contains(topPoint)) { return; } // залишаємо обробку для поля вводу адреси

                m.Result = (IntPtr)2; return; // HTCAPTION: заголовок для нативного перетягування та подвійного кліку
            }

            m.Result = (IntPtr)1; // HTCLIENT: робоча ділянка вмісту форми (WebView2)
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

    protected override void OnResizeEnd(EventArgs e) { // збереження розмірів після завершення ручного розтягування мишкою
        base.OnResizeEnd(e);
        if (this.WindowState == FormWindowState.Normal) {
            normalBounds = this.Bounds; // оновлюємо збережені межі
        }
    }

    protected override void OnLocationChanged(EventArgs e) { // збереження нової позиції після переміщення вікна
        base.OnLocationChanged(e);
        if (this.WindowState == FormWindowState.Normal) {
            normalBounds = this.Bounds; // оновлюємо збережені координати
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

    public MinimalAiApp() {
        // налаштування параметрів головного вікна
        this.Text = "bro";
        this.Width = 1280;
        this.Height = 1280;
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
            Padding = new Padding(18, 2, 4, 2)
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
                Width = 44,
                FlatStyle = FlatStyle.Flat,
                ForeColor = System.Drawing.Color.FromArgb(180, 180, 180),
                Font = new System.Drawing.Font("Segoe UI", 9F),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0; // прибираємо рамку кнопки
            btn.FlatAppearance.MouseOverBackColor = hoverBg; // колір підсвітки при наведенні
            btn.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(hoverBg.R - 15, hoverBg.G - 15, hoverBg.B - 15); // колір при натисканні
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
            BackColor = System.Drawing.Color.FromArgb(64, 64, 64)
        };
        dragSpacer.MouseDown += DragWindow;

        urlInput = new TextBox {   // вбудований у заголовок адресний рядок
            Dock = DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(22, 22, 22),
            ForeColor = System.Drawing.Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new System.Drawing.Font("Segoe UI", 9.5F)
        };

        urlInput.KeyDown += (s, args) => {        // перехід за адресою при натисканні Enter
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
        topBar.Controls.Add(urlInput);     // рядок вводу займає весь вільний простір
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
        // прапорці оптимізації рушія Chromium:
        // --disk-cache-size=1: жорстко обмежує запис кешу на накопичувач
        // --media-cache-size=1: вимикає накопичення аудіо- та відеокешу
        // --disable-background-networking: вимикає фонові службові запити Google
        // --disable-component-update: вимикає завантаження фонових модулів Chromium
        var args = "--disk-cache-size=1 " +
                   "--media-cache-size=1 " +
                   "--disable-background-networking " +
                   "--disable-component-update";

        // профіль (авторизація, налаштування) у теці поряд із виконуваним файлом
        string userDataFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bro_profile");
        var options = new CoreWebView2EnvironmentOptions(args);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

        await webView.EnsureCoreWebView2Async(env);   // старт ізольованого середовища

        // зчитування та застосування збереженого рівня масштабування сторінки
        string zoomFile = Path.Combine(userDataFolder, "zoom.txt");
        if (File.Exists(zoomFile) && double.TryParse(File.ReadAllText(zoomFile), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double savedZoom)) {
            webView.ZoomFactor = savedZoom; // відновлюємо збережений масштаб
        }

        // автоматичне збереження масштабу при його зміні користувачем (Ctrl + коліщатко миші)
        webView.ZoomFactorChanged += (s, args) => {
            try {
                File.WriteAllText(zoomFile, webView.ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            } catch { }
        };

        var core = webView.CoreWebView2;

        // блокування аналітики та телеметрії
        core.AddWebResourceRequestedFilter("*google-analytics.com*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("*googletagmanager.com*", CoreWebView2WebResourceContext.All);
        
        // повертаємо статус 404 замість завантаження трекерів для економії ресурсів
        core.WebResourceRequested += (s, reqArgs) => {
            reqArgs.Response = core.Environment.CreateWebResourceResponse(null, 404, "Blocked", "");
        };

        core.SourceChanged += (s, args) => {   // синхронізація поточної адреси у рядку вводу при переходах за посиланнями
            urlInput.Text = core.Source;
        };

        core.Navigate("https://gemini.google.com");  // початкова сторінка за замовчуванням
    }
}
