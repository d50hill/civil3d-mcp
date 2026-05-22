# Civil 3D MCP Server — Code Execution Architecture

An MCP server that enables AI assistants to **write and execute C# code** directly inside Autodesk Civil 3D. Instead of fixed tools, the AI generates code that runs with full API access.

## Architecture

```
┌─────────────────┐     stdio      ┌──────────────────┐     TCP/JSON-RPC    ┌──────────────────┐
│   AI Assistant   │ ◄────────────► │  MCP Server (TS) │ ◄──────────────────► │  Civil 3D Plugin │
│ (Claude, Cline)  │               │   3 meta-tools    │     port 8080       │  Roslyn Engine   │
└─────────────────┘               └──────────────────┘                      └──────────────────┘
                                         │                                         │
                                    Skills Library                           C# Code Execution
                                   (.skill.md files)                      (full Civil 3D API)
```

## 3 Meta-Tools

| Tool | Purpose | Safety |
|------|---------|--------|
| `civil3d_execute` | Execute C# code with **write** access (transaction committed) | ⚠️ Modifies drawing |
| `civil3d_query` | Execute C# code **read-only** (no commit) | ✅ No side effects |
| `civil3d_skills` | Browse/search/read code skill templates | ✅ Metadata only |

### How It Works

1. **AI reads a skill** → Gets a documented C# code template
2. **AI adapts the code** → Fills in parameters, combines patterns
3. **AI sends code** → Via `civil3d_execute` or `civil3d_query`
4. **Roslyn compiles + runs** → Inside Civil 3D with full API access
5. **Results return as JSON** → Back to the AI

### Example Interaction

```
User: "What surfaces are in my drawing?"

AI: Uses civil3d_query with:
  var surfaces = new List<object>();
  foreach (ObjectId id in CivilDoc.GetSurfaceIds()) {
    var s = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface;
    surfaces.Add(new { s.Name, s.Layer });
  }
  return surfaces;

Result: [{ "Name": "EG", "Layer": "C-TOPO-EG" }, ...]
```

## Skills Library

Skills are documented C# code templates in `skills/`:

```
skills/
├── surfaces/           # Surface operations
├── alignments/         # Alignment + station/offset
├── points/             # COGO points
├── geometry/           # Lines, polylines, text
├── drawing/            # Drawing info
└── workflows/          # Complex multi-object operations
```

### Script Globals

Code executed via `civil3d_execute` or `civil3d_query` has access to:

| Global | Type | Description |
|--------|------|-------------|
| `Document` | `Document` | Active AutoCAD document |
| `CivilDoc` | `CivilDocument` | Active Civil 3D document |
| `Database` | `Database` | Document database |
| `Transaction` | `Transaction` | Active transaction |
| `Editor` | `Editor` | Document editor |

All Civil 3D namespaces are auto-imported.

## Setup

### 1. Build MCP Server
```bash
npm install && npm run build
```

### 2. Build Plugin
```bash
# Copy DLLs from Civil 3D to C_References/ (see C_References/README.md)
cd plugin/Civil3dMcpPlugin
dotnet build
```

### 3. Load in Civil 3D
```
NETLOAD → select Civil3dMcpPlugin.dll
C3DMCPSTATUS → verify running
```

### 4. Configure AI
```json
{
  "mcpServers": {
    "civil3d": {
      "command": "node",
      "args": ["/path/to/civil3d-mcp/build/index.js"]
    }
  }
}
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `CIVIL3D_HOST` | `localhost` | Plugin host |
| `CIVIL3D_PORT` | `8080` | Plugin port |
| `CIVIL3D_COMMAND_TIMEOUT` | `120000` | Execution timeout (ms) |
| `LOG_LEVEL` | `info` | Log level |

## Security

This MCP gives the AI the ability to execute arbitrary C# inside Civil 3D, so
the surface needs serious defenses. See **[SECURITY.md](SECURITY.md)** for the
full security model and an integration-test checklist.

Short version:

1. **Sandbox.** Roslyn `SemanticModel` walk rejects anything that references
   `System.IO`, `System.Net`, `System.Reflection`,
   `System.Runtime.InteropServices`, `Microsoft.Win32`, `Process`, `Activator`,
   `AppDomain`, `Type.GetType(string)`, `Environment.Exit`, `dynamic`,
   `unsafe`, `stackalloc`, or `[DllImport]`. Aliases and `using static` are
   resolved, so `using P = System.Diagnostics.Process;` is caught.
2. **Read-only enforcement.** `civil3d_query` rejects `OpenMode.ForWrite` and
   `Transaction.Commit` at the script-validation level — read-only is no
   longer just "the transaction isn't committed."
3. **TCP authentication.** The plugin writes a 256-bit shared-secret token to
   `%LOCALAPPDATA%\civil3d-mcp\token` with a user-only ACL on every start.
   Every JSON-RPC request must carry that token; any other local process is
   rejected with `CIVIL3D.UNAUTHORIZED`.
4. **Audit log.** Every execution (success or failure, including the full code
   preview, hash, mode, duration, and any error) is appended to
   `%LOCALAPPDATA%\civil3d-mcp\audit\<yyyy-MM-dd>.log`. Review it.
5. **Input size caps.** 64 KB per script (rejected at the MCP tool layer) and
   1 MB per JSON-RPC payload (rejected at the socket).
6. **Sanitized errors.** Unhandled exceptions return a correlation ID; the
   full stack trace stays in the plugin audit log.

### Known limitation: prompt injection

Layer names, block descriptions, XData, or filenames in a DWG can flow back
into the AI's context. A maliciously crafted drawing can therefore influence
the code the AI generates. **Do not load untrusted DWG files while this MCP
plugin is active.** Review the audit log periodically.

## License

  MIT
