import * as net from "net";
import { createLogger } from "./logger.js";

const log = createLogger("SocketClient");
const COMMAND_TIMEOUT_MS = parseInt(process.env.CIVIL3D_COMMAND_TIMEOUT ?? "120000", 10);
const MAX_RESPONSE_BYTES = 1_048_576;

export class ApplicationClientConnection {
  host: string;
  port: number;
  socket: net.Socket;
  isConnected: boolean = false;
  responseCallbacks: Map<string, (response: string) => void> = new Map();
  buffer: string = "";
  private readonly authToken: string;

  constructor(host: string, port: number, authToken: string) {
    this.host = host;
    this.port = port;
    this.authToken = authToken;
    this.socket = new net.Socket();
    this.setupSocketListeners();
  }

  private setupSocketListeners(): void {
    this.socket.on("connect", () => {
      this.isConnected = true;
      log.info("Connected", { host: this.host, port: this.port });
    });

    this.socket.on("data", (data) => {
      this.buffer += data.toString();
      if (this.buffer.length > MAX_RESPONSE_BYTES) {
        log.warn("Response too large; closing socket", { bytes: this.buffer.length });
        this.rejectAllPending(`Response exceeded ${MAX_RESPONSE_BYTES} bytes`);
        this.buffer = "";
        this.socket.destroy();
        return;
      }
      this.processBuffer();
    });

    this.socket.on("close", () => {
      this.isConnected = false;
      log.debug("Connection closed");
    });

    this.socket.on("error", (error) => {
      log.error("Connection error", { error: String(error) });
      this.isConnected = false;
    });
  }

  private rejectAllPending(reason: string): void {
    for (const [id, cb] of this.responseCallbacks) {
      cb(JSON.stringify({ id, error: { message: reason } }));
    }
    this.responseCallbacks.clear();
  }

  /**
   * Attempt to parse the buffer as a complete JSON object.
   * If parsing fails, the data is incomplete — wait for more.
   */
  private processBuffer(): void {
    try {
      JSON.parse(this.buffer);
      this.handleResponse(this.buffer);
      this.buffer = "";
    } catch {
      // Incomplete JSON — wait for more data
    }
  }

  public connect(): boolean {
    if (this.isConnected) {
      return true;
    }

    try {
      this.socket.connect(this.port, this.host);
      return true;
    } catch (error) {
      log.error("Failed to connect", { host: this.host, port: this.port, error: String(error) });
      return false;
    }
  }

  public disconnect(): void {
    this.socket.end();
    this.isConnected = false;
  }

  private generateRequestId(): string {
    return Date.now().toString() + Math.random().toString().substring(2, 8);
  }

  private handleResponse(responseData: string): void {
    try {
      const response = JSON.parse(responseData);
      const requestId = response.id || "default";

      const callback = this.responseCallbacks.get(requestId);
      if (callback) {
        callback(responseData);
        this.responseCallbacks.delete(requestId);
      }
    } catch (error) {
      log.error("Error parsing response", { error: String(error) });
    }
  }

  /**
   * Send a JSON-RPC command to the Civil 3D plugin and wait for a response.
   * The shared-secret auth token is attached to every request.
   */
  public sendCommand(command: string, params: any = {}): Promise<any> {
    return new Promise((resolve, reject) => {
      try {
        if (!this.isConnected) {
          this.connect();
        }

        const requestId = this.generateRequestId();

        const commandObj = {
          jsonrpc: "2.0",
          auth: this.authToken,
          method: command,
          params: params,
          id: requestId,
        };

        this.responseCallbacks.set(requestId, (responseData) => {
          try {
            const response = JSON.parse(responseData);
            if (response.error) {
              const code = response.error.code as string | undefined;
              const message = response.error.message || "Unknown error from Civil 3D plugin";
              if (code === "CIVIL3D.UNAUTHORIZED") {
                reject(
                  new Error(
                    `Civil 3D plugin rejected the auth token. ` +
                      `Restart Civil 3D and the MCP server so both reload the current token. ` +
                      `(${message})`
                  )
                );
              } else {
                reject(new Error(message));
              }
            } else {
              resolve(response.result);
            }
          } catch (error) {
            if (error instanceof Error) {
              reject(new Error(`Failed to parse response: ${error.message}`));
            } else {
              reject(new Error(`Failed to parse response: ${String(error)}`));
            }
          }
        });

        const commandString = JSON.stringify(commandObj);
        log.debug("Sending command", { method: command, requestId });
        this.socket.write(commandString);

        setTimeout(() => {
          if (this.responseCallbacks.has(requestId)) {
            this.responseCallbacks.delete(requestId);
            log.warn("Command timed out", {
              method: command,
              requestId,
              timeoutMs: COMMAND_TIMEOUT_MS,
            });
            reject(new Error(`Command timed out after ${COMMAND_TIMEOUT_MS}ms: ${command}`));
          }
        }, COMMAND_TIMEOUT_MS);
      } catch (error) {
        reject(error);
      }
    });
  }
}
