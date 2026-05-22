using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;
using App = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Civil3DMcpPlugin;

/// <summary>
/// Routes JSON-RPC methods. With code execution architecture,
/// only 2 methods are needed: executeCode and getCivil3DHealth.
/// </summary>
public static class CommandDispatcher
{
  public static async Task<object?> DispatchAsync(
    string method,
    JsonObject? parameters,
    CancellationToken cancellationToken)
  {
    return method switch
    {
      "executeCode" => await ExecuteCodeAsync(parameters),
      "getCivil3DHealth" => await GetHealthAsync(),

      _ => throw new JsonRpcDispatchException(
        "CIVIL3D.INVALID_INPUT",
        $"Unknown method '{method}'. Available: executeCode, getCivil3DHealth"
      ),
    };
  }

  /// <summary>
  /// Execute C# code via Roslyn in the Civil 3D context. Every invocation is
  /// recorded in the audit log so the user can review what the AI actually ran.
  /// </summary>
  private static async Task<object?> ExecuteCodeAsync(JsonObject? parameters)
  {
    var code = PluginRuntime.GetRequiredString(parameters, "code");
    var readOnly = parameters?["readOnly"]?.GetValue<bool>() ?? false;
    var description = PluginRuntime.GetOptionalString(parameters, "description") ?? "Script execution";

    var mode = readOnly ? "QUERY" : "EXECUTE";
    var codeHash = RoslynExecutor.HashCode(code);
    var sw = Stopwatch.StartNew();

    try
    {
      var result = await CivilExecution.ExecuteAsync((doc, civilDoc, db, tr) =>
      {
        var context = new ScriptContext(doc, civilDoc, db, tr);
        var task = RoslynExecutor.ExecuteAsync(code, context, readOnly);
        task.Wait();
        return task.Result;
      }, write: !readOnly);

      sw.Stop();
      AuditLog.Write(new AuditLog.Entry(
        Timestamp: DateTime.UtcNow.ToString("o"),
        Mode: mode,
        Description: description,
        CodeHash: codeHash,
        CodePreview: AuditLog.Preview(code),
        DurationMs: sw.Elapsed.TotalMilliseconds,
        Status: "ok"));

      return result;
    }
    catch (Exception ex)
    {
      sw.Stop();
      AuditLog.Write(new AuditLog.Entry(
        Timestamp: DateTime.UtcNow.ToString("o"),
        Mode: mode,
        Description: description,
        CodeHash: codeHash,
        CodePreview: AuditLog.Preview(code),
        DurationMs: sw.Elapsed.TotalMilliseconds,
        Status: "error",
        Error: ex is AggregateException agg && agg.InnerException != null
          ? agg.InnerException.Message
          : ex.Message));
      throw;
    }
  }

  /// <summary>Health check — verifies the plugin is alive and Civil 3D is responsive.</summary>
  private static Task<object?> GetHealthAsync()
  {
    return CivilExecution.ReadAsync<object?>((doc, civilDoc, db, tr) =>
    {
      return new
      {
        connected = true,
        drawingName = doc.Name,
        mode = "code_execution",
        roslyn = true,
      };
    });
  }
}
