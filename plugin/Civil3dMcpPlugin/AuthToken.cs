using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Civil3DMcpPlugin;

/// <summary>
/// Per-user shared-secret authentication for the local TCP server.
/// Generated on plugin start, written to a user-only file, validated on every JSON-RPC request.
/// </summary>
public static class AuthToken
{
  public static string? CurrentToken { get; private set; }

  public static string TokenFilePath
  {
    get
    {
      var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return Path.Combine(baseDir, "civil3d-mcp", "token");
    }
  }

  /// <summary>
  /// Generate a fresh 256-bit token, write it to the token file with a user-only ACL,
  /// and remember it in process memory for validation.
  /// </summary>
  public static void GenerateAndPersist()
  {
    var bytes = new byte[32];
    RandomNumberGenerator.Fill(bytes);
    var token = Convert.ToBase64String(bytes);

    var path = TokenFilePath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    File.WriteAllText(path, token, new UTF8Encoding(false));
    TryRestrictAcl(path);

    CurrentToken = token;
  }

  /// <summary>
  /// Remove the persisted token. Called on plugin shutdown so a stale token doesn't linger.
  /// </summary>
  public static void Clear()
  {
    CurrentToken = null;
    try
    {
      var path = TokenFilePath;
      if (File.Exists(path)) File.Delete(path);
    }
    catch
    {
      // Best-effort — if we can't delete, the next run will overwrite.
    }
  }

  /// <summary>
  /// Constant-time comparison of a supplied token against the current one.
  /// Returns false if no token is set or the supplied value is empty.
  /// </summary>
  public static bool Validate(string? supplied)
  {
    var current = CurrentToken;
    if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(supplied))
      return false;

    var a = Encoding.UTF8.GetBytes(current);
    var b = Encoding.UTF8.GetBytes(supplied);
    if (a.Length != b.Length) return false;
    return CryptographicOperations.FixedTimeEquals(a, b);
  }

  /// <summary>
  /// Tighten the ACL on the token file so only the current user can read it.
  /// Wrapped in try/catch because ACL operations can fail on exotic filesystems
  /// (network shares, etc.) — falling back to default ACL is still safer than nothing
  /// because the token alone is the secret.
  /// </summary>
#pragma warning disable CA1416 // Windows-only ACL APIs; this whole method only runs on Windows (plugin targets net10.0-windows).
  private static void TryRestrictAcl(string path)
  {
    try
    {
      var info = new FileInfo(path);
      var security = new FileSecurity();
      security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

      var user = WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Cannot determine current Windows user SID.");

      security.AddAccessRule(new FileSystemAccessRule(
        user,
        FileSystemRights.FullControl,
        AccessControlType.Allow));

      info.SetAccessControl(security);
    }
    catch
    {
      // ACL restriction is best-effort. The token value itself is the secret.
    }
  }
#pragma warning restore CA1416
}
