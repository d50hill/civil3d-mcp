import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { randomUUID } from "crypto";
import { z } from "zod";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import { createLogger } from "../utils/logger.js";

const log = createLogger("ExecuteTool");

const MAX_CODE_CHARS = 65_536;

/**
 * civil3d_execute — Executes C# code in Civil 3D with WRITE access.
 * The code runs inside a transaction that gets committed on success.
 *
 * Available globals in the script:
 *   - Document (active AutoCAD document)
 *   - CivilDoc (active Civil 3D document)
 *   - Database (document database)
 *   - Transaction (active transaction — auto-committed)
 *   - Editor (document editor)
 *
 * The script can use all Civil 3D and AutoCAD namespaces (auto-imported).
 * Return a value to send it back as JSON to the AI.
 */
export function registerExecuteTool(server: McpServer) {
  server.tool(
    "civil3d_execute",
    "Execute C# code in Civil 3D with write access. The code runs inside a committed transaction. " +
      "Available globals: Document, CivilDoc, Database, Transaction, Editor. " +
      "All Civil 3D namespaces are auto-imported. Return a value to get results back as JSON. " +
      "Use this for operations that MODIFY the drawing (create, edit, delete objects).",
    {
      code: z
        .string()
        .max(
          MAX_CODE_CHARS,
          `code must be at most ${MAX_CODE_CHARS} characters`
        )
        .describe(
          "C# code to execute. Has access to Document, CivilDoc, Database, Transaction, Editor. " +
            "Example: var id = TinSurface.Create(Database, \"MySurface\"); return new { success = true };"
        ),
      description: z.string().optional().describe(
        "Brief description of what this code does (for logging/audit trail)."
      ),
    },
    async (args) => {
      try {
        log.info("Executing write operation", { description: args.description });
        log.debug("Code", { code: args.code });

        const result = await withApplicationConnection(async (client) =>
          await client.sendCommand("executeCode", {
            code: args.code,
            readOnly: false,
            description: args.description,
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
        log.error("Execute failed", { ref, error: message });

        // Pass through structured plugin errors (sandbox/compilation/timeout/unauthorized)
        // — they're already safe and useful for the AI to react to.
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
                ? `Execution failed: ${message}`
                : `Execution failed (ref: ${ref}). See MCP server logs for details.`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
