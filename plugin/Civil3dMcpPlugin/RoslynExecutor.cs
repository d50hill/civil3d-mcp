using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Civil3DMcpPlugin;

/// <summary>
/// Compiles and executes C# code snippets using Roslyn.
/// Provides full access to AutoCAD + Civil 3D APIs through ScriptContext globals.
/// Includes script caching for performance.
/// </summary>
public static class RoslynExecutor
{
  /// <summary>Cache of compiled scripts keyed by SHA-256 hex digest of the source.</summary>
  private static readonly ConcurrentDictionary<string, Script<object>> _scriptCache = new();

  /// <summary>Max script execution time (default 120 seconds).</summary>
  public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

  private static ScriptOptions BuildOptions()
  {
    var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
      .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
      .ToArray();

    var options = ScriptOptions.Default
      .WithReferences(loadedAssemblies)
      .WithImports(
        "System",
        "System.Linq",
        "System.Collections.Generic",
        "System.Text",
        "Autodesk.AutoCAD.ApplicationServices",
        "Autodesk.AutoCAD.DatabaseServices",
        "Autodesk.AutoCAD.EditorInput",
        "Autodesk.AutoCAD.Geometry",
        "Autodesk.AutoCAD.Runtime",
        "Autodesk.Civil",
        "Autodesk.Civil.ApplicationServices",
        "Autodesk.Civil.DatabaseServices",
        "Autodesk.Civil.Settings"
      )
      .WithAllowUnsafe(false);

    return options;
  }

  /// <summary>
  /// Execute a C# code snippet with the given ScriptContext as globals.
  /// </summary>
  /// <param name="code">C# code to execute.</param>
  /// <param name="context">Globals (Document, CivilDoc, Database, Transaction, Editor).</param>
  /// <param name="readOnly">If true, the sandbox additionally blocks mutating APIs.</param>
  public static async Task<object?> ExecuteAsync(string code, ScriptContext context, bool readOnly)
  {
    ScriptSandbox.Validate(code, readOnly);

    var options = BuildOptions();
    var key = HashCode(code);

    if (!_scriptCache.TryGetValue(key, out var script))
    {
      script = CSharpScript.Create<object>(code, options, typeof(ScriptContext));
      script.Compile();
      _scriptCache.TryAdd(key, script);
    }

    using var cts = new CancellationTokenSource(Timeout);

    try
    {
      var result = await script.RunAsync(context, cts.Token);
      return result.ReturnValue;
    }
    catch (OperationCanceledException)
    {
      throw new JsonRpcDispatchException(
        "CIVIL3D.TIMEOUT",
        $"Script execution timed out after {Timeout.TotalSeconds}s."
      );
    }
    catch (CompilationErrorException ex)
    {
      var errors = string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
      throw new JsonRpcDispatchException(
        "CIVIL3D.COMPILATION_ERROR",
        $"C# compilation failed:\n{errors}"
      );
    }
  }

  /// <summary>Stable SHA-256 hex digest of the source. Collision-resistant cache key.</summary>
  public static string HashCode(string code)
  {
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
    return Convert.ToHexString(bytes);
  }

  /// <summary>Clear the script cache.</summary>
  public static void ClearCache() => _scriptCache.Clear();
}
