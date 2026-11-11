// 탐색기 우클릭 메뉴와 명령줄에서 부르는 "그림 → PDF". 뷰어 창을 띄우지 않고 변환만 한다.
//   PdfViewer.exe --img2pdf <그림>          그림마다 PDF 하나 (그림 옆에 "<이름>.pdf")
//   PdfViewer.exe --img2pdf-merge <그림>    고른 그림 전부를 PDF 하나로 ("<첫 그림> 외 N장.pdf")
//   PdfViewer.exe --img2pdf-out <결과.pdf> [--a4 | --image-size] <그림> [그림 ...]
//                                           명령줄·스크립트용: 창 없이, 준 순서대로. 종료 코드 0 = 성공, 1 = 일부 그림 빠짐, 2 = 실패
// 탐색기는 여러 파일을 골라도 파일마다 프로그램을 한 번씩 띄운다. 그래서 먼저 뜬 하나가 잠깐 기다리며
// 나머지가 보내는 경로를 모은 뒤 한꺼번에 처리한다 (이름 붙은 뮤텍스 + 파이프, 받았다는 답을 받아야 보낸 쪽이 끝난다).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PdfViewerApp
{
    static class ShellConvert
    {
        const string MutexBase = "Local\\PdfViewer.TaeheeKim.Img2Pdf.";
        const string PipeBase = "PdfViewer.TaeheeKim.Img2Pdf.";
        const int DefaultIdleMs = 600;   // 이만큼 조용하면 탐색기가 다 띄운 것으로 본다

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int dwProcessId);

        public static bool IsCommand(string[] args)
        {
            return args.Length > 0 && args[0].StartsWith("--img2pdf", StringComparison.Ordinal);
        }

        public static int Run(string[] args)
        {
            string mode = args[0];
            string queue = "", outPath = null;
            bool noOpen = false, quiet = false, fitGiven = false, a4 = false;
            int idleMs = DefaultIdleMs;
            var files = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--queue" && i + 1 < args.Length) queue = args[++i];              // 시험용: 다른 묶음과 섞이지 않게
                else if (a == "--idle" && i + 1 < args.Length) int.TryParse(args[++i], out idleMs);
                else if (a == "--no-open") noOpen = true;                                  // 만든 PDF를 뷰어로 열지 않음
                else if (a == "--quiet") quiet = true;                                     // 창·메시지 없이 (기록만)
                else if (a == "--a4") { a4 = true; fitGiven = true; }
                else if (a == "--image-size") { a4 = false; fitGiven = true; }
                else if (string.IsNullOrWhiteSpace(a)) continue;
                else if (mode == "--img2pdf-out" && outPath == null) outPath = Full(a);
                else files.Add(Full(a));
            }
            PageFit fit = fitGiven ? (a4 ? PageFit.A4 : PageFit.Image) : ImagePdf.FitSetting;

            if (mode == "--img2pdf-out") return RunDirect(outPath, files, fit);
            bool merge = mode == "--img2pdf-merge";
            if (!merge && mode != "--img2pdf") return 2;
            if (idleMs < 100) idleMs = 100;

            string key = (merge ? "merge" : "each") + (queue.Length > 0 ? "." + queue : "");
            bool created;
            using (var mutex = new Mutex(true, MutexBase + key, out created))
            {
                if (!created)
                {
                    // 먼저 뜬 쪽이 모으는 중 → 내 경로를 넘기고 끝낸다. 못 넘기면(그쪽이 막 끝났으면) 내가 직접 처리한다.
                    if (Send(PipeBase + key, files)) return 0;
                }
                else
                {
                    Collect(PipeBase + key, files, idleMs);
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
            return ProcessFiles(merge, files, fit, noOpen, quiet);
        }

        static string Full(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        // ---------- 창 없이 바로 변환 (명령줄용) ----------

        static int RunDirect(string outPath, List<string> files, PageFit fit)
        {
            if (string.IsNullOrEmpty(outPath) || files.Count == 0)
            {
                Note("사용법: PdfViewer.exe --img2pdf-out <결과.pdf> [--a4 | --image-size] <그림> [그림 ...]");
                return 2;
            }
            var failed = new List<string>();
            int pages;
            try { pages = ImagePdf.Build(files, outPath, fit, CancellationToken.None, null, failed); }
            catch (Exception ex)
            {
                Program.Log("img2pdf-out: " + ex);
                Note(ex.Message);
                return 2;
            }
            foreach (var f in failed) Note("빠짐: " + f);
            if (pages == 0) return 2;
            return failed.Count == 0 ? 0 : 1;
        }

        static void Note(string text)
        {
            Program.Log("img2pdf: " + text);
            try { Console.Error.WriteLine(text); } catch { }
        }

        // ---------- 여러 번 뜬 프로그램끼리 경로 모으기 ----------

        static bool ReadExact(PipeStream s, byte[] buf, int count, int timeoutMs)
        {
            int off = 0;
            while (off < count)
            {
                IAsyncResult ar = s.BeginRead(buf, off, count - off, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                int n = s.EndRead(ar);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        static bool Send(string pipeName, List<string> files)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    client.Connect(4000);
                    byte[] body = Encoding.UTF8.GetBytes(string.Join("\n", files));
                    byte[] head = BitConverter.GetBytes(body.Length);
                    client.Write(head, 0, 4);
                    client.Write(body, 0, body.Length);
                    client.Flush();
                    // 받았다는 답이 와야 넘긴 것으로 친다 (모으던 쪽이 그 순간 끝나 버리면 답이 안 온다)
                    var ack = new byte[1];
                    return ReadExact(client, ack, 1, 8000) && ack[0] == (byte)'K';
                }
            }
            catch (Exception ex)
            {
                Program.Log("img2pdf send: " + ex.Message);
                return false;
            }
        }

        static void Collect(string pipeName, List<string> files, int idleMs)
        {
            var sw = Stopwatch.StartNew();
            int own = files.Count;
            while (sw.ElapsedMilliseconds < 120000)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    IAsyncResult ar = server.BeginWaitForConnection(null, null);
                    // 하나만 골랐을 때는 짧게 기다리고 끝낸다. 다른 경로가 하나라도 왔으면 여러 개를 고른 것이므로
                    // (탐색기가 느리게 띄우는 경우에도 한 묶음으로 모이도록) 조금 더 넉넉히 기다린다.
                    int wait = files.Count > own ? Math.Max(idleMs, 1500) : idleMs;
                    if (!ar.AsyncWaitHandle.WaitOne(wait) && !ar.IsCompleted) break;   // 조용해졌다 → 그만 모은다
                    server.EndWaitForConnection(ar);
                    var head = new byte[4];
                    if (!ReadExact(server, head, 4, 5000)) continue;
                    int len = BitConverter.ToInt32(head, 0);
                    if (len < 0 || len > (1 << 20)) continue;
                    var body = new byte[len];
                    if (len > 0 && !ReadExact(server, body, len, 5000)) continue;
                    foreach (var line in Encoding.UTF8.GetString(body).Split('\n'))
                    {
                        string p = line.Trim();
                        if (p.Length > 0) files.Add(p);
                    }
                    server.Write(new byte[] { (byte)'K' }, 0, 1);
                    server.Flush();
                    try { server.WaitForPipeDrain(); } catch { }
                }
                catch (Exception ex)
                {
                    Program.Log("img2pdf collect: " + ex.Message);
                    Thread.Sleep(30);
                }
                finally
                {
                    if (server != null) { try { server.Dispose(); } catch { } }
                }
            }
        }

        // ---------- 변환 ----------

        static void Message(bool quiet, string text, MessageBoxIcon icon)
        {
            Program.Log("img2pdf: " + text.Replace("\r", "").Replace("\n", " / "));
            if (quiet) return;
            // 0x10000 = 앞으로 가져오기 (창이 없는 프로그램이 띄우는 메시지라 뒤에 숨지 않게)
            MessageBox.Show(text, MainForm.AppName, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, (MessageBoxOptions)0x10000);
        }

        static string FailList(List<string> failed)
        {
            var lines = failed.Take(8).ToList();
            if (failed.Count > lines.Count) lines.Add("… 외 " + (failed.Count - lines.Count) + "개");
            return string.Join("\n", lines);
        }

        static int ProcessFiles(bool merge, List<string> given, PageFit fit, bool noOpen, bool quiet)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            foreach (var f in given) if (File.Exists(f) && seen.Add(f)) files.Add(f);
            if (files.Count == 0)
            {
                Message(quiet, "PDF로 바꿀 그림 파일이 없습니다.", MessageBoxIcon.Warning);
                return 2;
            }
            files = ImagePdf.SortNatural(files);

            // 결과는 그림 옆에 둔다. 그 폴더에 쓸 수 없으면(읽기 전용 등) 저장할 폴더를 묻는다.
            var writable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string fallback = null;
            bool fallbackAsked = false;
            Func<string, string> outDirFor = delegate(string image)
            {
                string d = Path.GetDirectoryName(image);
                bool can;
                if (!writable.TryGetValue(d, out can)) { can = ImagePdf.CanWriteTo(d); writable[d] = can; }
                if (can) return d;
                if (!fallbackAsked) { fallbackAsked = true; fallback = AskFolder(quiet); }
                return fallback;
            };

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<ImageJob>();
            if (merge || files.Count == 1)
            {
                string dir = outDirFor(files[0]);
                if (dir == null) return 1;
                jobs.Add(new ImageJob(files, ImagePdf.UniquePath(Path.Combine(dir, ImagePdf.MergedName(files)), taken)));
            }
            else
            {
                foreach (var f in files)
                {
                    string dir = outDirFor(f);
                    if (dir == null) return 1;
                    jobs.Add(new ImageJob(new[] { f }, ImagePdf.UniquePath(Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + ".pdf"), taken)));
                }
            }

            ImageBatchResult res = null;
            Exception error = null;
            bool cancelled = false;
            string lastMessage = null;
            var cts = new CancellationTokenSource();
            var done = new ManualResetEvent(false);
            var worker = new Thread(delegate ()
            {
                try { res = ImagePdf.RunJobs(jobs, fit, cts.Token, delegate(string s) { lastMessage = s; }); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();

            // 금방 끝나면 아무 창도 띄우지 않는다. 오래 걸릴 때만 진행 창.
            if (!done.WaitOne(900) && !quiet)
            {
                var pf = new ProgressForm("PDF로 변환", lastMessage ?? "그림을 읽는 중…", true);
                pf.StartPosition = FormStartPosition.CenterScreen;
                pf.ShowInTaskbar = true;
                try { pf.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                pf.Cts.Token.Register(delegate { cts.Cancel(); });
                var timer = new System.Windows.Forms.Timer();
                timer.Interval = 100;
                timer.Tick += delegate
                {
                    if (done.WaitOne(0)) { timer.Stop(); pf.Finish(); }
                    else if (!pf.Cts.IsCancellationRequested && lastMessage != null) pf.SetMessage(lastMessage);
                };
                pf.Shown += delegate { timer.Start(); };
                Application.Run(pf);
                timer.Dispose();
            }
            done.WaitOne();

            if (cancelled) return 1;
            if (error != null)
            {
                Program.Log("img2pdf: " + error);
                Message(quiet, "PDF로 만들지 못했습니다.\n\n" + error.Message, MessageBoxIcon.Error);
                return 2;
            }
            if (res.Created.Count == 0)
            {
                Message(quiet, "PDF로 만들지 못했습니다.\n\n" + FailList(res.Failed), MessageBoxIcon.Error);
                return 2;
            }
            if (res.Failed.Count > 0)
                Message(quiet, "다음 그림은 읽지 못해 빠졌습니다.\n\n" + FailList(res.Failed), MessageBoxIcon.Warning);

            // PDF를 하나 만들었으면 뷰어로 보여 준다 (여러 개를 따로 만들었으면 그림 옆에 생긴 것으로 충분)
            if (!noOpen && res.Created.Count == 1) OpenInViewer(res.Created[0]);
            return res.Failed.Count == 0 ? 0 : 1;
        }

        static string AskFolder(bool quiet)
        {
            if (quiet) return null;
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "그림이 있는 폴더에는 저장할 수 없습니다. PDF를 저장할 폴더를 고르세요.";
                dlg.ShowNewFolderButton = true;
                try { dlg.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { }
                return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        static void OpenInViewer(string pdf)
        {
            try
            {
                AllowSetForegroundWindow(-1);   // 이미 떠 있는 뷰어 창이 앞으로 나올 수 있게
                var psi = new ProcessStartInfo(Application.ExecutablePath, "\"" + pdf + "\"");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex) { Program.Log("img2pdf open: " + ex.Message); }
        }
    }
}
