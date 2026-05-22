using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Civil3DMcpPlugin;

/// <summary>
/// Validates a C# script before Roslyn executes it. Uses a SemanticModel pass so
/// banned types are caught regardless of `using` aliases, `using static`, or fully
/// qualified vs short-form references. Catches what a regex over the source text
/// cannot: string concatenation that produces a banned name string, reflection,
/// and aliases.
/// </summary>
public static class ScriptSandbox
{
  public const int MaxCodeBytes = 65_536;

  /// <summary>Whole namespaces (and their descendants) forbidden in scripts.</summary>
  private static readonly string[] BannedNamespacePrefixes =
  {
    "System.IO",
    "System.Net",
    "System.Reflection",
    "System.Runtime.InteropServices",
    "Microsoft.Win32",
    "System.Threading.Tasks.Dataflow",
  };

  /// <summary>Specific banned types by full metadata name.</summary>
  private static readonly HashSet<string> BannedTypes = new(StringComparer.Ordinal)
  {
    "System.Diagnostics.Process",
    "System.Diagnostics.ProcessStartInfo",
    "System.Activator",
    "System.AppDomain",
    "System.Threading.Thread",
  };

  /// <summary>Banned member accesses ("Type.Member"). The two-argument
  /// <see cref="Type.GetType(string)"/> overload is forbidden but the parameterless
  /// <c>obj.GetType()</c> remains usable.</summary>
  private static readonly HashSet<string> BannedMembers = new(StringComparer.Ordinal)
  {
    "System.Type.GetType",
    "System.Environment.Exit",
    "System.Environment.FailFast",
    "System.Environment.SetEnvironmentVariable",
  };

  /// <summary>
  /// Validate code. Throws <see cref="JsonRpcDispatchException"/> on any violation.
  /// </summary>
  /// <param name="code">The script source.</param>
  /// <param name="readOnly">If true, also reject anything that mutates the DWG.</param>
  public static void Validate(string code, bool readOnly)
  {
    if (string.IsNullOrWhiteSpace(code))
      throw Reject("Code cannot be empty.");

    if (code.Length > MaxCodeBytes)
      throw Reject($"Script too large: {code.Length} chars (max {MaxCodeBytes}).");

    var parseOptions = new CSharpParseOptions(
      languageVersion: LanguageVersion.Latest,
      kind: SourceCodeKind.Script);

    var tree = CSharpSyntaxTree.ParseText(code, parseOptions);
    var root = tree.GetCompilationUnitRoot();

    // Surface parse errors before doing any deeper work.
    var parseErrors = tree.GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .ToList();
    if (parseErrors.Count > 0)
    {
      var msg = string.Join("; ", parseErrors.Take(3).Select(d => d.GetMessage()));
      throw new JsonRpcDispatchException("CIVIL3D.COMPILATION_ERROR", $"Parse error: {msg}");
    }

    // Cheap syntax-only checks (don't require a SemanticModel).
    SyntaxOnlyChecks(root, readOnly);

    // Semantic model — resolves identifiers through aliases / using static.
    var compilation = BuildValidationCompilation(tree);
    var model = compilation.GetSemanticModel(tree);

    var walker = new BanWalker(model, readOnly);
    walker.Visit(root);
    if (walker.Violation != null)
      throw Reject(walker.Violation);
  }

  private static JsonRpcDispatchException Reject(string reason) =>
    new("CIVIL3D.SANDBOX_VIOLATION", $"Script blocked: {reason}");

  private static void SyntaxOnlyChecks(CompilationUnitSyntax root, bool readOnly)
  {
    foreach (var node in root.DescendantNodesAndTokensAndSelf())
    {
      if (node.IsKind(SyntaxKind.UnsafeStatement))
        throw Reject("'unsafe' blocks are not allowed.");
      if (node.IsKind(SyntaxKind.StackAllocArrayCreationExpression) ||
          node.IsKind(SyntaxKind.ImplicitStackAllocArrayCreationExpression))
        throw Reject("'stackalloc' is not allowed.");
    }

    // 'dynamic' type usage: detect via predefined-type / identifier with name "dynamic"
    // (Roslyn represents it as an IdentifierNameSyntax with text "dynamic" used in a type position.)
    foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
    {
      if (id.Identifier.Text == "dynamic" && id.Parent is not InvocationExpressionSyntax)
      {
        // 'dynamic' is only contextually a keyword; a local named 'dynamic' would also hit
        // this. That's an acceptable false positive — scripts can rename the local.
        throw Reject("'dynamic' is not allowed.");
      }
    }

    // DllImport attribute (P/Invoke)
    foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
    {
      var name = attr.Name.ToString();
      if (name == "DllImport" || name.EndsWith(".DllImport") ||
          name == "DllImportAttribute" || name.EndsWith(".DllImportAttribute"))
        throw Reject("[DllImport] (P/Invoke) is not allowed.");
    }
  }

  private static CSharpCompilation BuildValidationCompilation(SyntaxTree tree)
  {
    var references = AppDomain.CurrentDomain.GetAssemblies()
      .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
      .Select(a =>
      {
        try { return (MetadataReference)MetadataReference.CreateFromFile(a.Location); }
        catch { return null!; }
      })
      .Where(r => r != null)
      .ToList();

    return CSharpCompilation.CreateScriptCompilation(
      "Civil3dMcpSandboxValidation",
      syntaxTree: tree,
      references: references,
      options: new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        allowUnsafe: false),
      globalsType: typeof(ScriptContext));
  }

  /// <summary>
  /// Walks the syntax tree and asks the SemanticModel to resolve identifiers and
  /// member accesses. Records the first violation it finds.
  /// </summary>
  private sealed class BanWalker : CSharpSyntaxWalker
  {
    private readonly SemanticModel _model;
    private readonly bool _readOnly;

    public string? Violation { get; private set; }

    public BanWalker(SemanticModel model, bool readOnly)
      : base(SyntaxWalkerDepth.Node)
    {
      _model = model;
      _readOnly = readOnly;
    }

    public override void Visit(SyntaxNode? node)
    {
      if (Violation != null) return;
      base.Visit(node);
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
      // Catches `using P = System.Diagnostics.Process;` and `using static …`
      var symbol = _model.GetSymbolInfo(node.Name!).Symbol;
      CheckSymbol(symbol, node.Name!.ToString());
      base.VisitUsingDirective(node);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
      // Don't double-flag inside a member access — the member-access visitor handles
      // the fully-qualified case more precisely.
      if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
      {
        base.VisitIdentifierName(node);
        return;
      }
      var symbol = _model.GetSymbolInfo(node).Symbol;
      CheckSymbol(symbol, node.Identifier.Text);
      CheckReadOnly(node, symbol);
      base.VisitIdentifierName(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
      var symbol = _model.GetSymbolInfo(node).Symbol;
      CheckSymbol(symbol, node.ToString());
      CheckReadOnly(node, symbol);
      base.VisitMemberAccessExpression(node);
    }

    public override void VisitQualifiedName(QualifiedNameSyntax node)
    {
      var symbol = _model.GetSymbolInfo(node).Symbol;
      CheckSymbol(symbol, node.ToString());
      base.VisitQualifiedName(node);
    }

    private void CheckSymbol(ISymbol? symbol, string display)
    {
      if (symbol == null || Violation != null) return;

      // 1) Banned namespaces (and any descendant type/member).
      var containingNs = symbol.ContainingNamespace?.ToDisplayString() ?? "";
      if (symbol is INamespaceSymbol ns)
        containingNs = ns.ToDisplayString();

      foreach (var prefix in BannedNamespacePrefixes)
      {
        if (containingNs == prefix || containingNs.StartsWith(prefix + ".", StringComparison.Ordinal))
        {
          Violation = $"namespace '{prefix}' is not allowed (saw '{display}').";
          return;
        }
        if (symbol is INamespaceSymbol nsSym &&
            (nsSym.ToDisplayString() == prefix ||
             nsSym.ToDisplayString().StartsWith(prefix + ".", StringComparison.Ordinal)))
        {
          Violation = $"namespace '{prefix}' is not allowed (saw '{display}').";
          return;
        }
      }

      // 2) Specific banned types.
      var typeFullName = (symbol as INamedTypeSymbol)?.ToDisplayString()
        ?? symbol.ContainingType?.ToDisplayString();
      if (typeFullName != null && BannedTypes.Contains(typeFullName))
      {
        Violation = $"type '{typeFullName}' is not allowed.";
        return;
      }

      // 3) Banned members like System.Type.GetType, System.Environment.Exit, …
      if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol)
      {
        var ownerType = symbol.ContainingType?.ToDisplayString();
        if (ownerType != null)
        {
          var memberKey = $"{ownerType}.{symbol.Name}";
          if (BannedMembers.Contains(memberKey))
          {
            // Allow obj.GetType() (no args). Only block Type.GetType(string).
            if (memberKey == "System.Type.GetType")
            {
              if (symbol is IMethodSymbol m && m.Parameters.Length == 0)
                return;
            }
            Violation = $"member '{memberKey}' is not allowed.";
            return;
          }
        }
      }
    }

    private void CheckReadOnly(SyntaxNode node, ISymbol? symbol)
    {
      if (!_readOnly || Violation != null) return;

      // Block any reference to Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite
      if (symbol is IFieldSymbol f &&
          f.Name == "ForWrite" &&
          f.ContainingType?.ToDisplayString() == "Autodesk.AutoCAD.DatabaseServices.OpenMode")
      {
        Violation = "Read-only mode: 'OpenMode.ForWrite' is not allowed. Use civil3d_execute for writes.";
        return;
      }

      // Block explicit Transaction.Commit() calls in read-only mode.
      if (symbol is IMethodSymbol m &&
          m.Name == "Commit" &&
          m.ContainingType?.ToDisplayString() == "Autodesk.AutoCAD.DatabaseServices.Transaction")
      {
        Violation = "Read-only mode: 'Transaction.Commit()' is not allowed.";
        return;
      }
    }
  }
}
