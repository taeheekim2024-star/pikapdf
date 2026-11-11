// 그림 → PDF.
//  - JPEG: 파일 바이트를 그대로 넣는다(DCTDecode). 다시 압축하지 않으므로 화질과 용량이 원본 그대로이고,
//          사진의 회전 정보(EXIF Orientation)는 그림을 놓는 행렬로 반영한다.
//  - PNG:  투명·인터레이스가 없으면 압축 데이터(IDAT)를 그대로 넣는다(FlateDecode + PNG 예측기).
//  - 그 밖(BMP, GIF, TIFF, WebP, HEIC, 투명 PNG ...): Windows 이미지 코덱(WIC)으로 풀어서
//          무손실 형식은 Flate로(무손실), 손실 형식은 JPEG(품질 92)로 넣는다. 투명한 부분은 흰 바탕에 합친다.
//          여러 쪽 TIFF는 쪽마다 한 장이 된다.
// 이 파일은 WPF의 이미지 형식(System.Windows.Media.Imaging = WIC)만 쓰고, 창(WinForms)은 쓰지 않는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfViewerApp
{
    // 쪽 크기: 그림 크기 그대로(그림의 해상도 정보로 환산) 또는 A4에 맞춤(가로·세로 자동)
    enum PageFit { Image, A4 }

    // 그림 묶음 하나 → PDF 하나
    class ImageJob
    {
        public readonly List<string> Images;
        public readonly string Output;
        public ImageJob(IEnumerable<string> images, string output) { Images = images.ToList(); Output = output; }
    }

    class ImageBatchResult
    {
        public readonly List<string> Created = new List<string>();   // 만든 PDF 경로
        public readonly List<string> Failed = new List<string>();    // "파일 이름: 이유"
        public int Pages;
    }

    // PDF에 넣을 그림 한 장 (이미 PDF가 이해하는 형태로 압축된 데이터)
    class PdfImage
    {
        public int Width, Height;
        public string ColorSpace;     // /DeviceRGB, /DeviceGray, /DeviceCMYK, [/Indexed /DeviceRGB n <...>]
        public int Bits = 8;          // BitsPerComponent
        public string Filter;         // /DCTDecode 또는 /FlateDecode
        public string DecodeParms;    // 없으면 null
        public string Decode;         // 없으면 null
        public byte[] Data;
        public double DpiX, DpiY;     // 0 = 모름
        public int Orientation = 1;   // EXIF 방향 1~8
        public byte[] Icc;            // 그림에 들어 있던 색 프로파일(ICC). 없으면 null → 기기 색(sRGB로 취급됨)
        public int IccComponents;     // 프로파일의 색 성분 수 (1 회색, 3 RGB, 4 CMYK)
    }

    class JpegInfo
    {
        public int Width, Height, Components, Precision, Sof;
        public bool Adobe;
        public bool RgbIds;           // 성분 이름이 R, G, B (YCbCr로 바꾸지 않고 저장한 JPEG)
        public double DpiX, DpiY;
        public int Orientation = 1;
        public bool Supported;        // PDF에 그대로 넣을 수 있는 JPEG인지
        public bool Complete;         // 압축 데이터가 끝 표식(EOI)까지 있는지 (없으면 중간에 끊긴 파일)
        public byte[] Icc;            // APP2 조각들에 나뉘어 들어 있던 색 프로파일
    }

    class PngInfo
    {
        public int Width, Height, BitDepth, ColorType, Interlace;
        public byte[] Palette;
        public bool Transparency;     // tRNS 조각이 있음
        public double DpiX, DpiY;
        public byte[] Idat;           // IDAT 조각들을 이은 것 (zlib 데이터)
        public long ExpectedLength;   // 풀었을 때 나와야 하는 길이
        public byte[] IccDeflated;    // iCCP 조각의 압축된 색 프로파일
    }

    static class ImagePdf
    {
        public static readonly string[] Extensions =
            { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif" };

        public const string DialogFilter =
            "그림 파일 (JPG, PNG, BMP, GIF, TIFF, WebP, HEIC)|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif|모든 파일 (*.*)|*.*";

        const double A4Short = 595.276, A4Long = 841.89;   // 210 × 297 mm
        const double MaxPagePt = 14400;                    // PDF 뷰어들이 받는 가장 큰 쪽 (200 인치)
        const double MinPagePt = 3;                        // 가장 작은 쪽
        const int JpegQuality = 92;

        public static bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext;
            try { ext = Path.GetExtension(path); } catch { return false; }
            return Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        // 마지막으로 고른 선택 (탐색기 우클릭 변환도 이 값을 따른다)
        public static PageFit FitSetting
        {
            get { return Settings.Get("img2pdf_fit", "image") == "a4" ? PageFit.A4 : PageFit.Image; }
            set { Settings.Set("img2pdf_fit", value == PageFit.A4 ? "a4" : "image"); }
        }

        public static bool MergeSetting
        {
            get { return Settings.Get("img2pdf_multi", "merge") != "each"; }
            set { Settings.Set("img2pdf_multi", value ? "merge" : "each"); }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        // 탐색기의 이름순과 같은 순서 (그림2 < 그림10)
        public static List<string> SortNatural(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            list.Sort(delegate(string a, string b)
            {
                int c = StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));
                return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        // 이미 있는 파일을 덮어쓰지 않도록 "이름 (2).pdf" 식으로 비켜 간다. taken은 같은 묶음에서 이미 정한 이름들.
        public static string UniquePath(string path, ICollection<string> taken)
        {
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            string candidate = path;
            for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate) || (taken != null && taken.Contains(candidate)); i++)
                candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (taken != null) taken.Add(candidate);
            return candidate;
        }

        // 합친 PDF의 이름: "<첫 그림> 외 N장.pdf" (한 장이면 "<이름>.pdf")
        public static string MergedName(IList<string> images)
        {
            string first = Path.GetFileNameWithoutExtension(images[0]);
            return images.Count > 1 ? first + " 외 " + (images.Count - 1) + "장.pdf" : first + ".pdf";
        }

        // 폴더에 파일을 만들 수 있는지 (읽기 전용 위치면 다른 곳에 저장해야 하므로)
        public static bool CanWriteTo(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".pdfviewer-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        // ---------- 작업 실행 ----------

        // 이미지 코덱(WIC)은 STA 스레드에서 쓰는 것이 안전하므로 전용 스레드에서 돌린다
        public static Task<ImageBatchResult> RunJobsAsync(IList<ImageJob> jobs, PageFit fit, CancellationToken ct, Action<string> progress)
        {
            var tcs = new TaskCompletionSource<ImageBatchResult>();
            var t = new Thread(delegate ()
            {
                try { tcs.SetResult(RunJobs(jobs, fit, ct, progress)); }
                catch (OperationCanceledException) { tcs.SetCanceled(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            return tcs.Task;
        }

        // 작업들을 차례로 처리한다. 읽지 못한 그림은 건너뛰고 Failed에 적는다. 취소되면 OperationCanceledException.
        public static ImageBatchResult RunJobs(IList<ImageJob> jobs, PageFit fit, CancellationToken ct, Action<string> progress)
        {
            var res = new ImageBatchResult();
            int total = jobs.Sum(j => j.Images.Count), done = 0;
            foreach (var job in jobs)
            {
                ct.ThrowIfCancellationRequested();
                int baseDone = done;
                Action<int, string> report = delegate(int i, string name)
                {
                    if (progress != null) progress(string.Format("{0} / {1}   {2}", baseDone + i + 1, total, name));
                };
                try
                {
                    int pages = Build(job.Images, job.Output, fit, ct, report, res.Failed);
                    if (pages > 0) { res.Created.Add(job.Output); res.Pages += pages; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Program.Log("img2pdf: " + job.Output + " : " + ex);
                    res.Failed.Add(Path.GetFileName(job.Output) + ": " + ex.Message);
                }
                done += job.Images.Count;
            }
            return res;
        }

        // 그림들을 PDF 하나로 만든다. 돌려주는 값은 쪽 수(0이면 파일을 만들지 않음). 읽지 못한 그림은 failed에 적고 건너뛴다.
        public static int Build(IList<string> images, string outPdf, PageFit fit, CancellationToken ct, Action<int, string> progress, List<string> failed)
        {
            outPdf = Path.GetFullPath(outPdf);
            // 임시 파일은 같은 폴더에 짧은 이름으로 (최종 이름보다 길어져 경로 길이 한도에 걸리는 일이 없게)
            string tmp = Path.Combine(Path.GetDirectoryName(outPdf), "~pdf" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            int pages = 0;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var w = new PdfWriter(fs);
                    int catalog = w.Reserve(), pagesObj = w.Reserve(), info = w.Reserve();
                    var kids = new List<int>();
                    for (int i = 0; i < images.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string path = images[i];
                        if (progress != null) progress(i, Path.GetFileName(path));
                        int before = pages;
                        Exception writeError = null;
                        try
                        {
                            // 그림(여러 쪽 TIFF는 쪽마다)을 풀리는 대로 바로 쪽으로 쓴다: 한 번에 한 장만 메모리에 둔다
                            LoadImage(path, ct, delegate(PdfImage img)
                            {
                                try { kids.Add(WritePage(w, pagesObj, img, fit)); }
                                catch (Exception wex) { writeError = wex; throw; }
                                pages++;
                            });
                        }
                        catch (Exception ex)
                        {
                            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                            // 그림이 아니라 PDF를 쓰다가 난 오류(디스크가 참 등)면 파일이 반쯤 쓰인 상태라 계속하지 않는다
                            if (writeError != null) throw new IOException("PDF 파일을 쓰지 못했습니다: " + writeError.Message, writeError);
                            Program.Log("img2pdf: " + path + " : " + ex.GetType().Name + " " + ex.Message);
                            if (failed != null)
                                failed.Add(Path.GetFileName(path) + ": " + Describe(ex) + (pages > before ? " (앞의 " + (pages - before) + "쪽까지만 들어갔습니다)" : ""));
                        }
                    }
                    if (pages > 0)
                    {
                        var sb = new StringBuilder("<< /Type /Pages /Count ").Append(kids.Count).Append(" /Kids [");
                        for (int i = 0; i < kids.Count; i++) sb.Append(i > 0 ? " " : "").Append(kids[i]).Append(" 0 R");
                        sb.Append("] >>");
                        w.Object(pagesObj, sb.ToString());
                        w.Object(catalog, "<< /Type /Catalog /Pages " + pagesObj + " 0 R >>");
                        w.Object(info, "<< /Producer (PikaPDF " + MainForm.AppVersion + ") /CreationDate (" + PdfDate(DateTime.Now) + ") >>");
                        w.Finish(catalog, info, MakeId(outPdf));
                    }
                }
                if (pages > 0) ReplaceFile(tmp, outPdf);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return pages;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string replacement, int flags);

        // 임시 파일을 최종 이름으로 바꾼다. 같은 폴더 안의 이름 바꾸기 한 번이라, 실패하거나 중간에 끊겨도
        // 이미 있던 파일이 반쯤 덮인 채로 남지 않는다 (네트워크 폴더에서도 같다).
        // 먼저 '그냥 이름 바꾸기'를 해 보고, 같은 이름의 파일이 이미 있을 때만 '덮어쓰는 이름 바꾸기'를 한다:
        // 덮어쓰기 표시를 붙인 호출은 대상이 없어도 한 건에 0.3~0.4초씩 걸리는 PC가 있다 (그림 100장을 따로 바꾸면 30초 넘게 차이 난다).
        static void ReplaceFile(string tmp, string target)
        {
            const int ReplaceExisting = 1, ErrorFileExists = 80, ErrorAlreadyExists = 183;
            if (MoveFileEx(tmp, target, 0)) return;
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorFileExists || err == ErrorAlreadyExists)
            {
                if (MoveFileEx(tmp, target, ReplaceExisting)) return;
                err = Marshal.GetLastWin32Error();
            }
            throw new IOException("결과 파일을 쓸 수 없습니다 (다른 프로그램이 열고 있을 수 있습니다): " + Path.GetFileName(target) + " [오류 " + err + "]");
        }

        static string Describe(Exception ex)
        {
            if (ex is NotSupportedException || ex is FileFormatException)
                return "읽을 수 없는 형식입니다 (Windows에 이 형식의 코덱이 없거나 파일이 손상되었습니다)";
            if (ex is OutOfMemoryException || ex is OverflowException) return "그림이 너무 큽니다";
            if (ex is FileNotFoundException) return "파일을 찾을 수 없습니다";
            return ex.Message;
        }

        static string PdfDate(DateTime now)
        {
            TimeSpan off = TimeZoneInfo.Local.GetUtcOffset(now);
            string sign = off < TimeSpan.Zero ? "-" : "+";
            off = off.Duration();
            return "D:" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + sign + off.Hours.ToString("00") + "'" + off.Minutes.ToString("00") + "'";
        }

        static string MakeId(string seed)
        {
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(seed + "|" + DateTime.UtcNow.Ticks + "|" + Guid.NewGuid()));
                var sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
        }

        // ---------- 쪽 쓰기 ----------

        // EXIF 방향 1~8: 저장된 그림(단위 정사각형)을 바로 선 모습으로 옮기는 행렬 [a b c d e f]
        static readonly double[][] OrientMatrix =
        {
            null,
            new double[] {  1,  0,  0,  1, 0, 0 },   // 1 그대로
            new double[] { -1,  0,  0,  1, 1, 0 },   // 2 좌우 뒤집힘
            new double[] { -1,  0,  0, -1, 1, 1 },   // 3 180° 돌아감
            new double[] {  1,  0,  0, -1, 0, 1 },   // 4 위아래 뒤집힘
            new double[] {  0, -1, -1,  0, 1, 1 },   // 5 대각선으로 뒤집힘
            new double[] {  0, -1,  1,  0, 0, 1 },   // 6 시계 방향으로 90° 돌려야 바로 섬
            new double[] {  0,  1,  1,  0, 0, 0 },   // 7 반대 대각선으로 뒤집힘
            new double[] {  0,  1, -1,  0, 1, 0 },   // 8 반시계 방향으로 90° 돌려야 바로 섬
        };

        // 해상도 정보가 없거나 말이 안 되면 96. 미터 단위로 저장된 값(95.9866, 96.012, 72.009 ...)은 가까운 정수로 맞춘다:
        // 쪽 크기가 깔끔해지고, 뷰어가 100%에서 그림을 픽셀 그대로(다시 계산하지 않고) 그릴 수 있다.
        // 현미경·분석 장비의 그림은 "1 cm에 20000픽셀" 같은 실제 축척을 해상도 칸에 적어 두기도 하는데(= 50800 dpi),
        // 그대로 믿으면 쪽이 1 mm짜리가 되므로 범위 밖의 값은 버린다.
        static double SaneDpi(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d) || d < 10 || d > 20000) return 96;
            double r = Math.Round(d);
            return (Math.Abs(d - r) / r < 0.0003) ? r : d;
        }

        static string N(double v)
        {
            string s = Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        static int WritePage(PdfWriter w, int parent, PdfImage img, PageFit fit)
        {
            int o = (img.Orientation >= 1 && img.Orientation <= 8) ? img.Orientation : 1;
            bool swap = o >= 5;   // 5~8은 가로·세로가 바뀐다
            double pxW = swap ? img.Height : img.Width, pxH = swap ? img.Width : img.Height;
            double dpiX = SaneDpi(swap ? img.DpiY : img.DpiX), dpiY = SaneDpi(swap ? img.DpiX : img.DpiY);
            double natW = pxW * 72.0 / dpiX, natH = pxH * 72.0 / dpiY;   // 그림의 원래 크기 (pt)
            // 긴 쪽이 반 인치(36 pt)도 안 되면 해상도 값이 축척 같은 다른 뜻으로 쓰인 것으로 보고 96 dpi로 다시 잡는다
            if (Math.Max(natW, natH) < 36) { natW = pxW * 72.0 / 96; natH = pxH * 72.0 / 96; }

            double pageW, pageH, drawW, drawH, x0, y0;
            if (fit == PageFit.A4)
            {
                bool landscape = natW > natH;
                pageW = landscape ? A4Long : A4Short;
                pageH = landscape ? A4Short : A4Long;
                double s = Math.Min(pageW / natW, pageH / natH);
                drawW = natW * s; drawH = natH * s;
                x0 = (pageW - drawW) / 2; y0 = (pageH - drawH) / 2;
            }
            else
            {
                // PDF 뷰어들이 받는 쪽 크기는 3 ~ 14400 pt. 아주 작은 그림은 짧은 쪽이 3 pt가 되게 키우고,
                // 아주 큰(또는 극단적으로 길쭉한) 그림은 긴 쪽이 14400 pt를 넘지 않게 줄인다.
                double s = 1.0;
                double longest = Math.Max(natW, natH), shortest = Math.Min(natW, natH);
                if (shortest * s < MinPagePt) s = MinPagePt / shortest;
                if (longest * s > MaxPagePt) s = MaxPagePt / longest;
                drawW = natW * s; drawH = natH * s;
                pageW = Math.Max(MinPagePt, drawW); pageH = Math.Max(MinPagePt, drawH);   // 둘 다 맞출 수 없는 비율이면 쪽만 3 pt로 두고 가운데에
                x0 = (pageW - drawW) / 2; y0 = (pageH - drawH) / 2;
            }

            double[] m = OrientMatrix[o];
            string cm = N(drawW * m[0]) + " " + N(drawH * m[1]) + " " + N(drawW * m[2]) + " " + N(drawH * m[3]) + " " +
                        N(x0 + drawW * m[4]) + " " + N(y0 + drawH * m[5]);

            int page = w.Reserve(), content = w.Reserve(), image = w.Reserve();

            // 그림에 색 프로파일이 들어 있었으면 PDF에도 넣는다 (아이폰의 Display P3, Adobe RGB 사진의 색이 달라지지 않게).
            // 프로파일을 모르는 뷰어는 Alternate(기기 색)로 그린다.
            string colorSpace = img.ColorSpace;
            if (img.Icc != null && img.Icc.Length > 0)
            {
                string device = img.IccComponents == 1 ? "/DeviceGray" : (img.IccComponents == 4 ? "/DeviceCMYK" : "/DeviceRGB");
                if (colorSpace == device || colorSpace.StartsWith("[/Indexed " + device + " ", StringComparison.Ordinal))
                {
                    int icc = w.Reserve();
                    byte[] packed;
                    using (var ms = new MemoryStream())
                    {
                        using (var z = new ZlibWriter(ms)) z.Write(img.Icc, 0, img.Icc.Length);
                        packed = ms.ToArray();
                    }
                    w.StreamObject(icc, "/N " + img.IccComponents + " /Alternate " + device + " /Filter /FlateDecode", packed);
                    string based = "[/ICCBased " + icc + " 0 R]";
                    colorSpace = colorSpace == device ? based : "[/Indexed " + based + colorSpace.Substring(("[/Indexed " + device).Length);
                }
            }

            var d = new StringBuilder();
            d.Append("/Type /XObject /Subtype /Image /Width ").Append(img.Width).Append(" /Height ").Append(img.Height)
             .Append(" /ColorSpace ").Append(colorSpace).Append(" /BitsPerComponent ").Append(img.Bits)
             .Append(" /Filter ").Append(img.Filter);
            if (img.DecodeParms != null) d.Append(" /DecodeParms ").Append(img.DecodeParms);
            if (img.Decode != null) d.Append(" /Decode ").Append(img.Decode);
            w.StreamObject(image, d.ToString(), img.Data);
            w.StreamObject(content, "", Encoding.ASCII.GetBytes("q\n" + cm + " cm\n/Im0 Do\nQ\n"));
            w.Object(page, "<< /Type /Page /Parent " + parent + " 0 R /MediaBox [0 0 " + N(pageW) + " " + N(pageH) + "]" +
                           " /Resources << /XObject << /Im0 " + image + " 0 R >> >> /Contents " + content + " 0 R >>");
            return page;
        }

        // ---------- 그림 읽기 ----------

        static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }

        static byte[] ReadAll(string path)
        {
            using (var fs = OpenShared(path))
            {
                if (fs.Length > int.MaxValue - 64) throw new Exception("파일이 너무 큽니다");
                var buf = new byte[(int)fs.Length];
                int off = 0;
                while (off < buf.Length)
                {
                    int n = fs.Read(buf, off, buf.Length - off);
                    if (n <= 0) throw new EndOfStreamException();
                    off += n;
                }
                return buf;
            }
        }

        // 파일 하나를 PDF용 그림으로 바꿔 emit에 넘긴다. 여러 쪽 TIFF만 여러 번 넘긴다(쪽마다 한 번).
        static void LoadImage(string path, CancellationToken ct, Action<PdfImage> emit)
        {
            var head = new byte[16];
            int got;
            using (var fs = OpenShared(path))
            {
                if (fs.Length == 0) throw new Exception("빈 파일입니다");
                got = fs.Read(head, 0, head.Length);
            }

            if (got >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            {
                byte[] data = ReadAll(path);
                JpegInfo ji = ParseJpeg(data);
                // 끝 표식이 없으면 중간에 끊긴 파일: 그대로 넣으면 아래쪽이 깨진 쪽이 조용히 만들어지므로 알린다
                if (ji != null && !ji.Complete) throw new Exception("JPEG 데이터가 중간에 끊겼습니다 (파일이 손상되었습니다)");
                if (ji != null && ji.Supported)
                {
                    // 촬영 정보(위치·기기), 미리보기, 끝 표식 뒤에 붙은 데이터를 뺀 것을 넣는다. 그림 데이터는 그대로다.
                    byte[] clean = CleanJpeg(data);
                    emit(FromJpeg(clean ?? data, ji));
                    return;
                }
                // 드문 JPEG(12비트, 산술 부호화 등)는 코덱으로 풀어서 다시 JPEG로 넣는다
                DecodeWithWic(path, head, 1, ji != null ? ji.Orientation : 0, ct, emit);
                return;
            }

            if (got >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
                head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            {
                PngInfo pi = ParsePng(ReadAll(path));   // null = 표준 구조가 아님 → 코덱에 맡긴다
                if (pi != null)
                {
                    // 코덱은 끊긴 PNG도 읽힌 데까지만 조용히 돌려주므로, 먼저 압축 데이터가 온전한지 확인한다
                    int check = ZlibCheck(pi.Idat, pi.ExpectedLength, ct);
                    if (check < 0) throw new Exception("PNG 데이터가 손상되었습니다 (중간에 끊겼거나 깨졌습니다)");
                    // 기대보다 길게 풀리면(여분 데이터) 손상은 아니지만 그대로 넣지는 않고 코덱에 맡긴다
                    PdfImage png = check == 0 ? PngPassthrough(pi) : null;
                    if (png != null) { emit(png); return; }
                }
                DecodeWithWic(path, head, 2, 0, ct, emit);
                return;
            }

            DecodeWithWic(path, head, 0, 0, ct, emit);
        }

        // ---------- 색 프로파일 ----------

        // ICC 프로파일이 멀쩡해 보이면 색 성분 수(1, 3, 4)를, 아니면 0을 돌려준다
        static int IccComponentsOf(byte[] icc)
        {
            if (icc == null || icc.Length < 132 || icc.Length > 16 * 1024 * 1024) return 0;
            if (icc[36] != (byte)'a' || icc[37] != (byte)'c' || icc[38] != (byte)'s' || icc[39] != (byte)'p') return 0;
            string space = Encoding.ASCII.GetString(icc, 16, 4);
            if (space == "RGB ") return 3;
            if (space == "GRAY") return 1;
            if (space == "CMYK") return 4;
            return 0;
        }

        // 프로파일의 성분 수가 그림과 맞을 때만 붙인다
        static void AttachIcc(PdfImage img, byte[] icc, int imageComponents)
        {
            int n = IccComponentsOf(icc);
            if (n == 0 || n != imageComponents) return;
            img.Icc = icc;
            img.IccComponents = n;
        }

        // ---------- JPEG ----------

        static PdfImage FromJpeg(byte[] data, JpegInfo ji)
        {
            var img = new PdfImage();
            img.Width = ji.Width; img.Height = ji.Height; img.Bits = 8;
            img.Filter = "/DCTDecode";
            img.Data = data;
            img.ColorSpace = ji.Components == 1 ? "/DeviceGray" : (ji.Components == 3 ? "/DeviceRGB" : "/DeviceCMYK");
            // Adobe 계열 프로그램이 쓴 CMYK JPEG는 값이 뒤집혀 저장된다
            if (ji.Components == 4 && ji.Adobe) img.Decode = "[1 0 1 0 1 0 1 0]";
            // 성분 이름이 R, G, B이고 Adobe 표식이 없는 JPEG는 YCbCr로 바꾸지 않은 것이므로, 뷰어도 바꾸지 말라고 알린다
            if (ji.Components == 3 && ji.RgbIds && !ji.Adobe) img.DecodeParms = "<< /ColorTransform 0 >>";
            img.DpiX = ji.DpiX; img.DpiY = ji.DpiY;
            img.Orientation = ji.Orientation;
            AttachIcc(img, ji.Icc, ji.Components);
            return img;
        }

        // 그림을 그리는 데 필요 없는 조각을 뺀 JPEG를 만든다: 촬영 정보(EXIF: 위치·기기·미리보기), XMP, 주석, 색 프로파일 조각
        // (프로파일은 PDF 쪽에 따로 넣는다), 그리고 끝 표식 뒤에 붙은 데이터(모션 포토의 동영상 등).
        // 압축된 그림 데이터는 한 바이트도 바꾸지 않는다. 구조가 예상과 다르면 null을 돌려주고, 그때는 원본을 그대로 쓴다.
        static byte[] CleanJpeg(byte[] d)
        {
            int n = d.Length;
            if (n < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
            using (var o = new MemoryStream(n))
            {
                o.WriteByte(0xFF); o.WriteByte(0xD8);
                int p = 2;
                while (true)
                {
                    if (p + 1 >= n) return null;
                    if (d[p] != 0xFF) { p++; continue; }          // 표식 사이에 낀 군더더기 바이트: 디코더도 건너뛰는 것이라 버린다
                    while (p + 1 < n && d[p + 1] == 0xFF) p++;   // 채움 바이트
                    if (p + 1 >= n) return null;
                    int m = d[p + 1];
                    if (m == 0xD9) { o.WriteByte(0xFF); o.WriteByte(0xD9); break; }                                   // 끝: 뒤에 붙은 것은 버린다
                    if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { o.WriteByte(0xFF); o.WriteByte((byte)m); p += 2; continue; }
                    if (m == 0x00 || m == 0xD8) { p += 2; continue; }   // 머리 부분에 있을 이유가 없는 표식: ParseJpeg와 같이 건너뛴다
                    if (p + 3 >= n) return null;
                    int len = (d[p + 2] << 8) | d[p + 3];
                    if (len < 2 || p + 2 + len > n) return null;
                    // APP1~APP15(EXIF, XMP, ICC, IPTC ...)와 주석을 뺀다. APP0(JFIF)과 APP14(Adobe: 색 처리 방식)는 남긴다.
                    bool drop = (m >= 0xE1 && m <= 0xEF && m != 0xEE) || m == 0xFE;
                    if (!drop) o.Write(d, p, 2 + len);
                    p += 2 + len;
                    if (m == 0xDA)
                    {
                        // 압축 데이터: 다음 표식(FF 뒤가 00도, 다시 시작 표식 D0~D7도 아닌 것)이 나올 때까지 그대로 복사
                        int q = p;
                        while (true)
                        {
                            if (q + 1 >= n) return null;
                            if (d[q] == 0xFF)
                            {
                                int x = d[q + 1];
                                if (x == 0x00 || (x >= 0xD0 && x <= 0xD7)) { q += 2; continue; }
                                if (x == 0xFF) { q++; continue; }
                                break;
                            }
                            q++;
                        }
                        o.Write(d, p, q - p);
                        p = q;
                    }
                }
                return o.ToArray();
            }
        }

        static int U16(byte[] d, long p, bool le)
        {
            return le ? (d[p] | (d[p + 1] << 8)) : ((d[p] << 8) | d[p + 1]);
        }

        static uint U32(byte[] d, long p, bool le)
        {
            return le ? (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24))
                      : (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
        }

        static bool Starts(byte[] d, int p, int avail, string ascii)
        {
            if (avail < ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++) if (d[p + i] != (byte)ascii[i]) return false;
            return true;
        }

        // 머리 부분의 표식만 읽는다: 크기·색 성분(SOF), 해상도(JFIF/EXIF), 회전(EXIF), Adobe 표식
        public static JpegInfo ParseJpeg(byte[] d)
        {
            if (d == null || d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
            var ji = new JpegInfo();
            double exifX = 0, exifY = 0;
            int exifUnit = 2;
            bool jfifDpi = false, sofSeen = false, exifSeen = false;
            Dictionary<int, int[]> iccParts = null;
            int iccTotal = 0;
            int p = 2, n = d.Length, sos = -1;
            while (p + 1 < n)
            {
                if (d[p] != 0xFF) { p++; continue; }
                int m = d[p + 1];
                if (m == 0xFF) { p++; continue; }                                              // 채움 바이트
                if (m == 0x00 || m == 0x01 || (m >= 0xD0 && m <= 0xD8)) { p += 2; continue; }  // 길이가 없는 표식
                if (m == 0xDA) { sos = p; break; }                                             // 압축 데이터 시작
                if (m == 0xD9) break;                                                          // 끝
                if (p + 3 >= n) break;
                int len = (d[p + 2] << 8) | d[p + 3];
                if (len < 2 || p + 2 + len > n) break;
                int s = p + 4, sl = len - 2;
                if (m == 0xE0 && sl >= 12 && Starts(d, s, sl, "JFIF\0"))
                {
                    int units = d[s + 7];
                    int xd = (d[s + 8] << 8) | d[s + 9], yd = (d[s + 10] << 8) | d[s + 11];
                    if (xd > 0 && yd > 0)
                    {
                        if (units == 1) { ji.DpiX = xd; ji.DpiY = yd; jfifDpi = true; }
                        else if (units == 2) { ji.DpiX = xd * 2.54; ji.DpiY = yd * 2.54; jfifDpi = true; }
                    }
                }
                else if (m == 0xE1 && sl >= 14 && Starts(d, s, sl, "Exif\0\0"))
                {
                    exifSeen = true;
                    ParseExif(d, s + 6, sl - 6, ji, ref exifX, ref exifY, ref exifUnit);
                }
                else if (m == 0xEE && sl >= 12 && Starts(d, s, sl, "Adobe"))
                {
                    ji.Adobe = true;
                }
                else if (m == 0xE2 && sl > 14 && Starts(d, s, sl, "ICC_PROFILE\0"))
                {
                    // 색 프로파일은 여러 조각으로 나뉘어 있을 수 있다: (조각 번호, 전체 조각 수, 데이터)
                    int seq = d[s + 12], total = d[s + 13];
                    if (iccParts == null) iccParts = new Dictionary<int, int[]>();
                    if (seq >= 1 && seq <= total && !iccParts.ContainsKey(seq)) { iccParts[seq] = new[] { s + 14, sl - 14 }; iccTotal = total; }
                }
                else if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                {
                    if (sl >= 6 && !sofSeen)
                    {
                        sofSeen = true;
                        ji.Sof = m;
                        ji.Precision = d[s];
                        ji.Height = (d[s + 1] << 8) | d[s + 2];
                        ji.Width = (d[s + 3] << 8) | d[s + 4];
                        ji.Components = d[s + 5];
                        if (ji.Components == 3 && sl >= 6 + 9)
                            ji.RgbIds = d[s + 6] == (byte)'R' && d[s + 9] == (byte)'G' && d[s + 12] == (byte)'B';
                    }
                }
                p += 2 + len;
            }
            if (iccParts != null && iccTotal > 0 && iccParts.Count == iccTotal)
            {
                int size = 0;
                for (int i = 1; i <= iccTotal; i++) size += iccParts[i][1];
                var icc = new byte[size];
                int at = 0;
                for (int i = 1; i <= iccTotal; i++) { Buffer.BlockCopy(d, iccParts[i][0], icc, at, iccParts[i][1]); at += iccParts[i][1]; }
                ji.Icc = icc;
            }
            if (!jfifDpi)
            {
                if (exifX > 0 && exifY > 0 && (exifUnit == 2 || exifUnit == 3))
                {
                    double k = exifUnit == 3 ? 2.54 : 1.0;
                    ji.DpiX = exifX * k; ji.DpiY = exifY * k;
                }
                else if (exifSeen) { ji.DpiX = 72; ji.DpiY = 72; }   // EXIF 규격의 기본 해상도
            }
            // 끝 표식(FF D9)은 압축 데이터 뒤에 있어야 한다. 앞쪽 머리(미리보기 그림)에 있는 것은 세지 않는다.
            if (sos > 0)
            {
                for (int i = n - 2; i > sos; i--)
                {
                    if (d[i] == 0xFF && d[i + 1] == 0xD9) { ji.Complete = true; break; }
                }
            }
            // PDF의 DCTDecode가 받는 것: 기본(C0)·확장(C1)·프로그레시브(C2), 8비트, 성분 1·3·4개
            ji.Supported = sofSeen && (ji.Sof == 0xC0 || ji.Sof == 0xC1 || ji.Sof == 0xC2) && ji.Precision == 8 &&
                           ji.Width > 0 && ji.Height > 0 && (ji.Components == 1 || ji.Components == 3 || ji.Components == 4);
            return ji;
        }

        static void ParseExif(byte[] d, int t, int len, JpegInfo ji, ref double xres, ref double yres, ref int unit)
        {
            if (len < 8) return;
            bool le;
            if (d[t] == (byte)'I' && d[t + 1] == (byte)'I') le = true;
            else if (d[t] == (byte)'M' && d[t + 1] == (byte)'M') le = false;
            else return;
            long end = (long)t + len;
            if (U16(d, t + 2, le) != 42) return;
            long ifd = t + (long)U32(d, t + 4, le);
            if (ifd < t || ifd + 2 > end) return;
            int count = U16(d, ifd, le);
            for (int i = 0; i < count; i++)
            {
                long e = ifd + 2 + 12L * i;
                if (e + 12 > end) break;
                int tag = U16(d, e, le), type = U16(d, e + 2, le);
                if (tag == 0x0112 && type == 3)
                {
                    int v = U16(d, e + 8, le);
                    if (v >= 1 && v <= 8) ji.Orientation = v;
                }
                else if (tag == 0x0128 && type == 3)
                {
                    unit = U16(d, e + 8, le);
                }
                else if ((tag == 0x011A || tag == 0x011B) && type == 5)
                {
                    long vp = t + (long)U32(d, e + 8, le);
                    if (vp >= t && vp + 8 <= end)
                    {
                        uint num = U32(d, vp, le), den = U32(d, vp + 4, le);
                        if (den != 0)
                        {
                            double v = (double)num / den;
                            if (tag == 0x011A) xres = v; else yres = v;
                        }
                    }
                }
            }
        }

        // ---------- PNG (압축 데이터를 그대로 넣기) ----------

        // PNG 조각들을 읽는다. 표준 구조가 아니면 null(코덱에 맡김), 조각이 파일 끝을 넘어가면 손상으로 본다.
        static PngInfo ParsePng(byte[] d)
        {
            int n = d.Length;
            var pi = new PngInfo();
            int comp = 0, filt = 0;
            bool ihdr = false;
            using (var idat = new MemoryStream())
            {
                long p = 8;
                while (p + 12 <= n)
                {
                    long clen = U32(d, p, false);
                    long ds = p + 8;
                    if (ds + clen + 4 > n) throw new Exception("PNG 파일이 중간에 끊겼습니다 (파일이 손상되었습니다)");
                    int s = (int)ds, cl = (int)clen;
                    string type = Encoding.ASCII.GetString(d, (int)p + 4, 4);
                    if (type == "IHDR" && cl >= 13)
                    {
                        pi.Width = (int)U32(d, s, false); pi.Height = (int)U32(d, s + 4, false);
                        pi.BitDepth = d[s + 8]; pi.ColorType = d[s + 9]; comp = d[s + 10]; filt = d[s + 11]; pi.Interlace = d[s + 12];
                        ihdr = true;
                    }
                    else if (type == "PLTE") { pi.Palette = new byte[cl]; Buffer.BlockCopy(d, s, pi.Palette, 0, cl); }
                    else if (type == "tRNS") pi.Transparency = true;
                    else if (type == "pHYs" && cl >= 9)
                    {
                        uint ux = U32(d, s, false), uy = U32(d, s + 4, false);
                        if (d[s + 8] == 1 && ux > 0 && uy > 0) { pi.DpiX = ux * 0.0254; pi.DpiY = uy * 0.0254; }
                    }
                    else if (type == "IDAT") idat.Write(d, s, cl);
                    else if (type == "iCCP" && pi.IccDeflated == null)
                    {
                        // 이름(1~79자) + 0 + 압축 방식(0) + zlib으로 압축한 프로파일
                        int z0 = Array.IndexOf(d, (byte)0, s, Math.Min(cl, 80));
                        if (z0 >= s && z0 + 2 < s + cl && d[z0 + 1] == 0)
                        {
                            pi.IccDeflated = new byte[s + cl - (z0 + 2)];
                            Buffer.BlockCopy(d, z0 + 2, pi.IccDeflated, 0, pi.IccDeflated.Length);
                        }
                    }
                    else if (type == "CgBI") return null;   // 애플 변형 PNG는 표준이 아님
                    else if (type == "IEND") break;
                    p = ds + clen + 4;
                }
                pi.Idat = idat.ToArray();
            }
            int w = pi.Width, h = pi.Height, bd = pi.BitDepth;
            if (!ihdr || w <= 0 || h <= 0 || comp != 0 || filt != 0 || pi.Interlace > 1) return null;
            int channels;
            switch (pi.ColorType)
            {
                case 0: channels = 1; break;
                case 2: channels = 3; break;
                case 3: channels = 1; break;
                case 4: channels = 2; break;
                case 6: channels = 4; break;
                default: return null;
            }
            if (bd != 1 && bd != 2 && bd != 4 && bd != 8 && bd != 16) return null;
            long bpp = (long)channels * bd;
            if (pi.Interlace == 0)
            {
                pi.ExpectedLength = ((w * bpp + 7) / 8 + 1) * h;
            }
            else
            {
                // Adam7: 일곱 번에 나눠 보내는 방식. 각 단계의 (시작 x, 시작 y, 간격 x, 간격 y)
                int[][] passes = { new[] { 0, 0, 8, 8 }, new[] { 4, 0, 8, 8 }, new[] { 0, 4, 4, 8 }, new[] { 2, 0, 4, 4 },
                                   new[] { 0, 2, 2, 4 }, new[] { 1, 0, 2, 2 }, new[] { 0, 1, 1, 2 } };
                long total = 0;
                foreach (var a in passes)
                {
                    long pw = (w - a[0] + a[2] - 1) / a[2], ph = (h - a[1] + a[3] - 1) / a[3];
                    if (pw > 0 && ph > 0) total += ((pw * bpp + 7) / 8 + 1) * ph;
                }
                pi.ExpectedLength = total;
            }
            return pi;
        }

        // 투명·인터레이스가 없는 PNG는 압축 데이터를 그대로 PDF에 넣을 수 있다. 안 되는 경우는 null.
        static PdfImage PngPassthrough(PngInfo pi)
        {
            if (pi.Interlace != 0 || pi.Transparency) return null;
            int bd = pi.BitDepth, colors;
            string cs;
            if (pi.ColorType == 2 && (bd == 8 || bd == 16)) { colors = 3; cs = "/DeviceRGB"; }
            else if (pi.ColorType == 0) { colors = 1; cs = "/DeviceGray"; }
            else if (pi.ColorType == 3 && bd <= 8 && pi.Palette != null && pi.Palette.Length >= 3)
            {
                colors = 1;
                int entries = Math.Min(256, pi.Palette.Length / 3);
                var sb = new StringBuilder("[/Indexed /DeviceRGB ").Append(entries - 1).Append(" <");
                for (int i = 0; i < entries * 3; i++) sb.Append(pi.Palette[i].ToString("X2"));
                cs = sb.Append(">]").ToString();
            }
            else return null;   // 투명(알파)이 있는 PNG는 풀어서 흰 바탕에 합친다

            var img = new PdfImage();
            img.Width = pi.Width; img.Height = pi.Height; img.Bits = bd;
            img.ColorSpace = cs;
            img.Filter = "/FlateDecode";
            img.DecodeParms = "<< /Predictor 15 /Colors " + colors + " /BitsPerComponent " + bd + " /Columns " + pi.Width + " >>";
            img.Data = pi.Idat;
            img.DpiX = pi.DpiX; img.DpiY = pi.DpiY;
            AttachIcc(img, Inflate(pi.IccDeflated, 16 * 1024 * 1024), colors == 3 || pi.ColorType == 3 ? 3 : 1);
            return img;
        }

        static bool ZlibHeaderOk(byte[] z)
        {
            return z != null && z.Length >= 8 && (z[0] & 0x0F) == 8 && ((z[0] << 8) | z[1]) % 31 == 0 && (z[1] & 0x20) == 0;
        }

        // zlib 데이터를 푼다 (작은 것 전용: 색 프로파일). 깨졌거나 limit보다 크면 null.
        static byte[] Inflate(byte[] z, int limit)
        {
            if (!ZlibHeaderOk(z)) return null;
            try
            {
                using (var ms = new MemoryStream(z, 2, z.Length - 2))
                using (var inf = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o = new MemoryStream())
                {
                    var buf = new byte[1 << 14];
                    int r;
                    while ((r = inf.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (o.Length + r > limit) return null;
                        o.Write(buf, 0, r);
                    }
                    return o.ToArray();
                }
            }
            catch { return null; }
        }

        // zlib 데이터를 풀어 보고 길이를 기대값과 견준다 (깨진 PNG를 그대로 넣지 않으려고).
        // 0 = 기대한 길이 그대로, 1 = 더 길게 풀림(여분 데이터가 있음), -1 = 손상(짧거나 풀다가 오류)
        static int ZlibCheck(byte[] z, long expected, CancellationToken ct)
        {
            if (!ZlibHeaderOk(z)) return -1;
            try
            {
                using (var ms = new MemoryStream(z, 2, z.Length - 2))
                using (var inf = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    var buf = new byte[1 << 16];
                    long total = 0;
                    int r, rounds = 0;
                    while ((r = inf.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += r;
                        if (total > expected) return 1;
                        if ((++rounds & 255) == 0) ct.ThrowIfCancellationRequested();
                    }
                    return total == expected ? 0 : -1;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return -1; }
        }

        // ---------- 그 밖의 형식: Windows 이미지 코덱(WIC)으로 풀기 ----------

        static bool IsWebp(byte[] head)
        {
            return head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
                   head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P';
        }

        // WebP 안의 그림이 무손실(VP8L)인지. 움직이는 WebP나 알 수 없는 구조는 손실로 본다.
        static bool WebpIsLossless(string path)
        {
            try
            {
                using (var fs = OpenShared(path))
                {
                    var b = new byte[8];
                    long pos = 12;
                    for (int guard = 0; guard < 64 && pos + 8 <= fs.Length; guard++)
                    {
                        fs.Position = pos;
                        if (fs.Read(b, 0, 8) < 8) break;
                        string cc = Encoding.ASCII.GetString(b, 0, 4);
                        long size = U32(b, 4, true);
                        if (cc == "VP8L") return true;
                        if (cc == "VP8 " || cc == "ANIM" || cc == "ANMF") return false;
                        pos += 8 + size + (size & 1);
                    }
                }
            }
            catch { }
            return false;
        }

        const long MaxPixels = 400L * 1000 * 1000;   // 한 장에 4억 화소까지 (그 이상은 메모리가 감당하기 어렵다)

        // 그림에 딸린 정보 한 칸을 읽는다 (TIFF의 꼬리표 등). 없거나 읽을 수 없으면 fallback.
        static int MetaInt(BitmapSource frame, string query, int fallback)
        {
            try
            {
                var bf = frame as BitmapFrame;
                var md = bf != null ? bf.Metadata as BitmapMetadata : null;
                object v = md != null ? md.GetQuery(query) : null;
                if (v is ushort) return (ushort)v;
                if (v is uint) return (int)(uint)v;
                if (v is byte) return (byte)v;
                if (v is int) return (int)v;
                var arr = v as ushort[];
                if (arr != null && arr.Length > 0) return arr[0];
            }
            catch { }
            return fallback;
        }

        // 코덱이 알려 주는 색 프로파일을 붙인다 (성분 수가 맞을 때만)
        static void AttachFrameIcc(PdfImage img, BitmapSource frame)
        {
            if (img.Icc != null) return;
            int comps = img.ColorSpace == "/DeviceGray" ? 1 : (img.ColorSpace == "/DeviceRGB" ? 3 : 0);
            if (comps == 0) return;
            try
            {
                var bf = frame as BitmapFrame;
                var contexts = bf != null ? bf.ColorContexts : null;
                if (contexts == null || contexts.Count == 0) return;
                using (Stream s = contexts[0].OpenProfileStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    AttachIcc(img, ms.ToArray(), comps);
                }
            }
            catch { }
        }

        // mode: 0 = 형식을 보고 정함, 1 = JPEG로, 2 = 무손실로. orientation이 1~8이면 그 방향을 쓴다(코덱으로 푸는 JPEG용).
        static void DecodeWithWic(string path, byte[] head, int mode, int orientation, CancellationToken ct, Action<PdfImage> emit)
        {
            using (var fs = OpenShared(path))
            {
                // 프레임을 미리 풀어 두지 않는다(DelayCreation, 캐시 없음): 여러 쪽 TIFF를 열 때 모든 쪽이 한꺼번에
                // 메모리에 올라가지 않도록 한 장씩 풀어서 바로 쪽으로 쓴다. 그래서 파일은 다 쓸 때까지 열어 둔다.
                BitmapDecoder dec = BitmapDecoder.Create(fs,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);
                bool tiff = dec is TiffBitmapDecoder;
                bool lossless;
                if (mode == 1) lossless = false;
                else if (mode == 2) lossless = true;
                else if (dec is PngBitmapDecoder || dec is BmpBitmapDecoder || dec is GifBitmapDecoder || tiff || dec is IconBitmapDecoder) lossless = true;
                else if (IsWebp(head)) lossless = WebpIsLossless(path);
                else lossless = false;   // HEIC, AVIF, JPEG XR, 카메라 RAW 등

                int total = dec.Frames.Count;
                if (total == 0) throw new NotSupportedException();
                var pick = new List<int>();
                if (tiff)
                {
                    // 여러 쪽 TIFF는 쪽마다 한 장. 미리보기·축소본으로 표시된 쪽(NewSubfileType의 첫 비트)은 뺀다.
                    for (int i = 0; i < total; i++)
                        if ((MetaInt(dec.Frames[i], "/ifd/{ushort=254}", 0) & 1) == 0) pick.Add(i);
                    if (pick.Count == 0) pick.Add(0);
                }
                else pick.Add(0);   // GIF의 프레임은 움직임, ICO의 프레임은 크기별 사본이라 첫 장만 쓴다

                foreach (int i in pick)
                {
                    ct.ThrowIfCancellationRequested();
                    BitmapSource frame = dec.Frames[i];
                    if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new OverflowException();
                    // TIFF의 넷째 채널이 "뜻이 정해지지 않음"(ExtraSamples = 0: 적외선 채널, 자리 채우기 등)이면 투명도로 보지 않는다
                    bool ignoreAlpha = tiff && MetaInt(frame, "/ifd/{ushort=338}", -1) == 0;
                    PdfImage img = lossless ? EncodeFlate(frame, ignoreAlpha, ct) : EncodeJpeg(frame, ignoreAlpha);
                    if (orientation >= 1 && orientation <= 8) img.Orientation = orientation;
                    else if (tiff)
                    {
                        // TIFF의 방향 꼬리표(274)는 코덱이 돌려 주지 않으므로 직접 반영한다 (HEIC는 코덱이 이미 돌려서 준다)
                        int o = MetaInt(frame, "/ifd/{ushort=274}", 1);
                        if (o >= 1 && o <= 8) img.Orientation = o;
                    }
                    AttachFrameIcc(img, frame);
                    emit(img);
                }
            }
        }

        static bool IsGray(PixelFormat f)
        {
            return f == PixelFormats.Gray8 || f == PixelFormats.Gray2 || f == PixelFormats.Gray4 || f == PixelFormats.Gray16 ||
                   f == PixelFormats.Gray32Float || f == PixelFormats.BlackWhite;
        }

        // 투명도가 있을 수 있는 형식인지. 투명도가 없다고 아는 형식만 false이고, 나머지는 있다고 보고 처리한다.
        // WebP·HEIC 같은 추가 코덱은 WPF가 모르는 형식(32비트 RGBA 등)으로 돌려주는데 그 안에 투명도가 들어 있다.
        // (모르는 형식은 PixelFormats.Default와 == 로 같다고 나오지 않으므로 "아는 불투명 형식" 쪽을 나열한다.
        //  투명도가 없는 그림을 흰 바탕에 합쳐도 결과는 그대로다.)
        static bool HasAlpha(BitmapSource src)
        {
            PixelFormat f = src.Format;
            if (f == PixelFormats.Bgr24 || f == PixelFormats.Rgb24 || f == PixelFormats.Bgr32 || f == PixelFormats.Bgr555 ||
                f == PixelFormats.Bgr565 || f == PixelFormats.Bgr101010 || f == PixelFormats.Rgb48 || f == PixelFormats.Rgb128Float ||
                f == PixelFormats.Cmyk32 || IsGray(f)) return false;
            if (f == PixelFormats.Indexed1 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed8)
            {
                try
                {
                    if (src.Palette != null)
                        foreach (Color c in src.Palette.Colors) if (c.A < 255) return true;
                }
                catch { }
                return false;
            }
            return true;
        }

        // BGRA 한 줄을 흰 바탕에 합쳐 RGB로
        static void CompositeRow(byte[] src, int so, byte[] dst, int width)
        {
            for (int x = 0, di = 0; x < width; x++, so += 4, di += 3)
            {
                int a = src[so + 3];
                if (a == 255) { dst[di] = src[so + 2]; dst[di + 1] = src[so + 1]; dst[di + 2] = src[so]; }
                else
                {
                    int inv = 255 * (255 - a);
                    dst[di] = (byte)((src[so + 2] * a + inv + 127) / 255);
                    dst[di + 1] = (byte)((src[so + 1] * a + inv + 127) / 255);
                    dst[di + 2] = (byte)((src[so] * a + inv + 127) / 255);
                }
            }
        }

        // 그림 한 장을 한 번에 푼다 (조각조각 요청하면 코덱에 따라 그때마다 처음부터 다시 풀 수 있다)
        static byte[] Grab(BitmapSource s, int stride, int h)
        {
            long total = (long)stride * h;
            if (total > int.MaxValue - 64) throw new OverflowException();
            var px = new byte[(int)total];
            s.CopyPixels(px, stride, 0);
            return px;
        }

        // 알파가 전부 0인 그림은 "완전히 투명한 그림"이 아니라 넷째 채널이 비어 있는 것이다 → 불투명으로 본다
        static bool AlphaAllZero(byte[] bgra)
        {
            if (bgra.Length < 4) return false;
            for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) return false;
            return true;
        }

        // 무손실: Flate로 다시 압축 (8비트 회색·RGB는 PNG 예측기를 써서 PNG와 비슷한 크기가 되게, 흑백 1비트는 그대로)
        static PdfImage EncodeFlate(BitmapSource src, bool ignoreAlpha, CancellationToken ct)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w <= 0 || h <= 0) throw new NotSupportedException();
            PixelFormat f = src.Format;
            var img = new PdfImage();
            img.Width = w; img.Height = h;
            img.DpiX = src.DpiX; img.DpiY = src.DpiY;
            img.Filter = "/FlateDecode";
            using (var ms = new MemoryStream())
            {
                using (var z = new ZlibWriter(ms))
                {
                    if (f == PixelFormats.BlackWhite)
                    {
                        // 1비트: 0 = 검정, 1 = 흰색 (PDF의 DeviceGray와 같음)
                        img.ColorSpace = "/DeviceGray"; img.Bits = 1;
                        byte[] px = Grab(src, (w + 7) / 8, h);
                        z.Write(px, 0, px.Length);
                    }
                    else
                    {
                        bool gray = IsGray(f);
                        bool alpha = !gray && !ignoreAlpha && HasAlpha(src);
                        PixelFormat want = gray ? PixelFormats.Gray8 : (alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24);
                        BitmapSource conv = f == want ? src : new FormatConvertedBitmap(src, want, null, 0);
                        int srcBpp = gray ? 1 : (alpha ? 4 : 3), outBpp = gray ? 1 : 3;
                        if ((long)w * srcBpp > int.MaxValue / 4) throw new OverflowException();
                        int srcStride = w * srcBpp, rowLen = w * outBpp;
                        byte[] px = Grab(conv, srcStride, h);
                        bool composite = alpha && !AlphaAllZero(px);
                        img.ColorSpace = gray ? "/DeviceGray" : "/DeviceRGB"; img.Bits = 8;
                        img.DecodeParms = "<< /Predictor 15 /Colors " + outBpp + " /BitsPerComponent 8 /Columns " + w + " >>";
                        var filter = new PngRowFilter(rowLen, outBpp, z);
                        var row = new byte[rowLen];
                        for (int y = 0; y < h; y++)
                        {
                            if ((y & 255) == 0) ct.ThrowIfCancellationRequested();
                            int so = y * srcStride;
                            if (gray) Buffer.BlockCopy(px, so, row, 0, rowLen);
                            else if (composite) CompositeRow(px, so, row, w);
                            else
                            {
                                // BGR(또는 알파를 쓰지 않는 BGRA) → RGB
                                for (int x = 0, i = so; x < rowLen; x += 3, i += srcBpp) { row[x] = px[i + 2]; row[x + 1] = px[i + 1]; row[x + 2] = px[i]; }
                            }
                            filter.WriteRow(row);
                        }
                    }
                }
                img.Data = ms.ToArray();
            }
            return img;
        }

        // 손실 형식에서 온 그림: 무손실로 넣으면 몇 배로 커지므로 JPEG(품질 92)로 넣는다
        static PdfImage EncodeJpeg(BitmapSource src, bool ignoreAlpha)
        {
            PixelFormat f = src.Format;
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w <= 0 || h <= 0) throw new NotSupportedException();
            // 먼저 메모리로 한 번에 풀어 놓고 거기서 JPEG를 만든다
            BitmapSource rgb;
            if (IsGray(f))
            {
                BitmapSource g = f == PixelFormats.Gray8 ? src : new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                rgb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, Grab(g, w, h), w);
            }
            else if (!ignoreAlpha && HasAlpha(src)) rgb = FlattenOnWhite(src);
            else
            {
                if ((long)w * 3 > int.MaxValue / 4) throw new OverflowException();
                BitmapSource c = f == PixelFormats.Bgr24 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
                rgb = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, Grab(c, w * 3, h), w * 3);
            }
            var enc = new JpegBitmapEncoder();
            enc.QualityLevel = JpegQuality;
            enc.Frames.Add(BitmapFrame.Create(rgb));
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                bytes = ms.ToArray();
            }
            JpegInfo ji = ParseJpeg(bytes);
            if (ji == null || !ji.Supported) throw new Exception("JPEG로 바꾸지 못했습니다");
            PdfImage img = FromJpeg(bytes, ji);
            img.DpiX = src.DpiX; img.DpiY = src.DpiY;
            img.Orientation = 1;
            return img;
        }

        static BitmapSource FlattenOnWhite(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            if ((long)w * h * 4 > int.MaxValue - 64) throw new OverflowException();
            BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            byte[] px = Grab(conv, w * 4, h);
            bool composite = !AlphaAllZero(px);   // 알파가 전부 0이면 넷째 채널이 비어 있는 것 → 색만 쓴다
            var bgr = new byte[w * 3 * h];
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                int so = y * w * 4, o = y * w * 3;
                if (composite)
                {
                    CompositeRow(px, so, row, w);
                    for (int x = 0; x < row.Length; x += 3) { bgr[o + x] = row[x + 2]; bgr[o + x + 1] = row[x + 1]; bgr[o + x + 2] = row[x]; }
                }
                else
                {
                    for (int x = 0, i = so; x < row.Length; x += 3, i += 4) { bgr[o + x] = px[i]; bgr[o + x + 1] = px[i + 1]; bgr[o + x + 2] = px[i + 2]; }
                }
            }
            BitmapSource result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgr, w * 3);
            result.Freeze();
            return result;
        }
    }

    // zlib 형식(머리 2바이트 + deflate + Adler-32)으로 쓰기: PDF의 FlateDecode가 받는 형식
    sealed class ZlibWriter : IDisposable
    {
        readonly Stream baseStream;
        readonly DeflateStream deflate;
        uint a = 1, b = 0;

        public ZlibWriter(Stream s)
        {
            baseStream = s;
            s.WriteByte(0x78); s.WriteByte(0xDA);
            deflate = new DeflateStream(s, CompressionLevel.Optimal, true);
        }

        public void Write(byte[] buf, int off, int len)
        {
            deflate.Write(buf, off, len);
            while (len > 0)
            {
                int k = len < 5552 ? len : 5552;
                len -= k;
                while (k-- > 0) { a += buf[off++]; b += a; }
                a %= 65521; b %= 65521;
            }
        }

        public void Dispose()
        {
            deflate.Dispose();
            uint adler = (b << 16) | a;
            baseStream.WriteByte((byte)(adler >> 24)); baseStream.WriteByte((byte)(adler >> 16));
            baseStream.WriteByte((byte)(adler >> 8)); baseStream.WriteByte((byte)adler);
        }
    }

    // PNG 예측기: 줄마다 다섯 가지(없음·왼쪽·위·평균·Paeth) 가운데 값이 가장 작아지는 것을 골라 쓴다
    sealed class PngRowFilter
    {
        readonly int n, bpp;
        readonly ZlibWriter z;
        readonly byte[] prev;
        readonly byte[][] cand = new byte[5][];

        public PngRowFilter(int rowLen, int bytesPerPixel, ZlibWriter writer)
        {
            n = rowLen; bpp = bytesPerPixel; z = writer;
            prev = new byte[n];
            for (int i = 0; i < 5; i++) { cand[i] = new byte[n + 1]; cand[i][0] = (byte)i; }
        }

        public void WriteRow(byte[] cur)
        {
            byte[] c0 = cand[0], c1 = cand[1], c2 = cand[2], c3 = cand[3], c4 = cand[4];
            long s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
            for (int i = 0; i < n; i++)
            {
                int x = cur[i];
                int left = i >= bpp ? cur[i - bpp] : 0;
                int up = prev[i];
                int upLeft = i >= bpp ? prev[i - bpp] : 0;
                int pa = up - upLeft, pb = left - upLeft, pc = pa + pb;
                if (pa < 0) pa = -pa;
                if (pb < 0) pb = -pb;
                if (pc < 0) pc = -pc;
                int paeth = (pa <= pb && pa <= pc) ? left : (pb <= pc ? up : upLeft);
                byte v0 = (byte)x, v1 = (byte)(x - left), v2 = (byte)(x - up), v3 = (byte)(x - ((left + up) >> 1)), v4 = (byte)(x - paeth);
                c0[i + 1] = v0; c1[i + 1] = v1; c2[i + 1] = v2; c3[i + 1] = v3; c4[i + 1] = v4;
                s0 += v0 < 128 ? v0 : 256 - v0;
                s1 += v1 < 128 ? v1 : 256 - v1;
                s2 += v2 < 128 ? v2 : 256 - v2;
                s3 += v3 < 128 ? v3 : 256 - v3;
                s4 += v4 < 128 ? v4 : 256 - v4;
            }
            int best = 0;
            long min = s0;
            if (s1 < min) { min = s1; best = 1; }
            if (s2 < min) { min = s2; best = 2; }
            if (s3 < min) { min = s3; best = 3; }
            if (s4 < min) { min = s4; best = 4; }
            z.Write(cand[best], 0, n + 1);
            Buffer.BlockCopy(cur, 0, prev, 0, n);
        }
    }

    // 아주 단순한 PDF 쓰기: 객체를 차례로 쓰고 끝에 교차 참조표를 붙인다
    sealed class PdfWriter
    {
        readonly Stream s;
        readonly List<long> offsets = new List<long>();

        public PdfWriter(Stream stream)
        {
            s = stream;
            Raw("%PDF-1.7\n");
            s.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);
        }

        public int Reserve()
        {
            offsets.Add(0);
            return offsets.Count;
        }

        void Raw(string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        public void Object(int num, string body)
        {
            offsets[num - 1] = s.Position;
            Raw(num + " 0 obj\n" + body + "\nendobj\n");
        }

        public void StreamObject(int num, string dictBody, byte[] data)
        {
            offsets[num - 1] = s.Position;
            Raw(num + " 0 obj\n<< " + dictBody + (dictBody.Length > 0 ? " " : "") + "/Length " + data.Length + " >>\nstream\n");
            s.Write(data, 0, data.Length);
            Raw("\nendstream\nendobj\n");
        }

        public void Finish(int root, int info, string id)
        {
            long xref = s.Position;
            // 교차 참조표의 위치 칸은 10자리다
            if (xref > 9999999999L) throw new IOException("PDF가 너무 큽니다 (10 GB를 넘음)");
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (long o in offsets) sb.Append(o.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root ").Append(root).Append(" 0 R /Info ").Append(info)
              .Append(" 0 R /ID [<").Append(id).Append("> <").Append(id).Append(">] >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            Raw(sb.ToString());
        }
    }
}
