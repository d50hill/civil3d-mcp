// Quick end-to-end check: read the auth token, open a TCP connection to the
// plugin, send an authenticated getCivil3DHealth call, print the response.
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";

const tokenPath =
  process.env.CIVIL3D_TOKEN_PATH ??
  path.join(
    process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"),
    "civil3d-mcp",
    "token"
  );
console.log(`token path: ${tokenPath}`);

const token = fs.readFileSync(tokenPath, "utf-8").trim();
console.log(`token bytes: ${token.length}`);

const payload = JSON.stringify({
  jsonrpc: "2.0",
  auth: token,
  method: "getCivil3DHealth",
  id: 1,
});

const sock = net.createConnection({ host: "127.0.0.1", port: 8080 });
sock.setTimeout(10_000);

let buf = "";
sock.on("connect", () => sock.write(payload));
sock.on("data", (d) => {
  buf += d.toString();
  try {
    JSON.parse(buf);
    console.log("response:", buf);
    sock.end();
  } catch {
    // wait for more
  }
});
sock.on("timeout", () => {
  console.error("timeout — no response from plugin");
  sock.destroy();
  process.exit(2);
});
sock.on("error", (e) => {
  console.error("socket error:", e.message);
  process.exit(3);
});
