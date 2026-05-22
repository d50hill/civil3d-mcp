using System.Text;
using System.Text.Json;

namespace Civil3DMcpPlugin;

/// <summary>
/// Append-only JSON-lines audit log. One file per local day under
/// %LOCALAPPDATA%\civil3d-mcp\audit\&lt;yyyy-MM-dd&gt;.log. The full code of every
/// execution is recorded so the user can review what the AI actually ran.
/// </summary>
public static class AuditLog
{
  private static readonly object Sync = new();

  public static string BaseDirectory =>
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "civil3d-mcp",
      "audit");

  public static string CurrentLogPath =>
    Path.Combine(BaseDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");

  public record Entry(
    string Timestamp,
    string Mode,
    string Description,
    string CodeHash,
    string CodePreview,
    double DurationMs,
    string Status,
    string? Error = null,
    string? CorrelationId = null);

  public static void Write(Entry entry)
  {
    try
    {
      Directory.CreateDirectory(BaseDirectory);
      var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
      lock (Sync)
      {
        File.AppendAllText(CurrentLogPath, line, new UTF8Encoding(false));
      }
    }
    catch
    {
      // Best-effort. Never throw from the audit path.
    }
  }

  /// <summary>Build the "preview" of a script (first 500 chars) for the audit entry.</summary>
  public static string Preview(string code)
  {
    if (string.IsNullOrEmpty(code)) return string.Empty;
    return code.Length <= 500 ? code : code[..500] + "…";
  }
}
