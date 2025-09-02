using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using JetBrains.Annotations;
using JetBrains.Metadata.Reader.API;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Navigation;
using JetBrains.ReSharper.Feature.Services.QuickDoc;
using JetBrains.ReSharper.Feature.Services.QuickDoc.Render;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Pointers;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.Util;
using JetBrains.Util.Logging;

public class ImageCommentQuickDocPresenter : IQuickDocPresenter
{
    private const bool _shouldLog = false;
    private static ILogger _logger;
    private readonly IDeclaredElementPointer<IDeclaredElement> _pointer;
    private readonly ISolution _solution;
    private readonly IPsiSourceFile _file;
    [NotNull]
    protected readonly DeclaredElementEnvoy<ITypeMember> myEnvoy;

    public ImageCommentQuickDocPresenter(IField field, ISolution solution, IPsiSourceFile file,
    [CanBeNull] IModuleReferenceResolveContext resolveContext = null,
    [CanBeNull] ISubstitution substitution = null)
    {
        _pointer = field.CreateElementPointer();
        _logger = LogManager.Instance.GetLogger(typeof(ImageCommentQuickDocPresenter));
        _solution = solution;
        _file = file;
        this.myEnvoy = new DeclaredElementEnvoy<ITypeMember>(field, substitution);
    }

    public QuickDocTitleAndText GetHtml(PsiLanguageType presentationLanguage)
    {
        TryLog("MyQuickDocPresenter GetHtml");
        var element = _pointer.FindDeclaredElement();
        if (element is not ITypeMember tm)
        {
            TryLog("MyQuickDocPresenter GetHtml 1");
            return default;
        }

        var xml = element.GetXMLDoc(true) ?? element.GetDeclarationsIn(_file).FirstOrDefault()?.GetXMLDoc(true); // XmlNode for the element’s XML-doc
        if (xml == null)
        {
            TryLog("MyQuickDocPresenter GetHtml 2");
            xml = tm.GetXMLDoc(true);
            if (xml == null)
            {
                TryLog("MyQuickDocPresenter GetHtml 3");
                return default;
            }
        }

        TryExtractImgAndSanitize(xml, out var tag, out var newXML);
        
        TryLog($"MyQuickDocPresenter GetHtml 123: {XmlUtil.OuterXmlIndented((XmlElement)newXML)}");
        TryLog("MyQuickDocPresenter GetHtml 4");

        var instance = new DeclaredElementInstance(element);

        TryLog("MyQuickDocPresenter GetHtml 5");
        // 1) Generate the SAME HTML the default provider would:
        XmlDocHtmlUtil.NavigationStyle navigationStyle = element is ICompiledElement ? XmlDocHtmlUtil.NavigationStyle.All : XmlDocHtmlUtil.NavigationStyle.Goto;
        var presenter = JetBrains.ReSharper.Resources.Shell.Shell.Instance.GetComponent<XmlDocHtmlPresenter>();
        TryLog($"MyQuickDocPresenter GetHtml 666: {presenter.GetType()}");
        var baseHtml = presenter.Run(newXML, _file.PsiModule, (DeclaredElementInstance) instance, _file.GetTheOnlyPsiFile(tm.PresentationLanguage)?.Language, navigationStyle, XmlDocHtmlUtil.CrefManager);
        TryLog("MyQuickDocPresenter GetHtml 6");
        
        var absPath = ResolveIconPath(_solution, tag.Src);

        if (absPath == null)
        {
            TryLog("MyQuickDocPresenter: Not found image path. fallbacking...");
            return new QuickDocTitleAndText(baseHtml, DeclaredElementPresenter.Format(presentationLanguage, DeclaredElementPresenter.FULL_NESTED_NAME_PRESENTER, instance));
        }
        
        var size = TryProbeSize(absPath.FullPath) ?? (96,96);
        var (w,h) = ComputeCssSize(size, tag.C, defaultTargetEdge: 96, allowUpscaleByDefault: false);

        var src = BuildImgSrc(absPath.FullPath); // chooses data: vs file:/// based on size

        var imgBlock = $"<img src='{src}' width='{w}' height='{h}'/>";
        
        TryLog($"MyQuickDocPresenter GetHtml 7: {baseHtml}:{imgBlock}");
        
        baseHtml = baseHtml.Append($"{imgBlock}");
        TryLog($"MyQuickDocPresenter GetHtml 8: {baseHtml}");

        return new QuickDocTitleAndText(baseHtml, DeclaredElementPresenter.Format(presentationLanguage, DeclaredElementPresenter.FULL_NESTED_NAME_PRESENTER, instance));
    }

    // Preserve aspect ratio, satisfy min/max; default target long-edge if none given.
    static (int W, int H) ComputeCssSize((int W, int H) intrinsic, ImgConstraints c,
                                         int defaultTargetEdge = 96, bool allowUpscaleByDefault = true)
    {
        var (iw, ih) = intrinsic.W > 0 && intrinsic.H > 0 ? intrinsic : (defaultTargetEdge, defaultTargetEdge);

        double sLower = 0.0; // min scale
        double sUpper = allowUpscaleByDefault ? double.PositiveInfinity : 1.0; // don’t upscale by default

        double longEdge = Math.Max(iw, ih);
        defaultTargetEdge = (int)longEdge;
        const double minEdge = 32;

        if (c != null)
        {
            if (c.MinEdge.HasValue) 
                sLower = Math.Max(sLower, c.MinEdge.Value / longEdge);
            else
                sLower = Math.Max(sLower, minEdge / longEdge);

            if (c.MinW.HasValue)    
                sLower = Math.Max(sLower, c.MinW.Value / (double)iw);
            else
                sLower = Math.Max(sLower, minEdge / (double)iw);
            
            if (c.MinH.HasValue)    
                sLower = Math.Max(sLower, c.MinH.Value / (double)ih);
            else
                sLower = Math.Max(sLower, minEdge / (double)ih);


            if (c.MaxEdge.HasValue) sUpper = Math.Min(sUpper, c.MaxEdge.Value / longEdge);
            if (c.MaxW.HasValue)    sUpper = Math.Min(sUpper, c.MaxW.Value / (double)iw);
            if (c.MaxH.HasValue)    sUpper = Math.Min(sUpper, c.MaxH.Value / (double)ih);
        }
        else
        {
            sLower = Math.Max(sLower, minEdge / longEdge);
        }

        // Default desire: fit long edge to defaultTargetEdge (without upscaling unless allowed)
        double desired = defaultTargetEdge / longEdge;
        if (!allowUpscaleByDefault && desired > 1) desired = 1;

        var s = Math.Max(sLower, Math.Min(desired, sUpper));
        if (double.IsInfinity(s) || s <= 0) s = 1.0; // safety

        int cw = Math.Max(1, (int)Math.Round(iw * s));
        int ch = Math.Max(1, (int)Math.Round(ih * s));
        return (cw, ch);
    }

    public sealed class ImgConstraints {
        public int? MinEdge, MaxEdge, MinW, MinH, MaxW, MaxH;
        public bool  AllowUpscale; // optional: allow upscaling even without mins
    }

    public sealed class ImgTagInfo {
        public string Src;
        public ImgConstraints C = new ImgConstraints();
    }
    
    bool TryExtractImgAndSanitize(XmlNode xml, out ImgTagInfo info, out XmlNode sanitized)
    {
        info = null; sanitized = null;
        if (xml == null)
        {
            TryLog($"MyQuick 1");
            return false;
        }

        var doc = new XmlDocument { PreserveWhitespace = true };
        var root = doc.ImportNode(xml, true); 
        doc.AppendChild(root);

        var summary = doc.SelectSingleNode("//*[local-name()='summary']");
        if (summary == null)
        {
            sanitized = root; 
            TryLog($"MyQuick 2");
            return false;
        }

        var img = summary.SelectSingleNode(".//*[local-name()='img']") as XmlElement;
        if (img == null)
        {
            sanitized = root; 
            TryLog($"MyQuick 3");
            return false;
        }

        var src = img.GetAttribute("src")?.Trim();
        if (string.IsNullOrEmpty(src))
        {
            sanitized = root; 
            TryLog($"MyQuick 4");
            return false;
        }

        var tag = new ImgTagInfo { Src = src };
        static int? Px(XmlElement e, params string[] names) {
            foreach (var n in names) {
                var v = e.GetAttribute(n);
                if (!string.IsNullOrWhiteSpace(v)) {
                    v = v.Trim().TrimEnd('p','x','P','X');
                    if (int.TryParse(v, out var npx) && npx > 0) return npx;
                }
            }
            return null;
        }
        tag.C.MinEdge = Px(img, "min", "min-size");
        tag.C.MaxEdge = Px(img, "max", "max-size");
        tag.C.MinW = Px(img, "min-width");  tag.C.MinH = Px(img, "min-height");
        tag.C.MaxW = Px(img, "max-width");  tag.C.MaxH = Px(img, "max-height");
        tag.C.AllowUpscale = string.Equals(img.GetAttribute("upscale"), "true", StringComparison.OrdinalIgnoreCase);

        foreach (XmlNode n in summary.SelectNodes(".//*[local-name()='img']"))
            n.ParentNode?.RemoveChild(n);

        info = tag; 
        sanitized = root; 
        return true;
    }

    static readonly ConcurrentDictionary<string, string> ImgSrcCache = new();

    static string BuildImgSrc(string absPath, long inlineThresholdBytes = 150 * 1024)
    {
        var fi = new FileInfo(absPath);
        var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{inlineThresholdBytes}";
        if (ImgSrcCache.TryGetValue(key, out var cached)) return cached;

        string result;
        var ext = Path.GetExtension(absPath)?.ToLowerInvariant();
        var mime = ext switch {
            ".png" => "image/png", ".jpg" => "image/jpeg", ".jpeg" => "image/jpeg",
            ".gif" => "image/gif", ".svg" => "image/svg+xml", ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };

        if (fi.Length <= inlineThresholdBytes) {
            // Inline small files only
            if (ext == ".svg") {
                var svg = File.ReadAllText(absPath, Encoding.UTF8);
                result = $"data:{mime};utf8,{Uri.EscapeDataString(svg)}";
            } else {
                var b = File.ReadAllBytes(absPath);
                result = $"data:{mime};base64,{Convert.ToBase64String(b)}";
            }
        } else {
            // Use file:// for big assets (fast, no huge HTML)
            result = new Uri(absPath).AbsoluteUri; // file:///...
        }

        ImgSrcCache[key] = result;
        return result;
    }

    private static (int W, int H)? TryProbeSize(string path)
    {
        try
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                var b2 = new byte[2];
                var b4 = new byte[4];
                var b8 = new byte[8];

                // PNG
                if (br.Read(b8, 0, 8) == 8 &&
                    b8[0]==0x89 && b8[1]==0x50 && b8[2]==0x4E && b8[3]==0x47)
                {
                    fs.Position = 16; br.Read(b8, 0, 8);
                    int w = (b8[0]<<24)|(b8[1]<<16)|(b8[2]<<8)|b8[3];
                    int h = (b8[4]<<24)|(b8[5]<<16)|(b8[6]<<8)|b8[7];
                    return (w, h);
                }

                // GIF
                fs.Position = 0; br.Read(b4, 0, 4); // "GIF8"
                if (b4[0]=='G' && b4[1]=='I' && b4[2]=='F')
                {
                    br.Read(b4, 0, 4);
                    int w = b4[0] | (b4[1]<<8);
                    int h = b4[2] | (b4[3]<<8);
                    return (w, h);
                }

                // BMP
                fs.Position = 0; br.Read(b2, 0, 2); // "BM"
                if (b2[0]=='B' && b2[1]=='M')
                {
                    fs.Position = 18; br.Read(b8, 0, 8);
                    int w = BitConverter.ToInt32(b8, 0);
                    int h = Math.Abs(BitConverter.ToInt32(b8, 4));
                    return (w, h);
                }

                // JPEG (scan to SOF)
                fs.Position = 0; br.Read(b2, 0, 2);
                if (b2[0]==0xFF && b2[1]==0xD8) // SOI
                {
                    while (fs.Position < fs.Length)
                    {
                        if (br.Read(b2, 0, 2) != 2 || b2[0] != 0xFF) break;
                        byte marker = b2[1];
                        if (marker==0xD9 || marker==0xDA) break; // EOI or SOS
                        br.Read(b2, 0, 2);
                        int len = (b2[0]<<8)|b2[1];
                        if (len < 2) break;

                        bool sof = (marker >= 0xC0 && marker <= 0xC3) ||
                                   (marker >= 0xC5 && marker <= 0xC7) ||
                                   (marker >= 0xC9 && marker <= 0xCB) ||
                                   (marker >= 0xCD && marker <= 0xCF);
                        if (sof)
                        {
                            br.ReadByte();                 // precision
                            br.Read(b2, 0, 2); int h = (b2[0]<<8)|b2[1];
                            br.Read(b2, 0, 2); int w = (b2[0]<<8)|b2[1];
                            return (w, h);
                        }
                        fs.Position += len - 2;
                    }
                }

                // SVG (very light sniff)
                fs.Position = 0;
                using (var sr = new StreamReader(fs, Encoding.UTF8, true, 4096, leaveOpen:true))
                {
                    var head = (sr.ReadLine() ?? "") + (sr.ReadLine() ?? "") + (sr.ReadLine() ?? "");
                    if (head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int W(string n) => ExtractIntAttr(head, n);
                        int w = W("width"), h = W("height");
                        return (w > 0 && h > 0) ? (w, h) : (64, 64);
                    }
                }
            }
        }
        catch { /* ignore and fall through */ }

        return null;
    }

    private static int ExtractIntAttr(string s, string name)
    {
        var i = s.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        var a = s.IndexOf('"', i);
        var b = (a >= 0) ? s.IndexOf('"', a + 1) : -1;
        if (a < 0 || b < 0) return -1;
        var v = s.Substring(a + 1, b - a - 1).Trim().TrimEnd('p','x','%');
        return int.TryParse(v, out var n) ? n : -1;
    }

    public string GetId() =>this.myEnvoy.GetValidDeclaredElement()?.XMLDocId;

    public IQuickDocPresenter Resolve(string id)
    {
        return null;
        // int result;
        // if (id.StartsWith("ID:") && int.TryParse(id.Substring("ID:".Length), out result))
        //     return this.myQuickDocTypeMemberProvider.Resolve(this.LinkCollector.GetEnvoyById(result)?.GetValidDeclaredElement());
        // return this.myEnvoy.GetValidDeclaredElement() != null ? this.myQuickDocTypeMemberProvider.Resolve(id, this.GetModule()) : (IQuickDocPresenter) null;
    }
    
    public void OpenInEditor(string navigationId = "")
    {
        IClrDeclaredElement validDeclaredElement = this.myEnvoy.GetValidDeclaredElement();
        if (validDeclaredElement == null)
            return;
        validDeclaredElement.Navigate(true);
    }

    public void ReadMore(string navigationId = "") => throw new NotSupportedException();
    
    private VirtualFileSystemPath? ResolveIconPath(ISolution solution, string relativePath)
    {
        var root = solution.SolutionDirectory;
        var candidates = new[]
        {
            root.Combine(relativePath),
            root.Combine("Assets").Combine(relativePath),
        };
        foreach (var p in candidates)
            if (p.ExistsFile)
            {
                TryLog($"Found: {p.FullPath}");
                return p;
            }
            else
            {
                TryLog($"Not Found: {p.FullPath}");

            }

        return null;
    }

    private static void TryLog(string message)
    {
        if (!_shouldLog)
            return;
        
        _logger.Info(message);
    }
}
