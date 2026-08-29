using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

class MinimalAiApp : Form {
    private WebView2 webView;

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.Run(new MinimalAiApp());
    }

    public MinimalAiApp() {
        this.Text = "Gemini Minimal Workspace";
        this.Width = 1200;
        this.Height = 800;

        webView = new WebView2 { Dock = DockStyle.Fill };
        this.Controls.Add(webView);
        this.Load += InitWebView;
    }

    private async void InitWebView(object sender, EventArgs e) {
        // Забороняємо кеш на диску (захист SSD) та фонову мережеву активність
        var args = "--disk-cache-size=1 " +
                   "--media-cache-size=1 " +
                   "--disable-background-networking " +
                   "--disable-component-update";

        string userDataFolder = Path.Combine(Path.GetTempPath(), "MinimalAiApp_Profile");
        var options = new CoreWebView2EnvironmentOptions(args);
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

        await webView.EnsureCoreWebView2Async(env);

        var core = webView.CoreWebView2;

        // Блокування аналітики
        core.AddWebResourceRequestedFilter("*google-analytics.com*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("*googletagmanager.com*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, reqArgs) => {
            reqArgs.Response = core.Environment.CreateWebResourceResponse(null, 404, "Blocked", "");
        };

        // Кастомний CSS під себе
        string myCustomCss = @"
            aside { display: none !important; }
        ";

        string injectScript = $@"
            document.addEventListener('DOMContentLoaded', () => {{
                let style = document.createElement('style');
                style.innerHTML = `{myCustomCss}`;
                document.head.appendChild(style);
            }});
        ";

        await core.AddScriptToExecuteOnDocumentCreatedAsync(injectScript);
        core.Navigate("https://gemini.google.com");
    }
}
