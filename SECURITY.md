# Security Model

This MCP server lets an AI execute arbitrary C# inside Civil 3D. Because that
is intrinsically high-trust, every layer is hardened. This document describes
the defenses and how to verify them.

## What the server does

- The Node.js MCP process exposes three tools to the AI:
  - `civil3d_execute` — write operations (committed transaction)
  - `civil3d_query` — read-only operations (no commit; write APIs also blocked)
  - `civil3d_skills` — read-only browse of bundled skill templates
- The TypeScript layer forwards JSON-RPC requests over TCP to the C# Civil 3D
  plugin on `localhost:8080`.
- The plugin validates the code with a Roslyn AST/SemanticModel sandbox, then
  executes it via `CSharpScript` on the AutoCAD main thread.

## Defenses

| # | Defense | Implementation |
|---|---|---|
| 1 | Sandbox blocks dangerous APIs before execution | `plugin/.../ScriptSandbox.cs` — SemanticModel walk |
| 2 | TCP server requires a per-user shared-secret token | `AuthToken.cs`, file at `%LOCALAPPDATA%\civil3d-mcp\token` (user-only ACL) |
| 3 | Read-only mode also blocks write APIs | `OpenMode.ForWrite` / `Transaction.Commit` rejected |
| 4 | Max request size limits on both sides | 1 MB at the socket; 64 KB at the tool layer |
| 5 | Generic error messages with correlation IDs | Internal exception text never leaves the plugin |
| 6 | Audit log of every execution | `%LOCALAPPDATA%\civil3d-mcp\audit\<yyyy-MM-dd>.log` |
| 7 | Stable, collision-resistant script cache | SHA-256 hex digest as key |

### Sandbox blocklist

The sandbox uses Roslyn's `SemanticModel` so symbols are resolved through
`using` aliases, `using static`, and fully-qualified names alike.

Banned namespaces (and all descendants):
- `System.IO`
- `System.Net`
- `System.Reflection`
- `System.Runtime.InteropServices`
- `Microsoft.Win32`
- `System.Threading.Tasks.Dataflow`

Banned types:
- `System.Diagnostics.Process`, `System.Diagnostics.ProcessStartInfo`
- `System.Activator`, `System.AppDomain`, `System.Threading.Thread`

Banned members:
- `System.Type.GetType(string)` (one-arg overload; parameterless `obj.GetType()` still works)
- `System.Environment.Exit`, `Environment.FailFast`, `Environment.SetEnvironmentVariable`

Banned syntax:
- `unsafe` blocks
- `stackalloc`
- `dynamic` typed identifiers
- `[DllImport]` attribute

Additional read-only restrictions (when `civil3d_query` is used):
- `Autodesk.AutoCAD.DatabaseServices.OpenMode.ForWrite`
- `Autodesk.AutoCAD.DatabaseServices.Transaction.Commit`

## Threats explicitly NOT defended against

- **Prompt injection from drawing content.** Layer names, block descriptions,
  XData, or filenames in a malicious DWG can be passed back into the AI's
  context. The AI might then generate code based on instructions hidden in
  that content. Do not load untrusted DWG files while the MCP plugin is active.
- **A compromised user account.** The auth token is per-user. Any code running
  as your Windows account can read it. Defense relies on Windows account
  isolation.
- **Resource exhaustion via legitimate APIs.** A script that legitimately
  iterates millions of objects is still allowed; only execution wall-clock
  time is capped (120 s default).

## Integration test checklist

Run these manually after every plugin rebuild. All "expected: rejected" cases
should fail fast with a clear error; "expected: ok" should succeed.

### Sandbox — System.*

| # | `civil3d_query` code | Expected |
|---|---|---|
| 1 | `return System.IO.File.ReadAllText("C:\\Windows\\System32\\drivers\\etc\\hosts");` | rejected (`System.IO`) |
| 2 | `System.IO.File.WriteAllText("C:\\evil.txt", "x"); return 0;` | rejected (`System.IO`) |
| 3 | `var p = System.Diagnostics.Process.Start("calc.exe"); return p.Id;` | rejected (`System.Diagnostics.Process`) |
| 4 | `var t = System.Type.GetType("System.Diagnostics.Process"); return t.FullName;` | rejected (`System.Type.GetType` member) |
| 5 | `using P = System.Diagnostics.Process; return 1;` | rejected (alias resolution) |
| 6 | `using static System.Diagnostics.Process; return 1;` | rejected (using static) |
| 7 | `var c = new System.Net.WebClient(); return c.DownloadString("http://x");` | rejected (`System.Net`) |
| 8 | `return System.Reflection.Assembly.GetExecutingAssembly().Location;` | rejected (`System.Reflection`) |
| 9 | `System.Environment.Exit(0); return 0;` | rejected (`Environment.Exit`) |
| 10 | `dynamic x = 1; return x;` | rejected (`dynamic`) |

### Sandbox — read-only enforcement

| # | `civil3d_query` code | Expected |
|---|---|---|
| 11 | `foreach (ObjectId id in CivilDoc.GetSurfaceIds()) { Transaction.GetObject(id, OpenMode.ForWrite); } return 0;` | rejected (`OpenMode.ForWrite`) |
| 12 | `Transaction.Commit(); return 0;` | rejected (`Transaction.Commit`) |

### Sandbox — legitimate scripts still work

| # | Code | Expected |
|---|---|---|
| 13 | `civil3d_query`: `var names = new List<string>(); foreach (ObjectId id in CivilDoc.GetSurfaceIds()) { var s = Transaction.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.TinSurface; if (s != null) names.Add(s.Name); } return names;` | ok |
| 14 | `civil3d_execute`: `var n = "Hello"; return n.GetType().FullName;` | ok (parameterless GetType allowed) |

### Authentication

| # | Action | Expected |
|---|---|---|
| 15 | Connect to `localhost:8080` with `nc` or PowerShell and send `{"jsonrpc":"2.0","method":"getCivil3DHealth","id":1}` (no `auth`) | `CIVIL3D.UNAUTHORIZED` |
| 16 | Same but with `"auth":"bogus"` | `CIVIL3D.UNAUTHORIZED` |
| 17 | Stop the plugin (`C3DMCPSTOP`); the token file at `%LOCALAPPDATA%\civil3d-mcp\token` is deleted | file is gone |
| 18 | Start the plugin again; a new token is written; old MCP processes get `CIVIL3D.UNAUTHORIZED` (until they re-read or restart) | as described |
| 19 | `Get-Acl "$env:LOCALAPPDATA\civil3d-mcp\token" \| Format-List` | only current user has rights |

### Input size

| # | Action | Expected |
|---|---|---|
| 20 | `civil3d_query` with a 100 KB code string | rejected by zod (`code must be at most 65536 characters`) |
| 21 | Send a > 1 MB raw payload directly over TCP | empty response / `INVALID_INPUT` |

### Audit log

| # | Action | Expected |
|---|---|---|
| 22 | After running a few queries/executions, open `%LOCALAPPDATA%\civil3d-mcp\audit\<today>.log` | one JSON line per execution containing mode, codeHash, codePreview, durationMs, status |
| 23 | Trigger a sandbox rejection | corresponding line has `"status":"error"` and an `Error` field |
