import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { ApplicationClientConnection } from "./SocketClient.js";
import { createLogger } from "./logger.js";

const log = createLogger("ConnectionManager");

const CIVIL3D_HOST = process.env.CIVIL3D_HOST ?? "localhost";
const CIVIL3D_PORT = parseInt(process.env.CIVIL3D_PORT ?? "8080", 10);
const CONNECT_TIMEOUT_MS = parseInt(process.env.CIVIL3D_CONNECT_TIMEOUT ?? "5000", 10);

/**
 * Resolve the path the plugin writes the shared-secret token to.
 * Mirrors AuthToken.TokenFilePath on the C# side.
 *
 * Resolution order:
 *   1. CIVIL3D_TOKEN_PATH env var (explicit override — useful for WSL or custom layouts)
 *   2. LOCALAPPDATA\civil3d-mcp\token (native Windows)
 *   3. ~/AppData/Local/civil3d-mcp/token (fallback when LOCALAPPDATA isn't set)
 */
function getTokenPath(): string {
  if (process.env.CIVIL3D_TOKEN_PATH) {
    return process.env.CIVIL3D_TOKEN_PATH;
  }
  const base =
    process.env.LOCALAPPDATA ??
    path.join(os.homedir(), "AppData", "Local");
  return path.join(base, "civil3d-mcp", "token");
}

let cachedToken: string | null = null;

/**
 * Read the shared-secret token written by the Civil 3D plugin.
 * Cached for the lifetime of the MCP process (rotates only on plugin restart;
 * a stale token simply triggers a clear UNAUTHORIZED response).
 */
function readAuthToken(): string {
  if (cachedToken !== null) return cachedToken;

  const tokenPath = getTokenPath();
  try {
    cachedToken = fs.readFileSync(tokenPath, "utf-8").trim();
    return cachedToken;
  } catch (err) {
    throw new Error(
      `Could not read Civil 3D MCP auth token at ${tokenPath}. ` +
        `Make sure Civil 3D is running with the MCP plugin loaded (NETLOAD) ` +
        `as the same Windows user as this MCP server. (${String(err)})`
    );
  }
}

/**
 * Force a re-read of the token from disk on the next request. Useful when the
 * plugin has been restarted and rotated its token while the MCP server kept running.
 */
export function invalidateAuthTokenCache(): void {
  cachedToken = null;
}

/**
 * Opens a short-lived connection to the Civil 3D plugin, runs the given
 * operation, and tears the connection down afterwards.
 */
export async function withApplicationConnection<T>(
  operation: (client: ApplicationClientConnection) => Promise<T>
): Promise<T> {
  const token = readAuthToken();
  const appClient = new ApplicationClientConnection(CIVIL3D_HOST, CIVIL3D_PORT, token);

  try {
    if (!appClient.isConnected) {
      await new Promise<void>((resolve, reject) => {
        const onConnect = () => {
          appClient.socket.removeListener("connect", onConnect);
          appClient.socket.removeListener("error", onError);
          resolve();
        };

        const onError = (error: any) => {
          appClient.socket.removeListener("connect", onConnect);
          appClient.socket.removeListener("error", onError);
          log.error("Connection failed", { host: CIVIL3D_HOST, port: CIVIL3D_PORT });
          reject(
            new Error(
              `Failed to connect to Civil 3D plugin at ${CIVIL3D_HOST}:${CIVIL3D_PORT}. ` +
                `Make sure Civil 3D is running and the MCP plugin is loaded (NETLOAD).`
            )
          );
        };

        appClient.socket.on("connect", onConnect);
        appClient.socket.on("error", onError);

        appClient.connect();

        setTimeout(() => {
          appClient.socket.removeListener("connect", onConnect);
          appClient.socket.removeListener("error", onError);
          log.warn("Connection timed out", {
            host: CIVIL3D_HOST,
            port: CIVIL3D_PORT,
            timeoutMs: CONNECT_TIMEOUT_MS,
          });
          reject(
            new Error(
              `Connection to Civil 3D plugin timed out after ${CONNECT_TIMEOUT_MS}ms. ` +
                `Verify the plugin is running on ${CIVIL3D_HOST}:${CIVIL3D_PORT}.`
            )
          );
        }, CONNECT_TIMEOUT_MS);
      });
    }

    return await operation(appClient);
  } finally {
    appClient.disconnect();
  }
}
