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
    }

    class JpegInfo
    {
        public int Width, Height, Components, Precision, Sof;
        public bool Adobe;
        public double DpiX, DpiY;
        public int Orientation = 1;
        public bool Supported;        // PDF에 그대로 넣을 수 있는 JPEG인지
        public bool Complete;         // 압축 데이터가 끝 표식(EOI)까지 있는지 (없으면 중간에 끊긴 파일)
    }

    class PngInfo
    {
        public int Width, Height, BitDepth, ColorType, Interlace;
        public byte[] Palette;
        public bool Transparency;     // tRNS 조각이 있음
        public double DpiX, DpiY;
        public byte[] Idat;           // IDAT 조각들을 이은 것 (zlib 데이터)
        public long ExpectedLength;   // 풀었을 때 나와야 하는 길이
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
            string tmp = Path.Combine(Path.GetDirectoryName(outPdf), Path.GetFileName(outPdf) + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
                        List<PdfImage> frames;
                        try { frames = LoadImage(path, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Program.Log("img2pdf: " + path + " : " + ex.GetType().Name + " " + ex.Message);
                            if (failed != null) failed.Add(Path.GetFileName(path) + ": " + Describe(ex));
                            continue;
                        }
                        foreach (var img in frames)
                        {
                            kids.Add(WritePage(w, pagesObj, img, fit));
                            pages++;
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

        static void ReplaceFile(string tmp, string target)
        {
            if (File.Exists(target))
            {
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

        // 해상도 정보가 없거나 이상하면 96. 미터 단위로 저장된 값(95.9866, 96.012, 72.009 ...)은 가까운 정수로 맞춘다:
        // 쪽 크기가 깔끔해지고, 뷰어가 100%에서 그림을 픽셀 그대로(다시 계산하지 않고) 그릴 수 있다.
        static double SaneDpi(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d) || d < 1 || d > 100000) return 96;
            double r = Math.Round(d);
            return (r >= 1 && Math.Abs(d - r) / r < 0.0003) ? r : d;
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
            var d = new StringBuilder();
            d.Append("/Type /XObject /Subtype /Image /Width ").Append(img.Width).Append(" /Height ").Append(img.Height)
             .Append(" /ColorSpace ").Append(img.ColorSpace).Append(" /BitsPerComponent ").Append(img.Bits)
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

        // 파일 하나 → PDF용 그림(들). 여러 쪽 TIFF만 여러 장이 나온다.
        static List<PdfImage> LoadImage(string path, CancellationToken ct)
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
                if (ji != null && ji.Supported) return new List<PdfImage> { FromJpeg(data, ji) };
                // 드문 JPEG(12비트, 산술 부호화 등)는 코덱으로 풀어서 다시 JPEG로 넣는다
                var list = DecodeWithWic(path, head, 1, ct);
                if (ji != null) foreach (var im in list) im.Orientation = ji.Orientation;
                return list;
            }

            if (got >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
                head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            {
                PngInfo pi = ParsePng(ReadAll(path));   // null = 표준 구조가 아님 → 코덱에 맡긴다
                if (pi != null)
                {
                    // 코덱은 끊긴 PNG도 읽힌 데까지만 조용히 돌려주므로, 먼저 압축 데이터가 온전한지 확인한다
                    if (!ZlibInflatesTo(pi.Idat, pi.ExpectedLength)) throw new Exception("PNG 데이터가 손상되었습니다 (중간에 끊겼거나 깨졌습니다)");
                    PdfImage png = PngPassthrough(pi);
                    if (png != null) return new List<PdfImage> { png };
                }
                return DecodeWithWic(path, head, 2, ct);
            }

            return DecodeWithWic(path, head, 0, ct);
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
            img.DpiX = ji.DpiX; img.DpiY = ji.DpiY;
            img.Orientation = ji.Orientation;
            return img;
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
                    }
                }
                p += 2 + len;
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
            return img;
        }

        // zlib 데이터가 멀쩡하고, 풀면 기대한 길이가 나오는지 (깨진 PNG를 그대로 넣지 않으려고)
        static bool ZlibInflatesTo(byte[] z, long expected)
        {
            if (z == null || z.Length < 8) return false;
            if ((z[0] & 0x0F) != 8 || ((z[0] << 8) | z[1]) % 31 != 0 || (z[1] & 0x20) != 0) return false;
            try
            {
                using (var ms = new MemoryStream(z, 2, z.Length - 2))
                using (var inf = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    var buf = new byte[1 << 16];
                    long total = 0;
                    int r;
                    while ((r = inf.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += r;
                        if (total > expected) return false;
                    }
                    return total == expected;
                }
            }
            catch { return false; }
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

        // mode: 0 = 형식을 보고 정함, 1 = JPEG로, 2 = 무손실로
        static List<PdfImage> DecodeWithWic(string path, byte[] head, int mode, CancellationToken ct)
        {
            BitmapDecoder dec;
            using (var fs = OpenShared(path))
            {
                dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            }
            bool lossless;
            if (mode == 1) lossless = false;
            else if (mode == 2) lossless = true;
            else if (dec is PngBitmapDecoder || dec is BmpBitmapDecoder || dec is GifBitmapDecoder || dec is TiffBitmapDecoder || dec is IconBitmapDecoder) lossless = true;
            else if (IsWebp(head)) lossless = WebpIsLossless(path);
            else lossless = false;   // HEIC, AVIF, JPEG XR, 카메라 RAW 등

            // 여러 쪽 TIFF만 모든 프레임을 쓴다 (GIF의 프레임은 움직임, ICO의 프레임은 크기별 사본)
            int count = dec is TiffBitmapDecoder ? dec.Frames.Count : 1;
            if (dec.Frames.Count == 0) throw new NotSupportedException();
            var result = new List<PdfImage>();
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                BitmapSource frame = dec.Frames[i];
                result.Add(lossless ? EncodeFlate(frame, ct) : EncodeJpeg(frame));
            }
            return result;
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

        // 무손실: Flate로 다시 압축 (8비트 회색·RGB는 PNG 예측기를 써서 PNG와 비슷한 크기가 되게, 흑백 1비트는 그대로)
        static PdfImage EncodeFlate(BitmapSource src, CancellationToken ct)
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
                        int stride = (w + 7) / 8;
                        int rows = Math.Max(1, Math.Min(h, (1 << 20) / stride));
                        var buf = new byte[stride * rows];
                        for (int y = 0; y < h; y += rows)
                        {
                            ct.ThrowIfCancellationRequested();
                            int n = Math.Min(rows, h - y);
                            src.CopyPixels(new System.Windows.Int32Rect(0, y, w, n), buf, stride, 0);
                            z.Write(buf, 0, stride * n);
                        }
                    }
                    else
                    {
                        bool gray = IsGray(f);
                        bool alpha = !gray && HasAlpha(src);
                        PixelFormat want = gray ? PixelFormats.Gray8 : (alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24);
                        BitmapSource conv = f == want ? src : new FormatConvertedBitmap(src, want, null, 0);
                        int srcBpp = gray ? 1 : (alpha ? 4 : 3), outBpp = gray ? 1 : 3;
                        if ((long)w * srcBpp > int.MaxValue / 4) throw new OverflowException();
                        int srcStride = w * srcBpp, rowLen = w * outBpp;
                        img.ColorSpace = gray ? "/DeviceGray" : "/DeviceRGB"; img.Bits = 8;
                        img.DecodeParms = "<< /Predictor 15 /Colors " + outBpp + " /BitsPerComponent 8 /Columns " + w + " >>";
                        var filter = new PngRowFilter(rowLen, outBpp, z);
                        int rows = Math.Max(1, Math.Min(h, (4 << 20) / srcStride));
                        var buf = new byte[srcStride * rows];
                        var row = new byte[rowLen];
                        for (int y = 0; y < h; y += rows)
                        {
                            ct.ThrowIfCancellationRequested();
                            int n = Math.Min(rows, h - y);
                            conv.CopyPixels(new System.Windows.Int32Rect(0, y, w, n), buf, srcStride, 0);
                            for (int r = 0; r < n; r++)
                            {
                                int so = r * srcStride;
                                if (gray) Buffer.BlockCopy(buf, so, row, 0, rowLen);
                                else if (alpha) CompositeRow(buf, so, row, w);
                                else
                                {
                                    for (int x = 0, i = so; x < rowLen; x += 3, i += 3) { row[x] = buf[i + 2]; row[x + 1] = buf[i + 1]; row[x + 2] = buf[i]; }
                                }
                                filter.WriteRow(row);
                            }
                        }
                    }
                }
                img.Data = ms.ToArray();
            }
            return img;
        }

        // 손실 형식에서 온 그림: 무손실로 넣으면 몇 배로 커지므로 JPEG(품질 92)로 넣는다
        static PdfImage EncodeJpeg(BitmapSource src)
        {
            PixelFormat f = src.Format;
            BitmapSource rgb;
            if (HasAlpha(src)) rgb = FlattenOnWhite(src);
            else if (IsGray(f)) rgb = f == PixelFormats.Gray8 ? src : new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
            else rgb = f == PixelFormats.Bgr24 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
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
            var px = new byte[w * 4 * h];
            conv.CopyPixels(px, w * 4, 0);
            var bgr = new byte[w * 3 * h];
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                CompositeRow(px, y * w * 4, row, w);
                int o = y * w * 3;
                for (int x = 0; x < row.Length; x += 3) { bgr[o + x] = row[x + 2]; bgr[o + x + 1] = row[x + 1]; bgr[o + x + 2] = row[x]; }
            }
            BitmapSource result = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgr24, null, bgr, w * 3);
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
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (long o in offsets) sb.Append(o.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root ").Append(root).Append(" 0 R /Info ").Append(info)
              .Append(" 0 R /ID [<").Append(id).Append("> <").Append(id).Append(">] >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            Raw(sb.ToString());
        }
    }
}
