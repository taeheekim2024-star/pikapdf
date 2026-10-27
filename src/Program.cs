// PDF 뷰어 - Firefox의 PDF 엔진(PDF.js)을 Windows WebView2 창에 올린 단독 실행 프로그램.
// 창 하나에 탭 여러 개(크롬식 헤더), 프로그램은 한 번만 실행(두 번째 실행은 열린 창에 탭으로 전달).
// 도구: 페이지 회전 저장(PDF.js 패치), 용량 줄이기·잠금 해제(PDF24의 Ghostscript),
//       이미지 추출·PDF→이미지/텍스트(PDF.js), PDF→Word·오피스/한글→PDF(Office·한컴 COM)
// 빌드: src\build.ps1 (Windows에 기본 포함된 .NET Framework C# 컴파일러 사용, C# 5 문법만 사용)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PdfViewerApp
{
    static class Program
    {
        public static int ExitCode;
        const string MutexNameBase = "Local\\PdfViewer.TaeheeKim.SingleInstance";
        const string PipeNameBase = "PdfViewer.TaeheeKim.Pipe";
        // 자체 시험은 실제로 떠 있는 뷰어 창과 섞이지 않도록 별도 이름을 쓴다
        public static string PipeName = PipeNameBase;
        static Mutex instanceMutex;

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int dwProcessId);

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 실행 중에 새 버전을 설치하면 옛 실행 파일이 PdfViewer.old.exe 로 남는다 → 다음 시작 때 치운다
            try
            {
                string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PdfViewer.old.exe");
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }

            var files = new List<string>();
            string selftestDir = null;
            bool newWindow = false;
            bool testClient = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--selftest" && i + 1 < args.Length) selftestDir = Path.GetFullPath(args[++i]);
                else if (args[i] == "--new-window") newWindow = true;
                else if (args[i] == "--selftest-client") testClient = true;
                else if (!string.IsNullOrWhiteSpace(args[i]))
                {
                    try { files.Add(Path.GetFullPath(args[i])); } catch { files.Add(args[i]); }
                }
            }
            string mutexName = MutexNameBase;
            if (selftestDir != null || testClient) { mutexName += ".selftest"; PipeName = PipeNameBase + ".selftest"; }

            bool primary = true;
            if (!newWindow)
            {
                bool created;
                instanceMutex = new Mutex(true, mutexName, out created);
                primary = created;
                if (!created && selftestDir == null)
                {
                    AllowSetForegroundWindow(-1);
                    if (ForwardToRunning(files)) return 0;
                    // 전달에 실패하면(드물게) 그냥 새 창으로 연다
                }
            }

            try
            {
                Application.Run(new MainForm(files, selftestDir, primary));
            }
            catch (Exception ex)
            {
                Log("FATAL " + ex);
                MessageBox.Show("프로그램 오류: " + ex.Message, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return ExitCode;
        }

        static bool ForwardToRunning(List<string> files)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(3000);
                    var bytes = Encoding.UTF8.GetBytes(string.Join("\n", files));
                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("forward failed: " + ex.Message);
                return false;
            }
        }

        static readonly object logLock = new object();
        public static void Log(string msg)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(MainForm.DataDir);
                    File.AppendAllText(Path.Combine(MainForm.DataDir, "log.txt"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ===================================================================================
    //  창: 헤더(탭·메뉴) + 문서 영역, 각 탭이 DocTab(WebView2 하나 = 문서 하나)
    // ===================================================================================
    class MainForm : Form
    {
        public const string AppName = "PDF 뷰어";
        public const string AppVersion = "1.2";
        public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF뷰어");

        readonly List<string> startFiles;
        public readonly string SelftestDir;
        readonly bool primary;
        readonly HeaderBar header;
        readonly Panel content;
        readonly List<DocTab> docs = new List<DocTab>();
        int activeIndex = -1;
        CoreWebView2Environment env;
        Task<CoreWebView2Environment> envTask;
        Label loadingLabel;
        readonly DateTime processStart = Process.GetCurrentProcess().StartTime;

        public long SinceStartMs { get { return (long)(DateTime.Now - processStart).TotalMilliseconds; } }

        void CenterLoading()
        {
            if (loadingLabel == null) return;
            loadingLabel.Location = new Point((content.ClientSize.Width - loadingLabel.Width) / 2, (content.ClientSize.Height - loadingLabel.Height) / 2 - Scale(20));
        }

        public void HideLoading()
        {
            if (loadingLabel != null && loadingLabel.Visible)
            {
                loadingLabel.Visible = false;
                SelftestLog("FIRST PAGE SHOWN at +" + SinceStartMs + " ms");
            }
        }
        string injectScript;
        bool closingAll;
        bool forceClose;
        volatile bool pipeStop;
        int selftestPhase;

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        public MainForm(List<string> files, string selftestDir, bool primary)
        {
            startFiles = files;
            SelftestDir = selftestDir;
            this.primary = primary;
            Text = AppName;
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.Manual;
            SetupBounds();

            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Color.White;
            content.Margin = new Padding(0);
            Controls.Add(content);

            // 브라우저 엔진이 뜰 때까지 보여 줄 안내 (첫 화면이 나오면 숨김)
            loadingLabel = new Label();
            loadingLabel.Text = "불러오는 중…";
            loadingLabel.Font = new Font("Malgun Gothic", 11f);
            loadingLabel.ForeColor = Color.FromArgb(0x8A, 0x91, 0x9C);
            loadingLabel.AutoSize = true;
            loadingLabel.BackColor = Color.White;
            content.Controls.Add(loadingLabel);
            content.Resize += delegate { CenterLoading(); };
            CenterLoading();

            // 브라우저 엔진 준비는 창이 뜨기 전부터 시작해 둔다
            try
            {
                var opts = new CoreWebView2EnvironmentOptions();
                opts.Language = "ko-KR";
                Directory.CreateDirectory(DataDir);
                envTask = CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), opts);
            }
            catch (Exception ex) { Program.Log("env start failed: " + ex.Message); }

            header = new HeaderBar(this);
            header.Dock = DockStyle.Top;
            header.SetMenus(new[] { "파일", "도구", "도움말" }, new[] { BuildFileMenu(), BuildToolsMenu(), BuildHelpMenu() });
            Controls.Add(header);
            content.BringToFront();

            Load += OnLoadAsync;
        }

        // 배율: WinForms의 DeviceDpi는 app.config 없이는 96에 머무르므로 창의 실제 DPI를 직접 읽는다 (175% 화면이면 168)
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();
        int dpiCache;

        public int Dpi
        {
            get
            {
                if (dpiCache <= 0)
                {
                    try { dpiCache = (int)(IsHandleCreated ? GetDpiForWindow(Handle) : GetDpiForSystem()); } catch { }
                    if (dpiCache <= 0)
                    {
                        try { using (var g = CreateGraphics()) dpiCache = (int)g.DpiX; } catch { }
                    }
                    if (dpiCache <= 0) dpiCache = 96;
                }
                return dpiCache;
            }
        }

        public int Scale(int px) { return (int)Math.Round(px * Dpi / 96.0); }
        public IList<DocTab> Docs { get { return docs; } }
        public int ActiveIndex { get { return activeIndex; } }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x02E0) // WM_DPICHANGED: 다른 배율의 모니터로 옮겨짐
            {
                dpiCache = 0;
                if (header != null) header.RefreshSize();
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return SelftestDir != null; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Chrome.ApplyCaption(this, HeaderBar.Brand, Color.White);
        }

        public void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        // ---------- 메뉴 ----------

        static ToolStripMenuItem MenuItemOf(string text, string shortcut, EventHandler onClick)
        {
            var mi = new ToolStripMenuItem(text);
            if (!string.IsNullOrEmpty(shortcut)) mi.ShortcutKeyDisplayString = shortcut;
            mi.Click += onClick;
            return mi;
        }

        static ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip();
            m.Font = new Font("Malgun Gothic", 9.5f);
            m.ShowImageMargin = false;
            m.Renderer = new MenuRenderer();
            return m;
        }

        ContextMenuStrip BuildFileMenu()
        {
            var m = NewMenu();
            m.Items.Add(MenuItemOf("열기...", "Ctrl+O", delegate { OpenDialog(); }));
            m.Items.Add(MenuItemOf("저장", "Ctrl+S", delegate { var d = ActiveDoc; if (d != null) d.RequestSave(); }));
            m.Items.Add(MenuItemOf("다른 이름으로 저장...", "Ctrl+Shift+S", delegate { var d = ActiveDoc; if (d != null) d.PostCommand("saveas"); }));
            m.Items.Add(MenuItemOf("인쇄...", "Ctrl+P", delegate { var d = ActiveDoc; if (d != null) d.PostCommand("print"); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(MenuItemOf("탭 닫기", "Ctrl+W", delegate { CloseTab(ActiveDoc); }));
            m.Items.Add(MenuItemOf("끝내기", null, delegate { Close(); }));
            return m;
        }

        ContextMenuStrip BuildToolsMenu()
        {
            var m = NewMenu();
            m.Items.Add(MenuItemOf("페이지 회전 저장...", null, delegate { ToolRotate(); }));
            m.Items.Add(MenuItemOf("용량 줄이기...", null, delegate { ToolCompress(); }));
            m.Items.Add(MenuItemOf("잠금(암호) 해제...", null, delegate { ToolUnlock(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(MenuItemOf("문서 안의 이미지 추출...", null, delegate { ToolExtractImages(); }));
            m.Items.Add(MenuItemOf("PDF → 이미지(PNG/JPG)...", null, delegate { ToolPagesToImages(); }));
            m.Items.Add(MenuItemOf("PDF → 텍스트...", null, delegate { ToolExtractText(); }));
            m.Items.Add(MenuItemOf("PDF → Word(docx)...", null, delegate { ToolPdfToWord(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(MenuItemOf("오피스·한글 문서 → PDF...", null, delegate { ToolOfficeToPdf(); }));
            return m;
        }

        ContextMenuStrip BuildHelpMenu()
        {
            var m = NewMenu();
            m.Items.Add(MenuItemOf("사용법 (README)", null, delegate { OpenReadme(); }));
            var notice = new ToolStripMenuItem("시작할 때 프로젝트 공지 받기");
            notice.CheckOnClick = true;
            notice.Checked = Notice.Enabled;
            notice.ToolTipText = "프로젝트 사이트의 notice.json 한 개만 읽습니다 (계정·추적 없음)";
            notice.CheckedChanged += delegate { Notice.Enabled = notice.Checked; };
            m.Items.Add(notice);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(MenuItemOf("프로젝트 페이지 (GitHub)", null, delegate { Util.OpenExternal(Notice.ProjectUrl); }));
            m.Items.Add(MenuItemOf("정보", null, delegate { ShowAbout(); }));
            return m;
        }

        void OpenReadme()
        {
            string p = Path.Combine(BaseDir, "README.txt");
            if (!File.Exists(p)) { Info("README.txt 가 없습니다"); return; }
            try { var psi = new ProcessStartInfo(p); psi.UseShellExecute = true; Process.Start(psi); }
            catch (Exception ex) { Error(ex.Message); }
        }

        void ShowAbout()
        {
            string wv = "";
            try { if (env != null) wv = env.BrowserVersionString; } catch { }
            Info(AppName + " " + AppVersion + "\n\n" +
                 "PDF 엔진: PDF.js 6.4.299 (Mozilla, Apache-2.0)\n" +
                 "화면: Microsoft WebView2 " + wv + "\n" +
                 "용량 줄이기·잠금 해제: PDF24에 포함된 Ghostscript" + (Tools.FindGs() != null ? "" : " (지금은 없음)") + "\n" +
                 "변환: Microsoft Office, 한컴오피스 (설치된 경우)\n\n" +
                 "라이선스: GPLv3 (WebView2 연결 예외 포함). 이름·아이콘은 제작자의 표장이며 파생작은 출처 표기를 유지해야 합니다 (NOTICE, TRADEMARK.md).\n" +
                 "공식 배포: " + Notice.ProjectUrl + "\n" +
                 "시작할 때 프로젝트 사이트의 notice.json 하나를 읽어 공지를 보여 줄 수 있습니다 (도움말 메뉴에서 끄기).\n\n" +
                 "폴더: " + BaseDir + "\n설정·백업: " + DataDir);
        }

        // ---------- 창 위치 ----------

        void SetupBounds()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (SelftestDir != null)
            {
                // 자체 시험: 창의 오른쪽 아래 3픽셀만 화면 모서리에 걸치게 두어 사용자를 방해하지 않게 함
                ShowInTaskbar = false;
                TopMost = true;
                Bounds = new Rectangle(wa.Left - 1400 + 3, wa.Top - 900 + 3, 1400, 900);
                return;
            }
            int w = (int)(wa.Width * 0.85), h = (int)(wa.Height * 0.9);
            var b = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
            bool max = false;
            try
            {
                string p = Path.Combine(DataDir, "window.txt");
                if (File.Exists(p))
                {
                    var parts = File.ReadAllText(p).Split(',');
                    if (parts.Length >= 5)
                    {
                        var r = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                        if (r.Width >= 400 && r.Height >= 300 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) b = r;
                        max = parts[4] == "1";
                    }
                }
            }
            catch { }
            Bounds = b;
            if (max) WindowState = FormWindowState.Maximized;
        }

        void SaveWindowState()
        {
            if (SelftestDir != null) return;
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(Path.Combine(DataDir, "window.txt"),
                    string.Format("{0},{1},{2},{3},{4}", r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized ? 1 : 0));
            }
            catch { }
        }

        // ---------- 시작 ----------

        async void OnLoadAsync(object sender, EventArgs e)
        {
            try
            {
                SelftestLog("LOAD at +" + SinceStartMs + " ms");
                Directory.CreateDirectory(DataDir);
                if (envTask == null)
                {
                    var opts = new CoreWebView2EnvironmentOptions();
                    opts.Language = "ko-KR";
                    envTask = CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), opts);
                }
                env = await envTask;
                SelftestLog("ENV ready at +" + SinceStartMs + " ms");
                injectScript = File.ReadAllText(Path.Combine(BaseDir, "inject.js"), Encoding.UTF8);

                if (SelftestDir != null)
                {
                    Directory.CreateDirectory(SelftestDir);
                    var watchdog = new System.Windows.Forms.Timer();
                    watchdog.Interval = 240000;
                    watchdog.Tick += delegate { SelftestFail("timeout"); };
                    watchdog.Start();
                }
                if (primary)
                {
                    var t = new Thread(PipeLoop);
                    t.IsBackground = true;
                    t.Start();
                }
                // 프로젝트 공지는 미리 받아 둔다 (시작 화면이 열리면 바로 쓰도록)
                var noticeTask = Notice.Fetch(SelftestDir);

                var files = new List<string>();
                foreach (var f in startFiles)
                {
                    if (File.Exists(f)) files.Add(f);
                    else MessageBox.Show(this, "파일을 찾을 수 없습니다:\n" + f, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                // 자체 시험: 첫 파일로 1차 시험, 둘째 파일은 두 번째 인스턴스를 거쳐 탭으로 들어오는지 본다
                if (SelftestDir != null && files.Count > 1) files = files.Take(1).ToList();
                if (files.Count == 0) await NewTab(null);
                else OpenPaths(files);
            }
            catch (Exception ex)
            {
                Program.Log("Init failed: " + ex);
                if (SelftestDir != null) SelftestLog("INIT FAILED " + ex);
                MessageBox.Show(this,
                    "뷰어를 시작하지 못했습니다.\n\n" + ex.Message + "\n\nWebView2 런타임(Microsoft Edge)이 설치되어 있어야 합니다.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Program.ExitCode = 2;
                forceClose = true;
                Close();
            }
        }

        // 두 번째로 실행된 프로그램이 파일 경로를 보내오면 탭으로 연다
        void PipeLoop()
        {
            while (!pipeStop)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        string text;
                        using (var ms = new MemoryStream())
                        {
                            server.CopyTo(ms);
                            text = Encoding.UTF8.GetString(ms.ToArray());
                        }
                        var files = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                        BeginInvoke((Action)delegate
                        {
                            BringToFrontNow();
                            if (files.Count > 0) OpenPaths(files);
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (!pipeStop) Program.Log("pipe: " + ex.Message);
                    Thread.Sleep(300);
                }
            }
        }

        void BringToFrontNow()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                SetForegroundWindow(Handle);
            }
            catch { }
        }

        // ---------- 탭 관리 ----------

        public DocTab ActiveDoc
        {
            get { return (activeIndex >= 0 && activeIndex < docs.Count) ? docs[activeIndex] : null; }
        }

        public void SelectTab(DocTab doc)
        {
            int i = docs.IndexOf(doc);
            if (i < 0) return;
            activeIndex = i;
            foreach (var d in docs) d.Web.Visible = (d == doc);
            doc.Focus();
            UpdateTitle();
        }

        async Task<DocTab> NewTab(string path)
        {
            var doc = new DocTab(this);
            doc.Passive = SelftestDir != null && selftestPhase == 1;
            docs.Add(doc);
            content.Controls.Add(doc.Web);
            SelectTab(doc);
            await doc.InitAsync(env, injectScript, path);
            return doc;
        }

        public async void OpenPaths(List<string> paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!File.Exists(p))
                {
                    MessageBox.Show(this, "파일을 찾을 수 없습니다:\n" + p, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                // 이미 열려 있으면 그 탭으로
                var open = docs.FirstOrDefault(d => d.DocPath != null && string.Equals(d.DocPath, p, StringComparison.OrdinalIgnoreCase));
                if (open != null) { SelectTab(open); continue; }
                // 빈 탭(현재 탭 우선)이 있으면 거기에, 아니면 새 탭
                var active = ActiveDoc;
                var empty = (active != null && active.IsEmpty) ? active : docs.FirstOrDefault(d => d.IsEmpty);
                if (empty != null && empty.Core != null)
                {
                    SelectTab(empty);
                    empty.NavigateTo(p);
                }
                else
                {
                    await NewTab(p);
                }
            }
            UpdateTitle();
        }

        public void OpenDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "PDF 열기";
                dlg.Filter = "PDF 파일 (*.pdf)|*.pdf|모든 파일 (*.*)|*.*";
                dlg.Multiselect = true;
                var active = ActiveDoc;
                if (active != null && !string.IsNullOrEmpty(active.DocPath))
                {
                    try { dlg.InitialDirectory = Path.GetDirectoryName(active.DocPath); } catch { }
                }
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenPaths(dlg.FileNames.ToList());
            }
        }

        public void CycleTab(int delta)
        {
            if (docs.Count < 2 || activeIndex < 0) return;
            SelectTab(docs[(activeIndex + delta + docs.Count) % docs.Count]);
        }

        // 탭 닫기: 저장 안 된 변경이 있으면 묻고, "예"면 저장이 끝난 뒤 닫는다
        public void CloseTab(DocTab doc)
        {
            if (doc == null || !docs.Contains(doc)) return;
            if (doc.Dirty && !doc.ForceClose)
            {
                SelectTab(doc);
                var r = MessageBox.Show(this, doc.DisplayName + "\n\n저장하지 않은 변경 사항이 있습니다. 저장할까요?", AppName,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { closingAll = false; return; }
                if (r == DialogResult.Yes)
                {
                    doc.CloseAfterSave = true;
                    doc.RequestSave();
                    return; // 저장이 끝나면 DocTab이 RemoveTab을 부른다
                }
            }
            RemoveTab(doc);
        }

        public void CancelClosing()
        {
            closingAll = false;
        }

        public void RemoveTab(DocTab doc)
        {
            int idx = docs.IndexOf(doc);
            if (idx < 0) return;
            docs.RemoveAt(idx);
            content.Controls.Remove(doc.Web);
            doc.Dispose();
            if (closingAll)
            {
                var next = docs.FirstOrDefault(d => d.Dirty);
                if (next == null) { forceClose = true; Close(); }
                else CloseTab(next);
                return;
            }
            if (docs.Count == 0) { forceClose = true; Close(); return; }
            int newIndex = activeIndex;
            if (idx < activeIndex) newIndex = activeIndex - 1;
            else if (idx == activeIndex) newIndex = Math.Min(idx, docs.Count - 1);
            SelectTab(docs[Math.Max(0, Math.Min(newIndex, docs.Count - 1))]);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!forceClose)
            {
                var dirtyTab = docs.FirstOrDefault(d => d.Dirty);
                if (dirtyTab != null)
                {
                    e.Cancel = true;
                    closingAll = true;
                    CloseTab(dirtyTab);
                    return;
                }
            }
            pipeStop = true;
            SaveWindowState();
            base.OnFormClosing(e);
        }

        public void ForceCloseWindow()
        {
            forceClose = true;
            Close();
        }

        // ---------- 제목 ----------

        public void UpdateTitle()
        {
            var doc = ActiveDoc;
            Text = doc == null ? AppName : doc.TabTitle + " - " + AppName;
            header.Invalidate();
        }

        // ---------- 메시지 상자 (자체 시험 중에는 기록만) ----------

        public void Info(string msg)
        {
            if (SelftestDir != null) { SelftestLog("INFO " + msg.Replace("\n", " / ")); return; }
            MessageBox.Show(this, msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void Error(string msg)
        {
            Program.Log("error: " + msg.Replace("\n", " / "));
            if (SelftestDir != null) { SelftestLog("ERROR " + msg.Replace("\n", " / ")); return; }
            MessageBox.Show(this, msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        bool Ask(string msg)
        {
            if (SelftestDir != null) { SelftestLog("ASK " + msg.Replace("\n", " / ")); return false; }
            return MessageBox.Show(this, msg, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        DocTab RequireDoc()
        {
            var doc = ActiveDoc;
            if (doc == null || string.IsNullOrEmpty(doc.DocPath) || doc.Core == null)
            {
                Info("먼저 PDF 문서를 열어 주세요.");
                return null;
            }
            return doc;
        }

        string AskSavePath(string basePath, string suffix, string ext, string filter, string title)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = title;
                dlg.Filter = filter;
                dlg.DefaultExt = ext.TrimStart('.');
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                dlg.FileName = Path.GetFileNameWithoutExtension(basePath) + suffix + ext;
                try { dlg.InitialDirectory = Path.GetDirectoryName(basePath); } catch { }
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
            }
        }

        // 문서 옆에 "<이름>_<접미>" 폴더를 만든다 (안 되면 문서 폴더에)
        string MakeExportDir(string docPath, string suffix)
        {
            string name = Path.GetFileNameWithoutExtension(docPath) + suffix;
            string dir = Path.Combine(Path.GetDirectoryName(docPath), name);
            try { Directory.CreateDirectory(dir); return dir; }
            catch { }
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDF뷰어 내보내기", name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        void ShowExportResult(int count, string folder, string what)
        {
            if (count < 0) { Info("취소했습니다."); return; }
            if (count == 0) { Info(what + " 없습니다."); return; }
            if (Ask(string.Format("{0} {1}개를 저장했습니다.\n{2}\n\n폴더를 열까요?", what, count, folder)))
            {
                try { Process.Start("explorer.exe", "\"" + folder + "\""); } catch { }
            }
        }

        // ---------- 긴 작업: 진행 창 ----------

        async Task<T> RunWithProgress<T>(string title, string message, bool cancellable, Func<CancellationToken, Task<T>> work)
        {
            if (SelftestDir != null) return await work(CancellationToken.None);
            var pf = new ProgressForm(title, message, cancellable);
            Task<T> task = null;
            pf.Shown += async delegate
            {
                try { task = work(pf.Cts.Token); await task; }
                catch { }
                pf.Finish();
            };
            pf.ShowDialog(this);
            if (task == null) throw new Exception("작업을 시작하지 못했습니다");
            return await task;
        }

        async Task RunWithProgressVoid(string title, string message, bool cancellable, Func<CancellationToken, Task> work)
        {
            await RunWithProgress<bool>(title, message, cancellable, async delegate(CancellationToken ct) { await work(ct); return true; });
        }

        // 뷰어(JS)가 하는 내보내기: 진행 메시지와 완료 신호를 메시지로 받는다
        async Task<int> RunJsExport(DocTab doc, string title, string message, string command)
        {
            var tcs = new TaskCompletionSource<int>();
            doc.ExportTcs = tcs;
            if (SelftestDir != null)
            {
                doc.PostCommand(command);
                int n = await WithTimeout(tcs.Task, 180000);
                doc.ExportTcs = null;
                return n;
            }
            var pf = new ProgressForm(title, message, true);
            doc.ExportProgress = pf;
            pf.Cts.Token.Register(delegate { try { doc.PostCommand("cancel"); } catch { } });
            pf.Shown += delegate { doc.PostCommand(command); };
            var ignored = tcs.Task.ContinueWith(delegate(Task<int> t) { pf.Finish(); });
            pf.ShowDialog(this);
            doc.ExportProgress = null;
            doc.ExportTcs = null;
            if (!tcs.Task.IsCompleted) return -1; // 취소
            return await tcs.Task;
        }

        public static async Task<T> WithTimeout<T>(Task<T> task, int ms)
        {
            var done = await Task.WhenAny(task, Task.Delay(ms));
            if (done != task) throw new TimeoutException("시간 초과 (" + (ms / 1000) + "초)");
            return await task;
        }

        // ---------- 도구 ----------

        async void ToolRotate()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            string info;
            try { info = await doc.GetPageInfoAsync(); }
            catch (Exception ex) { Error(ex.Message); return; }
            var parts = info.Split('|');
            if (parts.Length < 3) { Error("문서 정보를 읽지 못했습니다"); return; }
            int page = int.Parse(parts[0]), count = int.Parse(parts[1]);
            if (parts[2] == "1")
            {
                Info("페이지 삭제·순서 변경이 아직 저장되지 않았습니다.\n먼저 Ctrl+S 로 저장한 뒤 회전해 주세요.");
                return;
            }
            List<int> pages;
            int delta;
            using (var dlg = new RotateDialog(page, count))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                pages = dlg.Pages;
                delta = dlg.Delta;
            }
            doc.Rotate(pages, delta);
        }

        async void ToolCompress()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            string gs = Tools.FindGs();
            if (gs == null) { Error("용량 줄이기에는 PDF24 Creator에 포함된 Ghostscript가 필요합니다.\n(C:\\Program Files\\PDF24\\gs 가 없습니다)"); return; }
            if (doc.Dirty) { Info("저장하지 않은 변경이 있습니다. 먼저 Ctrl+S 로 저장해 주세요."); return; }
            string preset;
            bool overwrite;
            using (var dlg = new CompressDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                preset = dlg.Preset;
                overwrite = dlg.Overwrite;
            }
            string outPath = overwrite ? doc.DocPath : AskSavePath(doc.DocPath, "_압축", ".pdf", "PDF 파일 (*.pdf)|*.pdf", "줄인 파일 저장");
            if (outPath == null) return;
            long before = new FileInfo(doc.DocPath).Length;
            string tmp = null;
            try
            {
                string src = doc.DocPath;
                tmp = await RunWithProgress("용량 줄이기", "Ghostscript로 다시 쓰는 중…", true,
                    ct => Tools.GsCompressAsync(gs, src, preset, ct));
                if (overwrite) Util.WriteWithBackup(outPath, File.ReadAllBytes(tmp));
                else File.Copy(tmp, outPath, true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Error("용량 줄이기 실패:\n" + ex.Message); return; }
            finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
            long after = new FileInfo(outPath).Length;
            Info(string.Format("{0} → {1} ({2})\n{3}", Tools.FmtSize(before), Tools.FmtSize(after),
                after < before ? string.Format("{0}% 감소", (int)Math.Round(100.0 * (before - after) / before)) : "줄어들지 않음", outPath));
            if (overwrite) doc.NavigateTo(outPath);
            else OpenPaths(new List<string> { outPath });
        }

        async void ToolUnlock()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            string gs = Tools.FindGs();
            if (gs == null) { Error("잠금 해제에는 PDF24 Creator에 포함된 Ghostscript가 필요합니다.\n(C:\\Program Files\\PDF24\\gs 가 없습니다)"); return; }
            string password;
            using (var dlg = new PasswordDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                password = dlg.Password;
            }
            string outPath = AskSavePath(doc.DocPath, "_잠금해제", ".pdf", "PDF 파일 (*.pdf)|*.pdf", "잠금 해제한 파일 저장");
            if (outPath == null) return;
            string tmp = null;
            try
            {
                string src = doc.DocPath;
                tmp = await RunWithProgress("잠금 해제", "Ghostscript로 다시 쓰는 중…", true,
                    ct => Tools.GsUnlockAsync(gs, src, password, ct));
                File.Copy(tmp, outPath, true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Error("잠금 해제 실패:\n" + ex.Message); return; }
            finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
            Info("잠금을 해제한 파일을 저장했습니다.\n" + outPath);
            OpenPaths(new List<string> { outPath });
        }

        async void ToolExtractImages()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            string dir;
            try { dir = MakeExportDir(doc.DocPath, "_추출이미지"); }
            catch (Exception ex) { Error(ex.Message); return; }
            doc.ExportDir = dir;
            int n;
            try { n = await RunJsExport(doc, "이미지 추출", "문서 안의 그림을 찾는 중…", "extract"); }
            catch (Exception ex) { Error("이미지 추출 실패:\n" + ex.Message); return; }
            ShowExportResult(n, dir, "추출한 이미지");
        }

        async void ToolPagesToImages()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            string info;
            try { info = await doc.GetPageInfoAsync(); }
            catch (Exception ex) { Error(ex.Message); return; }
            var parts = info.Split('|');
            int page = int.Parse(parts[0]), count = int.Parse(parts[1]);
            string json;
            using (var dlg = new PagesToImagesDialog(page, count))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                json = dlg.ToJson();
            }
            string dir;
            try { dir = MakeExportDir(doc.DocPath, "_페이지이미지"); }
            catch (Exception ex) { Error(ex.Message); return; }
            doc.ExportDir = dir;
            int n;
            try { n = await RunJsExport(doc, "PDF → 이미지", "페이지를 그리는 중…", "pages2img|" + json); }
            catch (Exception ex) { Error("변환 실패:\n" + ex.Message); return; }
            ShowExportResult(n, dir, "페이지 이미지");
        }

        async void ToolExtractText()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            string outPath = AskSavePath(doc.DocPath, "", ".txt", "텍스트 파일 (*.txt)|*.txt", "텍스트 저장");
            if (outPath == null) return;
            doc.ExportTextPath = outPath;
            int n;
            try { n = await RunJsExport(doc, "PDF → 텍스트", "글자를 뽑는 중…", "text"); }
            catch (Exception ex) { Error("텍스트 추출 실패:\n" + ex.Message); return; }
            if (n < 0) return;
            if (Ask(n + "쪽의 글자를 저장했습니다.\n" + outPath + "\n\n열어 볼까요?"))
            {
                try { var psi = new ProcessStartInfo(outPath); psi.UseShellExecute = true; Process.Start(psi); } catch { }
            }
        }

        async void ToolPdfToWord()
        {
            var doc = RequireDoc();
            if (doc == null) return;
            if (!Office.IsAvailable("Word.Application")) { Error("Microsoft Word가 설치되어 있어야 합니다."); return; }
            string outPath = AskSavePath(doc.DocPath, "", ".docx", "Word 문서 (*.docx)|*.docx", "Word 문서로 저장");
            if (outPath == null) return;
            try
            {
                string src = doc.DocPath;
                await RunWithProgressVoid("PDF → Word", "Word가 변환하는 중… (문서가 길면 몇 분 걸릴 수 있습니다)", false,
                    ct => Tools.PdfToWordAsync(src, outPath));
            }
            catch (Exception ex) { Error("변환 실패:\n" + ex.Message); return; }
            if (Ask("Word 문서로 저장했습니다.\n" + outPath + "\n\nWord로 열까요?"))
            {
                try { var psi = new ProcessStartInfo(outPath); psi.UseShellExecute = true; Process.Start(psi); } catch { }
            }
        }

        public async void ToolOfficeToPdf()
        {
            List<string> files;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "PDF로 바꿀 문서 선택";
                dlg.Filter = "오피스·한글 문서|*.doc;*.docx;*.docm;*.rtf;*.odt;*.txt;*.xls;*.xlsx;*.xlsm;*.csv;*.ods;*.ppt;*.pptx;*.pptm;*.odp;*.hwp;*.hwpx|모든 파일 (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                files = dlg.FileNames.ToList();
            }
            var results = new List<string>();
            foreach (var src in files)
            {
                string app = Office.AppFor(src);
                if (app == null) { Error("지원하지 않는 형식입니다:\n" + src); continue; }
                if (!Office.IsAvailable(Office.ProgIdFor(src))) { Error(app + " 이(가) 설치되어 있어야 합니다:\n" + src); continue; }
                string pdf = Path.ChangeExtension(src, ".pdf");
                if (File.Exists(pdf))
                {
                    var r = MessageBox.Show(this, pdf + "\n\n이미 있습니다. 덮어쓸까요?", AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) break;
                    if (r == DialogResult.No) continue;
                }
                try
                {
                    string s = src, p = pdf;
                    await RunWithProgressVoid("PDF로 변환", Path.GetFileName(src) + "\n" + app + " 이(가) 변환하는 중…", false,
                        ct => Tools.ConvertToPdfAsync(s, p));
                    results.Add(pdf);
                }
                catch (Exception ex) { Error("변환 실패:\n" + src + "\n\n" + ex.Message); }
            }
            if (results.Count > 0) OpenPaths(results);
        }

        // ---------- 자체 시험 ----------

        public void SelftestLog(string line)
        {
            if (SelftestDir == null) return;
            try
            {
                File.AppendAllText(Path.Combine(SelftestDir, "log.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public void SelftestFail(string why)
        {
            SelftestLog("FAIL " + why);
            Program.ExitCode = 4;
            ForceCloseWindow();
        }

        public void SelftestFinish(bool ok)
        {
            if (ok && selftestPhase == 0 && startFiles.Count >= 2)
            {
                // 2차: 두 번째 인스턴스를 띄워 둘째 파일을 넘긴다 → 파이프를 거쳐 이 창의 새 탭으로 와야 한다
                selftestPhase = 1;
                SelftestLog("PHASE2 launching second instance for " + startFiles[1]);
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--selftest-client \"" + startFiles[1] + "\"");
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                }
                catch (Exception ex) { SelftestFail("second instance: " + ex.Message); }
                return;
            }
            SelftestLog(ok ? "DONE OK" : "DONE FAIL");
            Program.ExitCode = ok ? 0 : 3;
            ForceCloseWindow();
        }

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        void CaptureWindow(string name)
        {
            try
            {
                using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(Path.Combine(SelftestDir, name), System.Drawing.Imaging.ImageFormat.Png);
                }
                SelftestLog("CAPTURED " + name);
                // 제목 표시줄까지 포함한 창 전체 (DWM 색이 보이는지 확인용)
                using (var bmp = new Bitmap(Bounds.Width, Bounds.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(Handle, hdc, 2);
                        g.ReleaseHdc(hdc);
                    }
                    bmp.Save(Path.Combine(SelftestDir, "frame_" + name), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { SelftestLog("capture failed: " + ex.Message); }
        }

        public async void OnPassiveTabReady(DocTab doc)
        {
            try
            {
                await Task.Delay(1500);
                SelftestLog("DPI window=" + Dpi + " DeviceDpi=" + DeviceDpi + " headerHeight=" + header.Height + " bounds=" + Bounds + " cache304=" + DocTab.NotModifiedCount);
                SelftestLog("TABS " + docs.Count + " : " + string.Join(" | ", docs.Select(d => d.TabTitle)) +
                    " active=" + (ActiveDoc != null ? ActiveDoc.TabTitle : "") + " title=" + Text);
                CaptureWindow("03_window.png");
                await doc.CaptureShotPublic("04_tab2.png");
                // 빈 탭(시작 화면)도 하나 열어 찍어 본다
                var empty = await NewTab(null);
                empty.ReadyTcs = new TaskCompletionSource<bool>();
                await WithTimeout(empty.ReadyTcs.Task, 20000);
                await Task.Delay(1000);
                await empty.CaptureShotPublic("05_start.png");
                CaptureWindow("06_window_start.png");
                SelectTab(doc);
                RemoveTab(empty);
                bool ok = await SelftestTools(doc);
                SelftestLog(ok ? "DONE OK" : "DONE FAIL");
                Program.ExitCode = ok ? 0 : 5;
                ForceCloseWindow();
            }
            catch (Exception ex) { SelftestFail("phase2: " + ex); }
        }

        // 3차: 도구들을 둘째 탭(시험용 사본)에 차례로 돌려 본다
        async Task<bool> SelftestTools(DocTab doc)
        {
            bool ok = true;
            string dir = SelftestDir;
            try
            {
                // 회전 저장 → 다시 불러옴 → 1쪽 rotate 확인
                doc.ReadyTcs = new TaskCompletionSource<bool>();
                doc.Rotate(new List<int> { 1 }, 90);
                await WithTimeout(doc.ReadyTcs.Task, 30000);
                await Task.Delay(800);
                string rot = await doc.EvalTestAsync("window.PDFViewerApplication.pdfDocument.getPage(1).then(function(p){chrome.webview.postMessage('testresult|rotate:'+p.rotate+';count='+window.PDFViewerApplication.pagesCount);});");
                SelftestLog("TOOL rotate → " + rot + " bytes=" + new FileInfo(doc.DocPath).Length);
                if (rot.IndexOf("rotate:90", StringComparison.Ordinal) < 0) ok = false;
            }
            catch (Exception ex) { SelftestLog("TOOL rotate FAILED " + ex.Message); ok = false; }

            string gs = Tools.FindGs();
            if (gs == null) { SelftestLog("TOOL gs not found - compress/unlock skipped"); ok = false; }
            else
            {
                try
                {
                    long before = new FileInfo(doc.DocPath).Length;
                    string tmp = await Tools.GsCompressAsync(gs, doc.DocPath, "/ebook", CancellationToken.None);
                    long after = new FileInfo(tmp).Length;
                    File.Copy(tmp, Path.Combine(dir, "compressed.pdf"), true);
                    File.Delete(tmp);
                    SelftestLog("TOOL compress " + before + " → " + after);
                    if (after <= 0) ok = false;
                }
                catch (Exception ex) { SelftestLog("TOOL compress FAILED " + ex.Message); ok = false; }
                try
                {
                    string enc = await Tools.GsEncryptAsync(gs, doc.DocPath, "u1", "o1", CancellationToken.None);
                    string dec = await Tools.GsUnlockAsync(gs, enc, "u1", CancellationToken.None);
                    SelftestLog("TOOL unlock enc=" + new FileInfo(enc).Length + " dec=" + new FileInfo(dec).Length);
                    string wrongMsg = "";
                    try { string bad = await Tools.GsUnlockAsync(gs, enc, "nope", CancellationToken.None); File.Delete(bad); wrongMsg = "NO ERROR (unexpected)"; ok = false; }
                    catch (Exception ex2) { wrongMsg = "rejected as expected: " + Util.Truncate(ex2.Message.Replace("\n", " "), 120); }
                    SelftestLog("TOOL unlock wrong password → " + wrongMsg);
                    File.Delete(enc); File.Delete(dec);
                }
                catch (Exception ex) { SelftestLog("TOOL unlock FAILED " + ex.Message); ok = false; }
            }

            try
            {
                doc.ExportTextPath = Path.Combine(dir, "text.txt");
                int n = await RunJsExport(doc, null, null, "text");
                long len = new FileInfo(doc.ExportTextPath).Length;
                SelftestLog("TOOL text pages=" + n + " bytes=" + len);
                if (len < 50) ok = false;
            }
            catch (Exception ex) { SelftestLog("TOOL text FAILED " + ex.Message); ok = false; }

            try
            {
                doc.ExportDir = Path.Combine(dir, "images");
                int n = await RunJsExport(doc, null, null, "extract");
                SelftestLog("TOOL images count=" + n + " files=" + (Directory.Exists(doc.ExportDir) ? Directory.GetFiles(doc.ExportDir).Length : 0));
            }
            catch (Exception ex) { SelftestLog("TOOL images FAILED " + ex.Message); ok = false; }

            try
            {
                doc.ExportDir = Path.Combine(dir, "pages");
                int n = await RunJsExport(doc, null, null, "pages2img|{\"format\":\"png\",\"dpi\":100,\"pages\":[1,2]}");
                SelftestLog("TOOL pages2img count=" + n + " files=" + string.Join(",", Directory.GetFiles(doc.ExportDir).Select(Path.GetFileName)));
                if (n != 2) ok = false;
            }
            catch (Exception ex) { SelftestLog("TOOL pages2img FAILED " + ex.Message); ok = false; }

            if (!Office.IsAvailable("Word.Application")) SelftestLog("TOOL word not installed - skipped");
            else
            {
                string docx = Path.Combine(dir, "converted.docx");
                try
                {
                    var sw = Stopwatch.StartNew();
                    await Tools.PdfToWordAsync(doc.DocPath, docx);
                    SelftestLog("TOOL pdf→word bytes=" + new FileInfo(docx).Length + " " + sw.ElapsedMilliseconds + "ms");
                }
                catch (Exception ex) { SelftestLog("TOOL pdf→word FAILED " + ex.Message); ok = false; }
                try
                {
                    var sw = Stopwatch.StartNew();
                    string back = Path.Combine(dir, "converted_back.pdf");
                    await Tools.ConvertToPdfAsync(docx, back);
                    SelftestLog("TOOL word→pdf bytes=" + new FileInfo(back).Length + " " + sw.ElapsedMilliseconds + "ms");
                }
                catch (Exception ex) { SelftestLog("TOOL word→pdf FAILED " + ex.Message); ok = false; }
            }
            SelftestLog("TOOLS " + (ok ? "OK" : "FAIL"));
            return ok;
        }
    }

    // ===================================================================================
    //  헤더: 프로그램 색 띠 위에 크롬식 탭, "+", 오른쪽에 메뉴 버튼. 빈 자리를 끌면 창이 움직인다.
    // ===================================================================================
    class HeaderBar : Control
    {
        public static readonly Color Brand = Color.FromArgb(27, 78, 145);
        static readonly Color TextDark = Color.FromArgb(0x1F, 0x23, 0x28);

        readonly MainForm form;
        readonly Font tabFont;
        readonly ToolTip tip = new ToolTip();
        string[] labels = new string[0];
        ContextMenuStrip[] menus = new ContextMenuStrip[0];
        readonly List<Rectangle> tabRects = new List<Rectangle>();
        readonly List<Rectangle> closeRects = new List<Rectangle>();
        readonly List<Rectangle> buttonRects = new List<Rectangle>();
        Rectangle plusRect;
        int hoverTab = -1, hoverButton = -1, openMenu = -1;
        bool hoverClose, hoverPlus;
        string lastTip = "";

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public HeaderBar(MainForm form)
        {
            this.form = form;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.StandardDoubleClick, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Brand;
            tabFont = new Font("Malgun Gothic", 10f);
            Height = S(46);
            tip.ShowAlways = true;
        }

        int S(int px) { return form.Scale(px); }

        public void RefreshSize()
        {
            Height = S(46);
            Invalidate();
        }

        public void SetMenus(string[] labels, ContextMenuStrip[] menus)
        {
            this.labels = labels;
            this.menus = menus;
            foreach (var m in menus) m.Closed += delegate { openMenu = -1; Invalidate(); };
        }

        // 탭·버튼 자리 계산
        void DoLayout()
        {
            var docs = form.Docs;
            int pad = S(8), top = S(7), gap = S(3);
            int tabH = Height - top;

            buttonRects.Clear();
            int x = Width - pad;
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                int w = TextRenderer.MeasureText(labels[i], tabFont).Width + S(20);
                x -= w;
                buttonRects.Insert(0, new Rectangle(x, S(9), w, Height - S(18)));
                x -= S(2);
            }
            int rightLimit = (buttonRects.Count > 0 ? buttonRects[0].Left : Width) - S(14);
            int plusW = S(30);
            int avail = rightLimit - pad - plusW - S(4);
            int n = docs.Count;
            int tabW = n > 0 ? Math.Max(S(96), Math.Min(S(300), avail / n)) : 0;

            tabRects.Clear();
            closeRects.Clear();
            int tx = pad;
            for (int i = 0; i < n; i++)
            {
                var r = new Rectangle(tx, top, tabW - gap, tabH);
                tabRects.Add(r);
                int cs = S(18);
                closeRects.Add(new Rectangle(r.Right - cs - S(7), r.Y + (r.Height - cs) / 2, cs, cs));
                tx += tabW;
            }
            plusRect = new Rectangle(tx + S(2), top + (tabH - S(28)) / 2, S(28), S(28));
        }

        static GraphicsPath RoundedTop(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.CloseFigure();
            return p;
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            DoLayout();
            var g = e.Graphics;
            g.Clear(Brand);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var docs = form.Docs;
            int active = form.ActiveIndex;
            var white = Color.White;

            for (int i = 0; i < docs.Count && i < tabRects.Count; i++)
            {
                var doc = docs[i];
                var r = tabRects[i];
                bool isActive = i == active;
                if (isActive)
                {
                    using (var path = RoundedTop(r, S(8)))
                    using (var b = new SolidBrush(white)) g.FillPath(b, path);
                }
                else if (i == hoverTab)
                {
                    using (var path = Rounded(new Rectangle(r.X, r.Y, r.Width, r.Height - S(2)), S(8)))
                    using (var b = new SolidBrush(Color.FromArgb(34, 255, 255, 255))) g.FillPath(b, path);
                }
                // 비활성 탭 사이 구분선
                if (!isActive && i + 1 < docs.Count && i + 1 != active && i != hoverTab && i + 1 != hoverTab)
                {
                    using (var p = new Pen(Color.FromArgb(80, 255, 255, 255)))
                        g.DrawLine(p, r.Right + 1, r.Y + S(10), r.Right + 1, r.Bottom - S(10));
                }
                Color fg = isActive ? TextDark : white;
                int textLeft = r.X + S(12);
                if (doc.Dirty)
                {
                    int dot = S(7);
                    using (var b = new SolidBrush(isActive ? Brand : white))
                        g.FillEllipse(b, textLeft, r.Y + (r.Height - dot) / 2, dot, dot);
                    textLeft += dot + S(7);
                }
                var cr = closeRects[i];
                var textRect = new Rectangle(textLeft, r.Y, Math.Max(10, cr.Left - S(6) - textLeft), r.Height);
                TextRenderer.DrawText(g, doc.TitleText, tabFont, textRect, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                // 닫기 ×
                bool ch = i == hoverTab && hoverClose;
                if (ch)
                {
                    using (var b = new SolidBrush(isActive ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255)))
                        g.FillEllipse(b, cr);
                }
                int inset = S(5);
                using (var p = new Pen(isActive ? Color.FromArgb(0x55, 0x5A, 0x62) : Color.FromArgb(230, 255, 255, 255), Math.Max(1f, S(1) * 1.1f)))
                {
                    g.DrawLine(p, cr.X + inset, cr.Y + inset, cr.Right - inset, cr.Bottom - inset);
                    g.DrawLine(p, cr.X + inset, cr.Bottom - inset, cr.Right - inset, cr.Y + inset);
                }
            }

            // 새 탭 +
            if (hoverPlus)
            {
                using (var path = Rounded(plusRect, S(6)))
                using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillPath(b, path);
            }
            using (var p = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, S(1) * 1.3f)))
            {
                int cx = plusRect.X + plusRect.Width / 2, cy = plusRect.Y + plusRect.Height / 2, a = S(6);
                g.DrawLine(p, cx - a, cy, cx + a, cy);
                g.DrawLine(p, cx, cy - a, cx, cy + a);
            }

            // 메뉴 버튼
            for (int i = 0; i < labels.Length && i < buttonRects.Count; i++)
            {
                var br = buttonRects[i];
                if (i == hoverButton || i == openMenu)
                {
                    using (var path = Rounded(br, S(5)))
                    using (var b = new SolidBrush(Color.FromArgb(i == openMenu ? 60 : 36, 255, 255, 255))) g.FillPath(b, path);
                }
                TextRenderer.DrawText(g, labels[i], tabFont, br, white,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }

        int HitTab(Point pt)
        {
            for (int i = 0; i < tabRects.Count; i++) if (tabRects[i].Contains(pt)) return i;
            return -1;
        }

        int HitButton(Point pt)
        {
            for (int i = 0; i < buttonRects.Count; i++) if (buttonRects[i].Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            DoLayout();
            int t = HitTab(e.Location);
            bool c = t >= 0 && closeRects[t].Contains(e.Location);
            bool p = plusRect.Contains(e.Location);
            int b = HitButton(e.Location);
            if (t != hoverTab || c != hoverClose || p != hoverPlus || b != hoverButton)
            {
                hoverTab = t; hoverClose = c; hoverPlus = p; hoverButton = b;
                Invalidate();
            }
            string tipText = t >= 0 ? (form.Docs[t].DocPath ?? "새 탭") : (p ? "열기 (Ctrl+O)" : "");
            if (tipText != lastTip) { lastTip = tipText; tip.SetToolTip(this, tipText); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverTab = -1; hoverButton = -1; hoverClose = false; hoverPlus = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            DoLayout();
            int t = HitTab(e.Location);
            if (e.Button == MouseButtons.Middle)
            {
                if (t >= 0) { var d = form.Docs[t]; BeginInvoke((Action)delegate { form.CloseTab(d); }); }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (t >= 0)
            {
                var d = form.Docs[t];
                if (closeRects[t].Contains(e.Location)) BeginInvoke((Action)delegate { form.CloseTab(d); });
                else form.SelectTab(d);
                return;
            }
            if (plusRect.Contains(e.Location)) { form.OpenDialog(); return; }
            int b = HitButton(e.Location);
            if (b >= 0) { ShowMenu(b); return; }
            // 빈 자리: 두 번 누르면 최대화/복원, 끌면 창 이동
            if (e.Clicks == 2) { form.ToggleMaximize(); return; }
            ReleaseCapture();
            SendMessage(form.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }

        void ShowMenu(int i)
        {
            if (i < 0 || i >= menus.Length) return;
            openMenu = i;
            Invalidate();
            var br = buttonRects[i];
            menus[i].Show(this, new Point(br.Left, br.Bottom + S(2)));
        }
    }

    // 드롭다운 메뉴를 단순하고 밝게
    class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }
    }

    class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Color.FromArgb(0xE6, 0xEC, 0xF5); } }
        public override Color MenuItemSelectedGradientBegin { get { return MenuItemSelected; } }
        public override Color MenuItemSelectedGradientEnd { get { return MenuItemSelected; } }
        public override Color MenuItemBorder { get { return Color.FromArgb(0xC9, 0xD5, 0xE8); } }
        public override Color MenuBorder { get { return Color.FromArgb(0xC8, 0xCC, 0xD2); } }
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color SeparatorDark { get { return Color.FromArgb(0xE3, 0xE6, 0xEA); } }
        public override Color SeparatorLight { get { return Color.White; } }
    }

    // 제목 표시줄 색 (Windows 11)
    static class Chrome
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        public static void ApplyCaption(Form f, Color bg, Color fg)
        {
            try
            {
                int c = ColorRef(bg);
                DwmSetWindowAttribute(f.Handle, DWMWA_CAPTION_COLOR, ref c, 4);
                int t = ColorRef(fg);
                DwmSetWindowAttribute(f.Handle, DWMWA_TEXT_COLOR, ref t, 4);
                int b = ColorRef(bg);
                DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref b, 4);
            }
            catch { }
        }
    }

    // ===================================================================================
    //  탭 하나 = 문서 하나 (WebView2 + PDF.js 뷰어, 또는 시작 화면)
    // ===================================================================================
    class DocTab
    {
        const string Host = "pdfjs.local";
        static readonly string PdfjsDir = Path.GetFullPath(Path.Combine(MainForm.BaseDir, "pdfjs"));
        static readonly string StartDir = Path.GetFullPath(Path.Combine(MainForm.BaseDir, "start"));

        public readonly MainForm Form;
        public readonly WebView2 Web;
        public CoreWebView2 Core;

        public string DocPath;       // 지금 열린 파일의 실제 경로 (없으면 null = 시작 화면)
        string docToken;             // 뷰어가 파일을 요청할 때 쓰는 1회용 토큰
        string pageTitle = "";
        public bool Dirty;           // 저장 안 된 변경이 있는지
        public bool CloseAfterSave;  // 닫기 → 저장 → 저장 끝나면 닫기
        public bool ForceClose;      // 저장 여부 묻지 않고 닫기
        public bool Passive;         // 자체 시험 2차에서 열린 탭 (1차 시험 흐름을 돌리지 않음)
        bool passiveStarted;
        int selftestStep;
        string selftestSavedPath;

        // 내보내기 (뷰어가 /export/ 로 보내는 파일의 목적지)
        public string ExportDir;
        public string ExportTextPath;
        public ProgressForm ExportProgress;
        public TaskCompletionSource<int> ExportTcs;
        public TaskCompletionSource<bool> ReadyTcs;
        public TaskCompletionSource<string> TestResultTcs;

        public DocTab(MainForm form)
        {
            Form = form;
            Web = new WebView2();
            Web.Dock = DockStyle.Fill;
            Web.DefaultBackgroundColor = Color.White;
            Web.Visible = false;
        }

        public bool IsEmpty { get { return string.IsNullOrEmpty(DocPath) && !Dirty; } }
        public string DisplayName { get { return string.IsNullOrEmpty(DocPath) ? "새 탭" : Path.GetFileName(DocPath); } }

        // 탭에 쓰는 제목: 파일 이름(확장자 없이). PDF 안의 긴 제목은 쓰지 않는다
        public string TitleText
        {
            get
            {
                if (string.IsNullOrEmpty(DocPath)) return "새 탭";
                string n = Path.GetFileNameWithoutExtension(DocPath);
                return string.IsNullOrEmpty(n) ? DisplayName : n;
            }
        }

        // 창 제목에 쓰는 제목 (수정되면 * 앞에 붙음): PDF 제목이 있으면 그것, 없으면 파일 이름
        public string TabTitle
        {
            get
            {
                string t = (pageTitle ?? "").TrimStart('*', ' ');
                if (string.IsNullOrEmpty(DocPath) || string.IsNullOrEmpty(t)) t = TitleText;
                return (Dirty ? "*" : "") + t;
            }
        }

        public void Focus()
        {
            try { if (Web.Visible) Web.Focus(); } catch { }
        }

        public void Dispose()
        {
            try { Web.Dispose(); } catch { }
        }

        public async Task InitAsync(CoreWebView2Environment env, string injectScript, string path)
        {
            await Web.EnsureCoreWebView2Async(env);
            Core = Web.CoreWebView2;

            var s = Core.Settings;
            s.AreBrowserAcceleratorKeysEnabled = false; // 새로 고침 등 브라우저 단축키 차단 (편집 내용 보호)
            s.IsZoomControlEnabled = false;             // 확대/축소는 PDF.js가 담당
            s.IsPinchZoomEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.AreDevToolsEnabled = true;
            // SmartScreen 평판 조회 끄기: 내부 주소(pdfjs.local)는 조회가 안 되어 페이지를 열 때마다 2초씩 기다리게 했다
            try { s.IsReputationCheckingRequired = false; } catch { }

            // https://pdfjs.local/ 로 오는 모든 요청(문서·워커 포함)을 직접 처리한다:
            //   /web/, /build/ 등 정적 파일은 pdfjs 폴더, /start/ 는 시작 화면, /doc/ 는 열린 PDF, /save/ 는 저장, /export/ 는 내보내기
            // (폴더 매핑 SetVirtualHostNameToFolderMapping 은 WebResourceRequested 보다 먼저 응답해 버려 가로챌 수 없음)
            Core.AddWebResourceRequestedFilter("https://" + Host + "/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            Core.WebResourceRequested += OnWebResourceRequested;
            Core.WebMessageReceived += OnWebMessageReceived;
            Core.DocumentTitleChanged += delegate { pageTitle = Core.DocumentTitle; Form.UpdateTitle(); };
            Core.NewWindowRequested += OnNewWindowRequested;
            Core.NavigationStarting += OnNavigationStarting;
            Core.ContextMenuRequested += OnContextMenuRequested;
            Core.DownloadStarting += OnDownloadStarting;
            Core.ProcessFailed += delegate(object o, CoreWebView2ProcessFailedEventArgs pe)
            {
                Program.Log("ProcessFailed " + pe.ProcessFailedKind + " " + pe.Reason);
            };

            Web.NavigationCompleted += delegate { Form.HideLoading(); Form.SelftestLog("NAV done at +" + Form.SinceStartMs + " ms"); };
            Form.SelftestLog("CORE ready at +" + Form.SinceStartMs + " ms");
            if (Form.SelftestDir != null) await SetupConsoleCapture();
            await Core.AddScriptToExecuteOnDocumentCreatedAsync(injectScript);
            NavigateTo(path);
        }

        // ---------- 문서 열기, 뷰어에 명령 ----------

        Task<byte[]> prefetch;     // 뷰어가 뜨는 동안 PDF를 미리 읽어 두는 작업
        string prefetchToken;
        public static int NotModifiedCount; // 캐시 재사용(304) 횟수 (시험 기록용)

        public void NavigateTo(string path)
        {
            DocPath = path;
            docToken = Guid.NewGuid().ToString("N");
            Dirty = false;
            pageTitle = "";
            prefetch = null;
            string url;
            if (!string.IsNullOrEmpty(path))
            {
                string fileUrl = "https://" + Host + "/doc/" + docToken + "/" + Uri.EscapeDataString(Path.GetFileName(path));
                url = "https://" + Host + "/web/viewer.html?file=" + Uri.EscapeDataString(fileUrl);
                Util.AddToRecent(path);
                try
                {
                    long size = new FileInfo(path).Length;
                    if (size <= 256L * 1024 * 1024)
                    {
                        string p = path;
                        prefetchToken = docToken;
                        prefetch = Task.Run(new Func<byte[]>(delegate { return File.ReadAllBytes(p); }));
                    }
                }
                catch { prefetch = null; }
            }
            else
            {
                url = "https://" + Host + "/start/start.html";
            }
            Core.Navigate(url);
            Form.UpdateTitle();
        }

        public void PostCommand(string command)
        {
            if (Core != null) Core.PostWebMessageAsString(command);
        }

        public void RequestSave()
        {
            PostCommand("save");
        }

        public void Rotate(List<int> pages, int delta)
        {
            var sb = new StringBuilder("{\"pages\":[");
            for (int i = 0; i < pages.Count; i++) { if (i > 0) sb.Append(','); sb.Append(pages[i]); }
            sb.Append("],\"delta\":").Append(delta).Append('}');
            PostCommand("rotate|" + sb);
        }

        // "현재쪽|전체쪽|구조변경여부(0/1)"
        public async Task<string> GetPageInfoAsync()
        {
            if (Core == null) throw new Exception("뷰어가 준비되지 않았습니다");
            string r = await Core.ExecuteScriptAsync(
                "(function(){var a=window.PDFViewerApplication;if(!a||!a.pdfDocument)return '';var s=false;" +
                "try{s=!!(a.pdfThumbnailViewer&&a.pdfThumbnailViewer.hasStructuralChanges());}catch(e){}" +
                "return a.page+'|'+a.pagesCount+'|'+(s?1:0);})()");
            if (string.IsNullOrEmpty(r) || r == "null") throw new Exception("문서가 아직 열리지 않았습니다");
            r = r.Trim('"');
            if (r.Length == 0) throw new Exception("문서가 아직 열리지 않았습니다");
            return r;
        }

        public async Task<string> EvalTestAsync(string js)
        {
            var tcs = new TaskCompletionSource<string>();
            TestResultTcs = tcs;
            await Core.ExecuteScriptAsync(js);
            return await MainForm.WithTimeout(tcs.Task, 20000);
        }

        // ---------- 뷰어의 요청 처리: 정적 파일, 시작 화면, 문서 바이트, 저장, 내보내기 ----------

        static byte[] ReadBody(CoreWebView2WebResourceRequestedEventArgs e, out string err)
        {
            err = null;
            try
            {
                var content = e.Request.Content;
                if (content == null) return null;
                using (var ms = new MemoryStream())
                {
                    content.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        static void RespondJson(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
        }

        void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            try { uri = new Uri(e.Request.Uri); } catch { return; }
            var env = Core.Environment;
            if (Form.SelftestDir != null) Form.SelftestLog("REQUEST " + e.Request.Method + " " + uri.AbsolutePath);
            if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)) return;
            string path = uri.AbsolutePath;

            if (path == "/favicon.ico")
            {
                e.Response = env.CreateWebResourceResponse(null, 204, "No Content", "");
                return;
            }

            if (path == "/start/recent.json")
            {
                RespondJson(e, env, RecentList.ToJson());
                return;
            }
            if (path == "/start/notice.json")
            {
                // 프로젝트 공지: 시작 때 받아 둔 JSON (아직 안 왔으면 기다렸다가 응답)
                var deferral = e.GetDeferral();
                Notice.Fetch(Form.SelftestDir).ContinueWith(delegate(Task<string> t)
                {
                    Form.BeginInvoke((Action)delegate
                    {
                        string json = (t.Status == TaskStatus.RanToCompletion && t.Result != null) ? t.Result : "{}";
                        RespondJson(e, env, json);
                        deferral.Complete();
                    });
                });
                return;
            }
            if (path.StartsWith("/start/"))
            {
                ServeStatic(e, env, StartDir, path.Substring("/start/".Length));
                return;
            }

            if (path.StartsWith("/doc/"))
            {
                string[] parts = path.Split('/');
                if (parts.Length >= 3 && parts[2] == docToken && !string.IsNullOrEmpty(DocPath) && File.Exists(DocPath))
                {
                    // 뷰어가 뜨는 동안 미리 읽어 둔 바이트가 있으면 그것으로 (네트워크 드라이브에서 특히 빠름)
                    var pre = prefetch;
                    if (pre != null && prefetchToken == docToken)
                    {
                        var deferral = e.GetDeferral();
                        var self = this;
                        pre.ContinueWith(delegate(Task<byte[]> t)
                        {
                            Form.BeginInvoke((Action)delegate
                            {
                                try
                                {
                                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                                    {
                                        e.Response = env.CreateWebResourceResponse(new MemoryStream(t.Result), 200, "OK",
                                            "Content-Type: application/pdf\r\nContent-Length: " + t.Result.Length + "\r\nCache-Control: no-store\r\nAccept-Ranges: none");
                                    }
                                    else
                                    {
                                        self.RespondDocFromDisk(e, env);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Program.Log("doc response failed: " + ex.Message);
                                    e.Response = env.CreateWebResourceResponse(null, 500, "Error", "Content-Type: text/plain");
                                }
                                deferral.Complete();
                            });
                        });
                        return;
                    }
                    RespondDocFromDisk(e, env);
                    return;
                }
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            if (path.StartsWith("/save/"))
            {
                var deferral = e.GetDeferral();
                string readErr;
                byte[] data = ReadBody(e, out readErr);
                bool saveAs = uri.Query.IndexOf("saveas=1", StringComparison.Ordinal) >= 0;
                string suggested = Uri.UnescapeDataString(path.Substring("/save/".Length));
                Form.BeginInvoke((Action)delegate
                {
                    string resultJson;
                    try { resultJson = HandleSave(data, readErr, saveAs, suggested); }
                    catch (Exception ex) { Program.Log("save error: " + ex); resultJson = Util.JsonResult(false, null, ex.Message, false); }
                    RespondJson(e, env, resultJson);
                    deferral.Complete();
                    if (selftestSavedPath != null)
                    {
                        string t = selftestSavedPath;
                        selftestSavedPath = null;
                        Form.BeginInvoke((Action)delegate { OnSelftestSaved(t); });
                    }
                });
                return;
            }

            if (path.StartsWith("/export/"))
            {
                string readErr;
                byte[] data = ReadBody(e, out readErr);
                string name = Uri.UnescapeDataString(path.Substring("/export/".Length));
                string result;
                try
                {
                    if (data == null) throw new Exception("데이터를 받지 못했습니다" + (readErr != null ? " (" + readErr + ")" : ""));
                    string target;
                    if (name == "__text__.txt")
                    {
                        if (string.IsNullOrEmpty(ExportTextPath)) throw new Exception("저장 위치가 정해지지 않았습니다");
                        target = ExportTextPath;
                        File.WriteAllText(target, Encoding.UTF8.GetString(data), new UTF8Encoding(true));
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(ExportDir)) throw new Exception("내보내기 폴더가 정해지지 않았습니다");
                        Directory.CreateDirectory(ExportDir);
                        target = Path.Combine(ExportDir, Path.GetFileName(name));
                        File.WriteAllBytes(target, data);
                    }
                    result = Util.JsonResult(true, target, null, false);
                }
                catch (Exception ex)
                {
                    Program.Log("export failed: " + name + " : " + ex.Message);
                    result = Util.JsonResult(false, null, ex.Message, false);
                }
                RespondJson(e, env, result);
                return;
            }

            ServeStatic(e, env, PdfjsDir, path);
        }

        void RespondDocFromDisk(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env)
        {
            try
            {
                var fs = new FileStream(DocPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                e.Response = env.CreateWebResourceResponse(fs, 200, "OK",
                    "Content-Type: application/pdf\r\nContent-Length: " + fs.Length + "\r\nCache-Control: no-store\r\nAccept-Ranges: none");
            }
            catch (Exception ex)
            {
                Program.Log("open failed: " + DocPath + " : " + ex.Message);
                e.Response = env.CreateWebResourceResponse(null, 500, "Error", "Content-Type: text/plain");
            }
        }

        // 폴더의 정적 파일(viewer.html, *.mjs, cmaps, 글꼴, wasm, 번역, 시작 화면 ...)을 돌려준다.
        // ETag를 붙이고 같은 ETag로 다시 물으면 304로 답해, 브라우저가 캐시(컴파일된 스크립트 포함)를 재사용하게 한다.
        static void ServeStatic(CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env, string rootDir, string path)
        {
            try
            {
                string rel = Uri.UnescapeDataString(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(rootDir, rel));
                if (!full.StartsWith(rootDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                    return;
                }
                var fi = new FileInfo(full);
                string etag = "\"" + fi.Length.ToString("x") + "-" + fi.LastWriteTimeUtc.Ticks.ToString("x") + "\"";
                string ifNoneMatch = null;
                try { if (e.Request.Headers.Contains("If-None-Match")) ifNoneMatch = e.Request.Headers.GetHeader("If-None-Match"); } catch { }
                if (ifNoneMatch != null && ifNoneMatch.Trim() == etag)
                {
                    Interlocked.Increment(ref NotModifiedCount);
                    e.Response = env.CreateWebResourceResponse(null, 304, "Not Modified", "ETag: " + etag + "\r\nCache-Control: no-cache");
                    return;
                }
                byte[] bytes = File.ReadAllBytes(full);
                e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                    "Content-Type: " + Util.MimeFor(full) + "\r\nContent-Length: " + bytes.Length + "\r\nETag: " + etag + "\r\nCache-Control: no-cache");
            }
            catch (Exception ex)
            {
                Program.Log("static failed: " + path + " : " + ex.Message);
                e.Response = env.CreateWebResourceResponse(null, 500, "Error", "Content-Type: text/plain");
            }
        }

        string HandleSave(byte[] data, string readErr, bool saveAs, string suggestedName)
        {
            if (data == null || data.Length == 0)
            {
                CloseAfterSave = false; Form.CancelClosing();
                return Util.JsonResult(false, null, "저장할 데이터를 받지 못했습니다" + (readErr != null ? " (" + readErr + ")" : ""), false);
            }
            if (data.Length < 5 || data[0] != (byte)'%' || data[1] != (byte)'P' || data[2] != (byte)'D' || data[3] != (byte)'F')
            {
                CloseAfterSave = false; Form.CancelClosing();
                return Util.JsonResult(false, null, "PDF 데이터가 아닙니다", false);
            }

            // 추출/내보내기("export:" 접두)는 항상 다른 파일로 저장하고, 현재 문서 경로는 바꾸지 않는다
            bool isExport = suggestedName != null && suggestedName.StartsWith("export:");
            if (isExport) { suggestedName = suggestedName.Substring("export:".Length); saveAs = true; }

            string target = DocPath;
            if (saveAs || string.IsNullOrEmpty(target))
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = isExport ? "추출한 페이지 저장" : "다른 이름으로 저장";
                    dlg.Filter = "PDF 파일 (*.pdf)|*.pdf";
                    dlg.DefaultExt = "pdf";
                    dlg.AddExtension = true;
                    dlg.OverwritePrompt = true;
                    dlg.FileName = !string.IsNullOrEmpty(suggestedName) ? suggestedName
                        : (!string.IsNullOrEmpty(target) ? Path.GetFileName(target) : "document.pdf");
                    if (!string.IsNullOrEmpty(target))
                    {
                        try { dlg.InitialDirectory = Path.GetDirectoryName(target); } catch { }
                    }
                    if (dlg.ShowDialog(Form) != DialogResult.OK)
                    {
                        CloseAfterSave = false; Form.CancelClosing();
                        return Util.JsonResult(false, null, null, true);
                    }
                    target = dlg.FileName;
                }
            }

            try
            {
                Util.WriteWithBackup(target, data);
            }
            catch (Exception ex)
            {
                Program.Log("save failed: " + target + " : " + ex);
                CloseAfterSave = false; Form.CancelClosing();
                return Util.JsonResult(false, target, "파일을 쓰지 못했습니다: " + ex.Message, false);
            }

            if (!isExport)
            {
                DocPath = target;
                Dirty = false;
            }
            Util.AddToRecent(target);
            Form.UpdateTitle();
            if (CloseAfterSave)
            {
                CloseAfterSave = false;
                ForceClose = true;
                Form.BeginInvoke((Action)delegate { Form.RemoveTab(this); });
            }
            if (Form.SelftestDir != null && !Passive) selftestSavedPath = target;
            return Util.JsonResult(true, target, null, false);
        }

        // ---------- 뷰어 → 호스트 메시지 ----------

        void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try { msg = e.TryGetWebMessageAsString(); } catch { return; }
            if (msg == null) return;
            int i = msg.IndexOf('|');
            string type = i < 0 ? msg : msg.Substring(0, i);
            string arg = i < 0 ? "" : msg.Substring(i + 1);
            switch (type)
            {
                case "open":
                    Form.OpenDialog();
                    break;
                case "openpath":
                    if (!string.IsNullOrEmpty(arg)) Form.OpenPaths(new List<string> { arg });
                    break;
                case "officetopdf":
                    Form.ToolOfficeToPdf();
                    break;
                case "openurl":
                    Util.OpenExternal(arg);
                    break;
                case "drop":
                    {
                        var paths = new List<string>();
                        if (e.AdditionalObjects != null)
                        {
                            foreach (var o in e.AdditionalObjects)
                            {
                                var f = o as CoreWebView2File;
                                if (f != null && !string.IsNullOrEmpty(f.Path)) paths.Add(f.Path);
                            }
                        }
                        Form.OpenPaths(paths);
                        break;
                    }
                case "close":
                    Form.CloseTab(this);
                    break;
                case "nexttab":
                    Form.CycleTab(1);
                    break;
                case "prevtab":
                    Form.CycleTab(-1);
                    break;
                case "devtools":
                    Core.OpenDevToolsWindow();
                    break;
                case "dirty":
                    Dirty = arg == "1";
                    Form.UpdateTitle();
                    break;
                case "reload":
                    if (!string.IsNullOrEmpty(DocPath)) NavigateTo(DocPath);
                    break;
                case "progress":
                    {
                        int bar = arg.IndexOf('|');
                        string text = bar < 0 ? arg : arg.Substring(bar + 1);
                        if (ExportProgress != null) ExportProgress.SetMessage(text);
                        break;
                    }
                case "exportdone":
                    {
                        int n;
                        int.TryParse(arg, out n);
                        var t = ExportTcs;
                        if (t != null) t.TrySetResult(n);
                        break;
                    }
                case "exporterror":
                    {
                        var t = ExportTcs;
                        if (t != null) t.TrySetException(new Exception(arg));
                        break;
                    }
                case "error":
                    Program.Log("viewer error: " + arg);
                    if (Form.SelftestDir != null && !Passive) Form.SelftestFail("viewer error: " + arg);
                    break;
                case "log":
                    Program.Log("page: " + arg);
                    Form.SelftestLog("PAGE " + arg);
                    break;
                case "ready":
                    {
                        var r = ReadyTcs;
                        if (r != null) { ReadyTcs = null; r.TrySetResult(true); }
                        if (Form.SelftestDir != null && !string.IsNullOrEmpty(DocPath))
                        {
                            if (Passive) { if (!passiveStarted) { passiveStarted = true; Form.OnPassiveTabReady(this); } }
                            else OnSelftestReady();
                        }
                        break;
                    }
                case "testresult":
                    {
                        var t = TestResultTcs;
                        if (t != null) { TestResultTcs = null; t.TrySetResult(arg); break; }
                        if (Form.SelftestDir != null) OnSelftestResult(arg);
                        break;
                    }
            }
        }

        // ---------- 링크, 메뉴, 내려받기 ----------

        void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            Util.OpenExternal(e.Uri);
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            Util.OpenExternal(e.Uri);
        }

        void OnContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            var keep = new HashSet<string> { "copy", "cut", "paste", "selectAll", "copyImage", "copyLinkLocation" };
            var items = e.MenuItems;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(items[i].Name)) items.RemoveAt(i);
            }
            if (items.Count == 0) e.Handled = true;
        }

        void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            e.Handled = true; // 기본 내려받기 창 대신 저장 대화상자
            var deferral = e.GetDeferral();
            string suggested = e.ResultFilePath;
            Form.BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dlg = new SaveFileDialog())
                    {
                        dlg.Title = "저장";
                        dlg.FileName = Path.GetFileName(suggested ?? "file");
                        dlg.Filter = "모든 파일 (*.*)|*.*";
                        dlg.OverwritePrompt = true;
                        if (dlg.ShowDialog(Form) == DialogResult.OK) e.ResultFilePath = dlg.FileName;
                        else e.Cancel = true;
                    }
                }
                finally { deferral.Complete(); }
            });
        }

        // ---------- 자체 시험 1차 (--selftest 폴더 파일.pdf): 열기→형광펜→저장→다시 열기 ----------

        const string HighlightTestJs = @"
(async function () {
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  var app = window.PDFViewerApplication;
  var spans = [];
  for (var i = 0; i < 40; i++) {
    var tl = document.querySelector("".page[data-page-number='1'] .textLayer"");
    spans = tl ? Array.prototype.filter.call(tl.querySelectorAll('span'), function (s) { return s.textContent.trim().length > 3; }) : [];
    if (spans.length >= 2) break;
    await sleep(250);
  }
  if (spans.length < 2) { chrome.webview.postMessage('testresult|highlight:no-text-layer'); return; }
  var r = document.createRange(); r.setStartBefore(spans[0]); r.setEndAfter(spans[Math.min(2, spans.length - 1)]);
  var sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(r);
  await sleep(400);
  var mgr = app.pdfViewer._layerProperties.annotationEditorUIManager;
  if (!mgr) { chrome.webview.postMessage('testresult|highlight:no-uimanager'); return; }
  mgr.highlightSelection('selftest');
  await sleep(1200);
  var n = app.pdfDocument.annotationStorage.size;
  chrome.webview.postMessage('testresult|highlight:size=' + n + ';hasChanges=' + app._hasChanges() + ';dirty=' + (window.__hostDirty ? 1 : 0) + ';title=' + document.title);
})();";

        const string ReopenTestJs = @"
(async function () {
  var app = window.PDFViewerApplication;
  try {
    var p = await app.pdfDocument.getPage(1);
    var a = await p.getAnnotations();
    chrome.webview.postMessage('testresult|reopen:' + a.length + ':' + a.map(function (x) { return x.subtype; }).join(','));
  } catch (e) { chrome.webview.postMessage('testresult|reopen:error:' + e); }
})();";

        async Task SetupConsoleCapture()
        {
            await Core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            await Core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            Core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived +=
                delegate(object o, CoreWebView2DevToolsProtocolEventReceivedEventArgs ev) { Form.SelftestLog("EXCEPTION " + Util.Truncate(ev.ParameterObjectAsJson, 2000)); };
            Core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived +=
                delegate(object o, CoreWebView2DevToolsProtocolEventReceivedEventArgs ev)
                {
                    string j = ev.ParameterObjectAsJson;
                    if (j.Contains("\"type\":\"error\"") || j.Contains("\"type\":\"warning\"")) Form.SelftestLog("CONSOLE " + Util.Truncate(j, 2000));
                };
            Core.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived +=
                delegate(object o, CoreWebView2DevToolsProtocolEventReceivedEventArgs ev) { Form.SelftestLog("LOG " + Util.Truncate(ev.ParameterObjectAsJson, 2000)); };
        }

        public Task CaptureShotPublic(string name)
        {
            return CaptureShot(name);
        }

        async Task CaptureShot(string name)
        {
            using (var fs = new FileStream(Path.Combine(Form.SelftestDir, name), FileMode.Create))
            {
                await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
            Form.SelftestLog("CAPTURED " + name);
        }

        async void OnSelftestReady()
        {
            try
            {
                if (selftestStep == 0)
                {
                    selftestStep = 1;
                    Form.SelftestLog("READY step1 title=" + Core.DocumentTitle);
                    await Task.Delay(1500);
                    await CaptureShot("01_open.png");
                    await Core.ExecuteScriptAsync(HighlightTestJs);
                }
                else if (selftestStep == 2)
                {
                    selftestStep = 3;
                    Form.SelftestLog("READY step3 (reopened) title=" + Core.DocumentTitle);
                    await Task.Delay(1500);
                    await CaptureShot("02_reopened.png");
                    await Core.ExecuteScriptAsync(ReopenTestJs);
                }
            }
            catch (Exception ex) { Form.SelftestFail("ready step error: " + ex); }
        }

        void OnSelftestResult(string arg)
        {
            Form.SelftestLog("RESULT " + arg);
            if (selftestStep == 1)
            {
                if (arg.StartsWith("highlight:size=") && !arg.StartsWith("highlight:size=0;"))
                    RequestSave();
                else
                    Form.SelftestFail("highlight failed: " + arg);
            }
            else if (selftestStep == 3)
            {
                bool ok = arg.StartsWith("reopen:") && !arg.StartsWith("reopen:0") && !arg.Contains("error");
                Form.SelftestFinish(ok);
            }
        }

        void OnSelftestSaved(string target)
        {
            try { Form.SelftestLog("SAVED " + target + " bytes=" + new FileInfo(target).Length + " dirty=" + Dirty); } catch { }
            selftestStep = 2;
            NavigateTo(target);
        }
    }

    // 간단한 설정: %LOCALAPPDATA%\PDF뷰어\settings.txt, 한 줄에 "키=값"
    static class Settings
    {
        static readonly object sync = new object();
        static string FilePath { get { return Path.Combine(MainForm.DataDir, "settings.txt"); } }

        static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }
            return d;
        }

        public static string Get(string key, string fallback)
        {
            lock (sync)
            {
                string v;
                return Load().TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static void Set(string key, string value)
        {
            lock (sync)
            {
                try
                {
                    var d = Load();
                    d[key] = value;
                    Directory.CreateDirectory(MainForm.DataDir);
                    File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + kv.Value).ToArray(), Encoding.UTF8);
                }
                catch (Exception ex) { Program.Log("settings: " + ex.Message); }
            }
        }
    }

    // 프로젝트 공지: 프로젝트 사이트의 정적 파일 notice.json 하나를 시작할 때 한 번 받는다.
    //   {} 이면 아무것도 안 보임. 필드: id, text, text_ko, url(https만), label, label_ko, kind(info|update|sponsor), until(YYYY-MM-DD)
    //   계정·쿠키·식별자 없음. 4초 안에 안 오면 포기. 설정 notice=0 이면 아예 요청하지 않음. notice_url= 로 주소를 바꿀 수 있음.
    static class Notice
    {
        public const string ProjectUrl = "https://github.com/taeheekim2024-star/pikapdf";
        const string DefaultUrl = "https://taeheekim2024-star.github.io/pikapdf/notice.json";
        static Task<string> task;
        static readonly object sync = new object();

        public static bool Enabled
        {
            get { return Settings.Get("notice", "1") != "0"; }
            set { Settings.Set("notice", value ? "1" : "0"); }
        }

        public static string Url { get { return Settings.Get("notice_url", DefaultUrl); } }

        // 한 번만 요청하고 결과를 나눠 쓴다. 자체 시험에서는 시험 폴더의 notice.json 을 대신 쓴다 (네트워크 없이 화면 확인).
        public static Task<string> Fetch(string selftestDir)
        {
            lock (sync)
            {
                if (task != null) return task;
                if (selftestDir != null)
                {
                    string local = Path.Combine(selftestDir, "notice.json");
                    string text = File.Exists(local) ? File.ReadAllText(local, Encoding.UTF8) : "{}";
                    task = Task.FromResult(Sanitize(text));
                    return task;
                }
                if (!Enabled)
                {
                    task = Task.FromResult("{}");
                    return task;
                }
                string url = Url;
                task = Task.Run(new Func<string>(delegate
                {
                    try
                    {
                        using (var client = new System.Net.Http.HttpClient())
                        {
                            client.Timeout = TimeSpan.FromSeconds(4);
                            client.DefaultRequestHeaders.UserAgent.ParseAdd("PdfViewer/" + MainForm.AppVersion);
                            var resp = client.GetAsync(url).Result;
                            if (!resp.IsSuccessStatusCode) return "{}";
                            var bytes = resp.Content.ReadAsByteArrayAsync().Result;
                            if (bytes.Length > 8192) return "{}";
                            return Sanitize(Encoding.UTF8.GetString(bytes));
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.Log("notice: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                        return "{}";
                    }
                }));
                return task;
            }
        }

        // JSON 객체처럼 생긴 것만 통과시킨다 (파싱은 시작 화면 쪽 스크립트가 한다)
        static string Sanitize(string text)
        {
            if (text == null) return "{}";
            string s = text.Trim().TrimStart('﻿');
            if (!s.StartsWith("{") || !s.EndsWith("}")) return "{}";
            return s;
        }
    }

    // 최근 문서 목록 (시작 화면용): %LOCALAPPDATA%\PDF뷰어\recent.txt, 한 줄에 "경로|시각"
    static class RecentList
    {
        static readonly object sync = new object();
        static string FilePath { get { return Path.Combine(MainForm.DataDir, "recent.txt"); } }

        public static void Add(string path)
        {
            try
            {
                lock (sync)
                {
                    var lines = new List<string>();
                    if (File.Exists(FilePath)) lines.AddRange(File.ReadAllLines(FilePath, Encoding.UTF8));
                    lines.RemoveAll(l => string.Equals(l.Split('|')[0], path, StringComparison.OrdinalIgnoreCase));
                    lines.Insert(0, path + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                    if (lines.Count > 20) lines.RemoveRange(20, lines.Count - 20);
                    Directory.CreateDirectory(MainForm.DataDir);
                    File.WriteAllLines(FilePath, lines, Encoding.UTF8);
                }
            }
            catch (Exception ex) { Program.Log("recent: " + ex.Message); }
        }

        public static string ToJson()
        {
            var sb = new StringBuilder("[");
            try
            {
                string[] lines;
                lock (sync) { lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath, Encoding.UTF8) : new string[0]; }
                int n = 0;
                foreach (var line in lines)
                {
                    var parts = line.Split('|');
                    string p = parts[0];
                    if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                    if (n++ > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(Util.JsonStr(Path.GetFileName(p)))
                      .Append(",\"dir\":").Append(Util.JsonStr(Path.GetDirectoryName(p) ?? ""))
                      .Append(",\"path\":").Append(Util.JsonStr(p))
                      .Append(",\"when\":").Append(Util.JsonStr(parts.Length > 1 ? parts[1] : ""))
                      .Append('}');
                    if (n >= 12) break;
                }
            }
            catch (Exception ex) { Program.Log("recent json: " + ex.Message); }
            return sb.Append(']').ToString();
        }
    }

    // ===================================================================================
    //  진행 창
    // ===================================================================================
    class ProgressForm : Form
    {
        readonly Label label;
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();
        bool finished;

        public ProgressForm(string title, string message, bool cancellable)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Malgun Gothic", 9F);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, cancellable ? 130 : 96);

            label = new Label();
            label.Location = new Point(16, 14);
            label.Size = new Size(388, 40);
            label.Text = message;
            Controls.Add(label);

            var bar = new ProgressBar();
            bar.Location = new Point(16, 58);
            bar.Size = new Size(388, 18);
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;
            Controls.Add(bar);

            if (cancellable)
            {
                var cancel = new Button();
                cancel.Text = "취소";
                cancel.Size = new Size(84, 28);
                cancel.Location = new Point(320, 88);
                cancel.Click += delegate { cancel.Enabled = false; label.Text = "취소하는 중…"; Cts.Cancel(); };
                Controls.Add(cancel);
                CancelButton = cancel;
            }
        }

        public void SetMessage(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)delegate { SetMessage(s); }); return; }
            label.Text = s;
        }

        public void Finish()
        {
            if (IsDisposed || finished) return;
            if (InvokeRequired) { BeginInvoke((Action)Finish); return; }
            finished = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ===================================================================================
    //  작은 대화상자들
    // ===================================================================================
    class DialogBase : Form
    {
        protected readonly Button okButton = new Button();
        protected readonly Button cancelButton = new Button();

        protected DialogBase(string title, int width, int height)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Malgun Gothic", 9F);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(width, height);
            okButton.Text = "확인";
            okButton.Size = new Size(84, 28);
            okButton.Location = new Point(width - 184, height - 40);
            okButton.DialogResult = DialogResult.OK;
            cancelButton.Text = "취소";
            cancelButton.Size = new Size(84, 28);
            cancelButton.Location = new Point(width - 94, height - 40);
            cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        protected Label AddLabel(string text, int x, int y, int w, int h, bool emphasize = false)
        {
            var l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, h);
            if (emphasize) l.ForeColor = Color.FromArgb(0xB0, 0x30, 0x20);
            Controls.Add(l);
            return l;
        }

        protected RadioButton AddRadio(string text, int x, int y, int w, bool isChecked)
        {
            var r = new RadioButton();
            r.Text = text;
            r.Location = new Point(x, y);
            r.Size = new Size(w, 22);
            r.Checked = isChecked;
            Controls.Add(r);
            return r;
        }

        protected CheckBox AddCheck(string text, int x, int y, int w, bool isChecked)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Location = new Point(x, y);
            c.Size = new Size(w, 22);
            c.Checked = isChecked;
            Controls.Add(c);
            return c;
        }

        protected TextBox AddText(int x, int y, int w)
        {
            var t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, 23);
            Controls.Add(t);
            return t;
        }

        // 확인을 눌렀을 때 입력 검사. 문제가 있으면 메시지를 돌려주고 닫히지 않는다.
        protected virtual string ValidateInput() { return null; }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                string err = ValidateInput();
                if (err != null)
                {
                    MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    return;
                }
            }
            base.OnFormClosing(e);
        }
    }

    // 대상 페이지(현재/전체/범위) 고르기
    class PageScope
    {
        public readonly RadioButton Current, All, Range;
        public readonly TextBox RangeText;
        readonly int currentPage, pageCount;

        public PageScope(DialogBase dlg, int y, int currentPage, int pageCount, Func<string, int, int, int, bool, RadioButton> addRadio, Func<int, int, int, TextBox> addText)
        {
            this.currentPage = currentPage;
            this.pageCount = pageCount;
            Current = addRadio(string.Format("현재 페이지 ({0}쪽)", currentPage), 24, y, 200, true);
            All = addRadio(string.Format("모든 페이지 (1~{0}쪽)", pageCount), 24, y + 26, 200, false);
            Range = addRadio("범위:", 24, y + 52, 70, false);
            RangeText = addText(96, y + 52, 160);
            RangeText.Text = "";
            RangeText.GotFocus += delegate { Range.Checked = true; };
        }

        public List<int> Pages
        {
            get
            {
                if (Current.Checked) return new List<int> { currentPage };
                if (All.Checked) return Enumerable.Range(1, pageCount).ToList();
                return Tools.ParsePageRange(RangeText.Text, pageCount);
            }
        }

        public string Validate()
        {
            if (!Range.Checked) return null;
            try { Tools.ParsePageRange(RangeText.Text, pageCount); return null; }
            catch (Exception ex) { return ex.Message; }
        }
    }

    class RotateDialog : DialogBase
    {
        readonly PageScope scope;
        readonly RadioButton cw, ccw, half;

        public RotateDialog(int currentPage, int pageCount) : base("페이지 회전 저장", 380, 268)
        {
            AddLabel("회전할 페이지", 16, 12, 200, 20);
            scope = new PageScope(this, 34, currentPage, pageCount, AddRadio, AddText);
            AddLabel("방향", 16, 122, 200, 20);
            cw = AddRadio("시계 방향 90°", 24, 144, 150, true);
            ccw = AddRadio("반시계 방향 90°", 24, 170, 150, false);
            half = AddRadio("180°", 24, 196, 150, false);
            AddLabel("회전은 파일에 저장되며(주석·책갈피 유지), 다른 뷰어에서도 돌아간 채로 보입니다.", 190, 144, 180, 60);
        }

        public List<int> Pages { get { return scope.Pages; } }
        public int Delta { get { return cw.Checked ? 90 : (ccw.Checked ? 270 : 180); } }
        protected override string ValidateInput() { return scope.Validate(); }
    }

    class CompressDialog : DialogBase
    {
        readonly RadioButton ebook, screen, printer;
        readonly CheckBox overwrite;

        public CompressDialog() : base("용량 줄이기", 420, 262)
        {
            AddLabel("품질", 16, 12, 200, 20);
            ebook = AddRadio("전자책용 (150 dpi, 권장)", 24, 34, 300, true);
            screen = AddRadio("화면용 (72 dpi, 가장 작게)", 24, 60, 300, false);
            printer = AddRadio("인쇄용 (300 dpi)", 24, 86, 300, false);
            overwrite = AddCheck("원본 파일을 덮어쓰기 (덮어쓰기 전 원본은 자동 백업)", 24, 120, 380, false);
            AddLabel("주의: Ghostscript가 문서를 다시 쓰는 과정에서 형광펜·메모 등 주석이 사라집니다. " +
                     "주석이 있는 문서는 새 파일로 저장하고 원본을 보관하세요.", 16, 150, 390, 60, true);
        }

        public string Preset { get { return screen.Checked ? "/screen" : (printer.Checked ? "/printer" : "/ebook"); } }
        public bool Overwrite { get { return overwrite.Checked; } }
    }

    class PagesToImagesDialog : DialogBase
    {
        readonly PageScope scope;
        readonly RadioButton png, jpg;
        readonly ComboBox dpi;

        public PagesToImagesDialog(int currentPage, int pageCount) : base("PDF → 이미지", 380, 268)
        {
            AddLabel("페이지", 16, 12, 200, 20);
            scope = new PageScope(this, 34, currentPage, pageCount, AddRadio, AddText);
            AddLabel("형식", 16, 122, 100, 20);
            png = AddRadio("PNG", 24, 144, 70, true);
            jpg = AddRadio("JPG", 100, 144, 70, false);
            AddLabel("해상도", 190, 122, 100, 20);
            dpi = new ComboBox();
            dpi.DropDownStyle = ComboBoxStyle.DropDownList;
            dpi.Items.AddRange(new object[] { "100 dpi", "150 dpi", "200 dpi", "300 dpi" });
            dpi.SelectedIndex = 1;
            dpi.Location = new Point(196, 144);
            dpi.Size = new Size(120, 23);
            Controls.Add(dpi);
            AddLabel("문서 옆 \"<이름>_페이지이미지\" 폴더에 쪽마다 한 장씩 저장합니다.", 16, 180, 350, 40);
        }

        public string ToJson()
        {
            int d = int.Parse(((string)dpi.SelectedItem).Split(' ')[0]);
            var pages = scope.Pages;
            var sb = new StringBuilder();
            sb.Append("{\"format\":\"").Append(jpg.Checked ? "jpeg" : "png").Append("\",\"dpi\":").Append(d).Append(",\"pages\":[");
            for (int i = 0; i < pages.Count; i++) { if (i > 0) sb.Append(','); sb.Append(pages[i]); }
            sb.Append("]}");
            return sb.ToString();
        }

        protected override string ValidateInput() { return scope.Validate(); }
    }

    class PasswordDialog : DialogBase
    {
        readonly TextBox box;

        public PasswordDialog() : base("잠금(암호) 해제", 400, 170)
        {
            AddLabel("문서를 여는 암호 (모르면 비워 두세요)", 16, 14, 360, 20);
            box = AddText(16, 38, 368);
            box.UseSystemPasswordChar = true;
            AddLabel("열람 암호가 없고 인쇄·복사만 막힌 문서는 암호 없이 풀립니다. " +
                     "결과는 새 파일로 저장되며, 주석은 빠질 수 있습니다.", 16, 68, 368, 50, true);
        }

        public string Password { get { return box.Text; } }
    }

    // ===================================================================================
    //  도구: Ghostscript, Office 변환, 페이지 범위
    // ===================================================================================
    static class Tools
    {
        public static string FindGs()
        {
            foreach (var p in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PDF24", "gs", "bin", "gswin64c.exe"),
                @"C:\Program Files\PDF24\gs\bin\gswin64c.exe",
                @"C:\Program Files (x86)\PDF24\gs\bin\gswin64c.exe" })
            {
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string TempDir
        {
            get
            {
                string d = Path.Combine(Path.GetTempPath(), "PdfViewerTools");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string TempFile(string prefix, string ext)
        {
            return Path.Combine(TempDir, prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ext);
        }

        // 외부 프로그램이 한글 경로·네트워크 경로에서 말썽을 부리지 않도록 영문 임시 경로로 복사
        public static string StageInput(string src)
        {
            string tmp = TempFile("in", Path.GetExtension(src));
            File.Copy(src, tmp, true);
            return tmp;
        }

        public static Task<string> GsRunAsync(string gs, string args, CancellationToken ct)
        {
            return Task.Run(new Func<string>(delegate
            {
                var psi = new ProcessStartInfo(gs, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    var so = p.StandardOutput.ReadToEndAsync();
                    var se = p.StandardError.ReadToEndAsync();
                    while (!p.WaitForExit(200))
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try { p.Kill(); } catch { }
                            throw new OperationCanceledException();
                        }
                    }
                    string output = so.Result + se.Result;
                    if (p.ExitCode != 0)
                        throw new Exception("Ghostscript 오류 (코드 " + p.ExitCode + ")\n" + Util.Truncate(output.Trim(), 600));
                    return output;
                }
            }));
        }

        static void CheckPdf(string path, string what)
        {
            if (!File.Exists(path)) throw new Exception(what + " 파일이 만들어지지 않았습니다");
            var fi = new FileInfo(path);
            if (fi.Length < 100) throw new Exception(what + " 파일이 비어 있습니다");
            using (var fs = fi.OpenRead())
            {
                var head = new byte[4];
                fs.Read(head, 0, 4);
                if (head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F')
                    throw new Exception(what + " 파일이 PDF가 아닙니다");
            }
        }

        const string GsCommon = "-q -dNOPAUSE -dBATCH -dSAFER -sDEVICE=pdfwrite -dAutoRotatePages=/None -dPreserveAnnots=true";

        public static async Task<string> GsCompressAsync(string gs, string input, string preset, CancellationToken ct)
        {
            string tmpIn = StageInput(input);
            string tmpOut = TempFile("compressed", ".pdf");
            try
            {
                string args = string.Format("{0} -dCompatibilityLevel=1.5 -dPDFSETTINGS={1} -dDetectDuplicateImages=true -sOutputFile=\"{2}\" \"{3}\"",
                    GsCommon, preset, tmpOut, tmpIn);
                await GsRunAsync(gs, args, ct);
                CheckPdf(tmpOut, "압축 결과");
                return tmpOut;
            }
            catch (Exception)
            {
                try { File.Delete(tmpOut); } catch { }
                throw;
            }
            finally { try { File.Delete(tmpIn); } catch { } }
        }

        public static async Task<string> GsUnlockAsync(string gs, string input, string password, CancellationToken ct)
        {
            string tmpIn = StageInput(input);
            string tmpOut = TempFile("unlocked", ".pdf");
            try
            {
                string pw = string.IsNullOrEmpty(password) ? "" : " -sPDFPassword=\"" + password.Replace("\"", "") + "\"";
                string args = string.Format("{0}{1} -sOutputFile=\"{2}\" \"{3}\"", GsCommon, pw, tmpOut, tmpIn);
                string output = await GsRunAsync(gs, args, ct);
                if (output.IndexOf("No pages will be processed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    output.IndexOf("Password", StringComparison.OrdinalIgnoreCase) >= 0 && output.IndexOf("incorrect", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { File.Delete(tmpOut); } catch { }
                    throw new Exception("암호가 틀렸거나 풀 수 없는 문서입니다.\n" + Util.Truncate(output.Trim(), 300));
                }
                CheckPdf(tmpOut, "잠금 해제 결과");
                return tmpOut;
            }
            catch (Exception)
            {
                try { File.Delete(tmpOut); } catch { }
                throw;
            }
            finally { try { File.Delete(tmpIn); } catch { } }
        }

        // 자체 시험용: 암호 걸린 PDF 만들기
        public static async Task<string> GsEncryptAsync(string gs, string input, string userPw, string ownerPw, CancellationToken ct)
        {
            string tmpIn = StageInput(input);
            string tmpOut = TempFile("encrypted", ".pdf");
            try
            {
                string args = string.Format("{0} -sOwnerPassword={1} -sUserPassword={2} -dEncryptionR=3 -dKeyLength=128 -sOutputFile=\"{3}\" \"{4}\"",
                    GsCommon, ownerPw, userPw, tmpOut, tmpIn);
                await GsRunAsync(gs, args, ct);
                CheckPdf(tmpOut, "암호화 결과");
                return tmpOut;
            }
            finally { try { File.Delete(tmpIn); } catch { } }
        }

        public static Task RunStaAsync(Action work)
        {
            var tcs = new TaskCompletionSource<bool>();
            var t = new Thread(delegate ()
            {
                try { work(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }

        public static Task PdfToWordAsync(string pdf, string docx)
        {
            return RunStaAsync(delegate { Office.PdfToWord(pdf, docx); });
        }

        public static Task ConvertToPdfAsync(string src, string pdf)
        {
            return RunStaAsync(delegate { Office.ConvertToPdf(src, pdf); });
        }

        // "1-3, 5, 8-10" → [1,2,3,5,8,9,10]
        public static List<int> ParsePageRange(string text, int count)
        {
            var result = new List<int>();
            if (text == null) text = "";
            foreach (var raw in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string part = raw.Trim();
                int dash = part.IndexOf('-');
                int a, b;
                if (dash > 0)
                {
                    if (!int.TryParse(part.Substring(0, dash), out a) || !int.TryParse(part.Substring(dash + 1), out b))
                        throw new Exception("범위 형식이 잘못되었습니다: " + part + "\n예) 1-3, 5, 8-10");
                }
                else
                {
                    if (!int.TryParse(part, out a)) throw new Exception("숫자가 아닙니다: " + part + "\n예) 1-3, 5, 8-10");
                    b = a;
                }
                if (a > b) { int t = a; a = b; b = t; }
                if (a < 1 || b > count) throw new Exception(string.Format("페이지는 1~{0} 사이여야 합니다: {1}", count, part));
                for (int i = a; i <= b; i++) if (!result.Contains(i)) result.Add(i);
            }
            if (result.Count == 0) throw new Exception("페이지 범위를 입력해 주세요. 예) 1-3, 5, 8-10");
            return result;
        }

        public static string FmtSize(long bytes)
        {
            if (bytes >= 1L << 20) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            if (bytes >= 1L << 10) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
    }

    // ===================================================================================
    //  Office·한컴 COM 자동화 (창 없이 변환). 새 인스턴스를 만들어 쓰고 끝나면 닫는다.
    //  PowerPoint는 한 번에 하나만 뜨는 프로그램이라, 이미 떠 있으면 그 인스턴스를 빌려 쓰고 닫지 않는다.
    // ===================================================================================
    static class Office
    {
        public static bool IsAvailable(string progId)
        {
            if (progId == null) return false;
            try { return Type.GetTypeFromProgID(progId) != null; } catch { return false; }
        }

        public static string ProgIdFor(string src)
        {
            switch (Path.GetExtension(src).ToLowerInvariant())
            {
                case ".doc": case ".docx": case ".docm": case ".dot": case ".dotx": case ".rtf": case ".odt": case ".txt": case ".wps": return "Word.Application";
                case ".xls": case ".xlsx": case ".xlsm": case ".xlsb": case ".csv": case ".ods": return "Excel.Application";
                case ".ppt": case ".pptx": case ".pptm": case ".pps": case ".ppsx": case ".odp": return "PowerPoint.Application";
                case ".hwp": case ".hwpx": case ".hwt": return "HWPFrame.HwpObject";
                default: return null;
            }
        }

        public static string AppFor(string src)
        {
            switch (ProgIdFor(src))
            {
                case "Word.Application": return "Word";
                case "Excel.Application": return "Excel";
                case "PowerPoint.Application": return "PowerPoint";
                case "HWPFrame.HwpObject": return "한글";
                default: return null;
            }
        }

        static object Create(string progId)
        {
            Type t = Type.GetTypeFromProgID(progId);
            if (t == null) throw new Exception(progId + " 이(가) 설치되어 있지 않습니다");
            return Activator.CreateInstance(t);
        }

        static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        static void Quit(dynamic app)
        {
            try { app.Quit(); } catch { }
            Release((object)app);
        }

        public static void ConvertToPdf(string src, string pdf)
        {
            switch (ProgIdFor(src))
            {
                case "Word.Application": WordToPdf(src, pdf); break;
                case "Excel.Application": ExcelToPdf(src, pdf); break;
                case "PowerPoint.Application": PowerPointToPdf(src, pdf); break;
                case "HWPFrame.HwpObject": HwpToPdf(src, pdf); break;
                default: throw new Exception("지원하지 않는 형식입니다: " + Path.GetExtension(src));
            }
            if (!File.Exists(pdf)) throw new Exception("PDF가 만들어지지 않았습니다");
        }

        static void WordToPdf(string src, string pdf)
        {
            dynamic word = Create("Word.Application");
            try
            {
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic doc = word.Documents.Open(src, false, true, false); // 파일, 변환확인, 읽기전용, 최근문서추가
                try { doc.ExportAsFixedFormat(pdf, 17); }                 // 17 = wdExportFormatPDF
                finally { doc.Close(0); }
            }
            finally { Quit(word); }
        }

        public static void PdfToWord(string pdf, string docx)
        {
            dynamic word = Create("Word.Application");
            try
            {
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic doc = word.Documents.Open(pdf, false, true, false);
                try { doc.SaveAs2(docx, 16); }                            // 16 = wdFormatXMLDocument (docx)
                finally { doc.Close(0); }
            }
            finally { Quit(word); }
        }

        static void ExcelToPdf(string src, string pdf)
        {
            dynamic xl = Create("Excel.Application");
            try
            {
                xl.Visible = false;
                xl.DisplayAlerts = false;
                dynamic wb = xl.Workbooks.Open(src, 0, true);               // 파일, 링크갱신 안 함, 읽기전용
                try { wb.ExportAsFixedFormat(0, pdf); }                     // 0 = xlTypePDF
                finally { wb.Close(false); }
            }
            finally { Quit(xl); }
        }

        static void PowerPointToPdf(string src, string pdf)
        {
            dynamic pp = null;
            bool created = false;
            try { pp = Marshal.GetActiveObject("PowerPoint.Application"); } catch { }
            if (pp == null) { pp = Create("PowerPoint.Application"); created = true; }
            try
            {
                dynamic pres = pp.Presentations.Open(src, -1, 0, 0);        // 읽기전용, 제목없음 아님, 창 없이
                try { pres.SaveAs(pdf, 32); }                               // 32 = ppSaveAsPDF
                finally { pres.Close(); }
            }
            finally
            {
                if (created) Quit(pp);
                else Release((object)pp);
            }
        }

        static void HwpToPdf(string src, string pdf)
        {
            // 한글은 네트워크 경로에서 보안 창을 띄우므로 영문 임시 폴더로 복사해 연다
            string tmpIn = Tools.StageInput(src);
            string tmpOut = Tools.TempFile("hwp", ".pdf");
            dynamic hwp = Create("HWPFrame.HwpObject");
            try
            {
                try { hwp.SetMessageBoxMode(0x11011); } catch { }           // 메시지 상자는 자동으로 '예/확인'
                try { hwp.XHwpWindows.Item(0).Visible = false; } catch { }
                string fmt = Path.GetExtension(src).ToLowerInvariant() == ".hwpx" ? "HWPX" : "HWP";
                bool ok = hwp.Open(tmpIn, fmt, "forceopen:true");
                if (!ok) throw new Exception("한글이 문서를 열지 못했습니다");
                bool ok2 = hwp.SaveAs(tmpOut, "PDF", "");
                if (!ok2) throw new Exception("한글이 PDF로 저장하지 못했습니다");
            }
            finally
            {
                Quit(hwp);
                try { File.Delete(tmpIn); } catch { }
            }
            File.Copy(tmpOut, pdf, true);
            try { File.Delete(tmpOut); } catch { }
        }
    }

    // ===================================================================================
    //  공용 도우미
    // ===================================================================================
    static class Util
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void SHAddToRecentDocs(uint uFlags, string pv);

        public static void AddToRecent(string path)
        {
            try { SHAddToRecentDocs(3, path); } catch { }
            RecentList.Add(path);
        }

        public static void OpenExternal(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return;
            string u = uri.ToLowerInvariant();
            if (!(u.StartsWith("http://") || u.StartsWith("https://") || u.StartsWith("mailto:"))) return;
            try
            {
                var psi = new ProcessStartInfo(uri);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Program.Log("external link: " + ex.Message); }
        }

        // 임시 파일에 먼저 쓰고, 원본은 백업 폴더에 복사한 뒤 바꿔치기
        public static void WriteWithBackup(string target, byte[] data)
        {
            string dir = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
            string tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(target) + ".pdf.tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            File.WriteAllBytes(tmp, data);
            try
            {
                if (File.Exists(target))
                {
                    try { Backup(target); } catch (Exception bx) { Program.Log("backup failed: " + bx.Message); }
                    try { File.Replace(tmp, target, null, true); }
                    catch
                    {
                        File.Copy(tmp, target, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, target);
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        static void Backup(string target)
        {
            string bdir = Path.Combine(MainForm.DataDir, "backup");
            Directory.CreateDirectory(bdir);
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Path.GetFileName(target);
            File.Copy(target, Path.Combine(bdir, name), true);
            // 최근 40개, 합계 1GB까지만 보관
            var files = new DirectoryInfo(bdir).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            int idx = 0;
            foreach (var f in files)
            {
                idx++;
                total += f.Length;
                if (idx > 40 || total > (1L << 30))
                {
                    try { f.Delete(); } catch { }
                }
            }
        }

        public static string MimeFor(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".mjs": case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".wasm": return "application/wasm";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".ico": return "image/x-icon";
                case ".ftl": case ".txt": return "text/plain; charset=utf-8";
                case ".ttf": return "font/ttf";
                case ".otf": return "font/otf";
                case ".woff": return "font/woff";
                case ".woff2": return "font/woff2";
                case ".pdf": return "application/pdf";
                default: return "application/octet-stream";
            }
        }

        public static string JsonResult(bool ok, string path, string error, bool cancelled)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(ok ? "true" : "false");
            sb.Append(",\"cancelled\":").Append(cancelled ? "true" : "false");
            if (path != null)
            {
                sb.Append(",\"path\":").Append(JsonStr(path));
                sb.Append(",\"name\":").Append(JsonStr(Path.GetFileName(path)));
            }
            if (error != null) sb.Append(",\"error\":").Append(JsonStr(error));
            sb.Append("}");
            return sb.ToString();
        }

        public static string JsonStr(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }

        public static string Truncate(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }
    }
}
