using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using JetBrains.Annotations;
using JetBrains.Application.DataContext;
using JetBrains.DocumentManagers;
using JetBrains.DocumentModel.DataContext;
using JetBrains.Metadata.Reader.API;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.QuickDoc;
using JetBrains.ReSharper.Feature.Services.Util;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.DataContext;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.Util;
using JetBrains.Util.Logging;

[QuickDocProvider(-5)] // run before default XML-doc providers (priority 0)
public class ImageCommentQuickDocProvider : IQuickDocProvider
{
    private const bool _shouldLog = false;
    private static ILogger _logger;
    private readonly DocumentManager _documentManager;
    IModuleReferenceResolveContext _resolveContext;
    private ISubstitution substitution;

    public ImageCommentQuickDocProvider(DocumentManager documentManager, [CanBeNull] IModuleReferenceResolveContext resolveContext = null,
        [CanBeNull] ISubstitution substitution = null)
    {
        _documentManager = documentManager;
        _logger = LogManager.Instance.GetLogger(typeof(ImageCommentQuickDocProvider));
        TryLog($"MyQuickDocExtendingQuickDocProvider:");
    }

    public bool CanNavigate(IDataContext context)
    {
        TryLog($"MyQuickDocExtendingQuickDocProvider:can navigate");
        var decl = context.GetData(PsiDataConstants.DECLARED_ELEMENTS)?.FirstOrDefault() as ITypeMember;
        if (decl == null) return false;

        var xml = decl.GetXMLDoc(false);

        var res = TryExtractImgSrcAndSanitize(xml, out var src, out _); // only handle when <img src="..."> exists in <summary>

        TryLog($"MyQuickDocExtendingQuickDocProvider:can navigate {src}");
        return res;
    }

    private static bool TryExtractImgSrcAndSanitize(XmlNode xml, out string src, out XmlNode sanitized)
    {
        src = null;
        sanitized = null;
        if (xml == null) return false;

        // Clone into its own XmlDocument so we can edit safely
        var doc = new XmlDocument { PreserveWhitespace = true };
        var root = doc.ImportNode(xml, true);
        ;
        TryLog($"MEMO:{XmlUtil.OuterXmlIndented((XmlElement)xml)}");
        TryLog($"ANOTHER MEMO:{XmlUtil.OuterXmlIndented((XmlElement)root)}");
        doc.AppendChild(root);

        // XPath that ignores namespaces and limits to <summary>
        var ns = new XmlNamespaceManager(doc.NameTable);
        var summary = doc.SelectSingleNode("//*[local-name()='summary']");
        if (summary == null) { sanitized = root; return false; }

        // Grab first <img> src (if any)
        var firstImg = summary.SelectSingleNode(".//*[local-name()='img']") as XmlElement;
        if (firstImg == null) { sanitized = root; return false; }

        var s = firstImg.GetAttribute("src")?.Trim();
        if (!string.IsNullOrEmpty(s)) src = s;

        // Remove ALL <img> nodes under <summary>
        var imgs = summary.SelectNodes(".//*[local-name()='img']");
        if (imgs != null)
        {
            foreach (XmlNode n in imgs)
                n.ParentNode?.RemoveChild(n);
        }

        sanitized = root;
        return !string.IsNullOrEmpty(src);
    }

    public void Resolve(IDataContext context, Action<IQuickDocPresenter, PsiLanguageType> resolved)
    {
        TryLog("MyQuickDocExtendingQuickDocProvider Resolve: entered");
        if (!TryGetEditorIconField(context, out var field, out var solution))
        {
            _logger.Warn("MyQuickDocExtendingQuickDocProvider Resolve: no field resolved");
            return;
        }

        var id = field.ShortName; // constant name, e.g. _help
        TryLog($"Resolve: field={id}");

        var xml = field.GetXMLDoc(false);
        if (!TryExtractImgSrcAndSanitize(xml, out _, out _)) return;

        var doc    = context.GetData(DocumentModelDataConstants.DOCUMENT);
        var projectFile = doc != null ? _documentManager.TryGetProjectFile(doc) : null;

        var file = context.GetData(PsiDataConstants.SOURCE_FILE);
        var presenter = new ImageCommentQuickDocPresenter(field, solution, file, _resolveContext, substitution);

        // pick the correct presentation language
        var lang = PresentationUtil.GetPresentationLanguage(field);

        TryLog($"MyQuickDocExtendingQuickDocProvider Resolve: presenter:{lang}");

        resolved(presenter, lang);
    }

    private static bool TryGetEditorIconField(IDataContext ctx, out IField field, out ISolution solution)
    {
        field = null!;
        solution = ctx.GetData(JetBrains.ProjectModel.DataContext.ProjectModelDataConstants.SOLUTION);
        if (solution == null) return false;

        // Prefer declared elements from context
        var decl = TryGetDeclaredField(ctx);
        if (decl != null && IsEditorIconConst(decl))
        {
            field = decl;
            return true;
        }
        // Fallback: caret position
        // if (TryGetFieldAtCaret(ctx, out decl) && decl != null && IsEditorIconConst(decl))
        // { field = decl; return true; }

        return false;
    }

    private static IField? TryGetDeclaredField(IDataContext ctx)
    {
        // Different contexts expose one or many elements

        var many = ctx.GetData(JetBrains.ReSharper.Psi.DataContext.PsiDataConstants.DECLARED_ELEMENTS)
            as ICollection<IDeclaredElement>;
        if (many != null)
            foreach (var e in many)
                if (e is IField f)
                    return f;

        return null;
    }

    private static bool IsEditorIconConst(IField f)
    {
        if (!f.IsConstant) return false;
        if (!f.Type.IsString()) return false;
        var type = f.GetContainingType();
        if (type == null) return false;

        // Adjust if your constants class has another name:
        return string.Equals(type.ShortName, "EditorIcons", StringComparison.Ordinal);
    }

    private static void TryLog(string message)
    {
        if (!_shouldLog)
            return;
        
        _logger.Info(message);
    }
}
