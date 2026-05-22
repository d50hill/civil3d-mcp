import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { randomUUID } from "crypto";
import { z } from "zod";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { createLogger } from "../utils/logger.js";

const log = createLogger("QueryTool");

const MAX_CODE_CHARS = 65_536;

/**
 * civil3d_query — Executes C# code in Civil 3D in READ-ONLY mode.
 * The transaction is NOT committed, AND the sandbox additionally rejects
 * write-mutating APIs (OpenMode.ForWrite, Transaction.Commit, etc.) so the
 * read-only guarantee holds at the script-validation level too.
 *
 * Same globals as civil3d_execute but safe for data retrieval.
 * Use this for listing objects, getting properties, analyzing data, etc.
 */
export function registerQueryTool(server: McpServer) {
  server.tool(
    "civil3d_query",
    "Execute C# code in Civil 3D in READ-ONLY mode. Read-only is enforced at both " +
      "the AutoCAD transaction level (no commit) AND at the script-validation level " +
      "(OpenMode.ForWrite and Transaction.Commit are rejected by the sandbox). " +
      "Available globals: Document, CivilDoc, Database, Transaction, Editor. " +
      "All Civil 3D namespaces are auto-imported. Return a value to get results as JSON. " +
      "Use this for querying data: listing objects, getting properties, analyzing surfaces, etc.",
    {
      code: z
        .string()
        .max(
          MAX_CODE_CHARS,
          `code must be at most ${MAX_CODE_CHARS} characters`
        )
        .describe(
          "C# code to query data. Has access to Document, CivilDoc, Database, Transaction, Editor. " +
            "Example: var surfaces = new List<object>(); " +
            "foreach (ObjectId id in CivilDoc.GetSurfaceIds()) { " +
            "var s = Transaction.GetObject(id, OpenMode.ForRead) as TinSurface; " +
            'surfaces.Add(new { s.Name, s.Layer }); } return surfaces;'
        ),
    },
    async (args) => {
      try {
        log.debug("Executing query", { code: args.code });

        const result = await withApplicationConnection(async (client) =>
          await client.sendCommand("executeCode", {
            code: args.code,
            readOnly: true,
          })
        );

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(result, null, 2),
            },
          ],
        };
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        const ref = randomUUID().slice(0, 8);
        log.error("Query failed", { ref, error: message });

        const passThrough =
          message.startsWith("Script blocked:") ||
          message.startsWith("C# compilation failed") ||
          message.startsWith("Script execution timed out") ||
          message.startsWith("Civil 3D plugin rejected") ||
          message.startsWith("Could not read Civil 3D MCP auth token") ||
          message.startsWith("Failed to connect to Civil 3D plugin") ||
          message.startsWith("Connection to Civil 3D plugin timed out") ||
          message.startsWith("Internal error (ref:");

        return {
          content: [
            {
              type: "text" as const,
              text: passThrough
                ? `Query failed: ${message}`
                : `Query failed (ref: ${ref}). See MCP server logs for details.`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
