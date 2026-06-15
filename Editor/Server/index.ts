import {
    McpServer,
    WebStandardStreamableHTTPServerTransport,
} from "@modelcontextprotocol/server";
import * as z from "zod";
import { appendFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

// This process is detached from Unity and outlives its AppDomain reloads. Its stdout/stderr are
// NOT redirected by Unity (a redirected pipe would deadlock once the draining domain reloads), so
// the inherited handles may be invalid. Route all logging to a file and never write to the real
// stdout/stderr, and never let logging or a stray async error crash the server.
// The log lives in the OS temp dir (NOT inside the package) so Unity never imports it as an asset.
const LOG_FILE = join(tmpdir(), "unislop-bun.log");

function logToFile(level: string, args: unknown[]): void {
    try {
        const text = args
            .map((a) => {
                if (typeof a === "string") return a;
                try {
                    return JSON.stringify(a);
                } catch {
                    return String(a);
                }
            })
            .join(" ");
        appendFileSync(LOG_FILE, `[${new Date().toISOString()}] [${level}] ${text}\n`);
    } catch {
        // Logging must never throw.
    }
}

console.log = (...args: unknown[]) => logToFile("log", args);
console.error = (...args: unknown[]) => logToFile("error", args);
console.warn = (...args: unknown[]) => logToFile("warn", args);

process.on("uncaughtException", (e) => logToFile("uncaughtException", [String((e as Error)?.stack ?? e)]));
process.on("unhandledRejection", (e) => logToFile("unhandledRejection", [String(e)]));

const UNITY_API_URL = "http://127.0.0.1:5108/";
const MCP_PORT = Number(process.env.MCP_PORT) || 5107;

// Overall budget for a tool call, covering one or more Unity domain reloads.
const JOB_TIMEOUT_MS = 300_000;
// How often we poll Unity for job status.
const POLL_INTERVAL_MS = 400;
// While Unity is reloading its AppDomain the internal API is down; keep retrying.
const RECONNECT_INTERVAL_MS = 300;
// A single HTTP call to Unity should be quick; the long wait is the poll loop.
const CALL_TIMEOUT_MS = 15_000;

let agentNotified = false;

interface UnityResponse {
    status: "success" | "error";
    message: string;
    data?: Record<string, unknown>;
}

const server = new McpServer({
    name: "UniSlop",
    version: "1.0.0",
});

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// Mark a Unity-reported error so callUnityResilient knows it is final and must not retry.
function asUnityError(message: string): Error {
    const e = new Error(message) as Error & { unityError?: boolean };
    e.unityError = true;
    return e;
}

// Single POST to the Unity in-process API. Throws a tagged error on a Unity-reported error
// status, or a plain (transport) error if Unity is unreachable (e.g. mid-domain-reload).
async function callUnity(
    command: string,
    params: Record<string, unknown> = {},
): Promise<UnityResponse> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), CALL_TIMEOUT_MS);

    try {
        const response = await fetch(UNITY_API_URL, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ command, ...params }),
            signal: controller.signal,
        });

        const result = (await response.json()) as UnityResponse;
        if (result.status === "error") throw asUnityError(result.message);
        return result;
    } finally {
        clearTimeout(timeout);
    }
}

// Like callUnity, but tolerates the connection dropping while Unity reloads its AppDomain.
// Retries transport failures until the deadline; surfaces Unity error statuses immediately.
async function callUnityResilient(
    command: string,
    params: Record<string, unknown>,
    deadline: number,
): Promise<UnityResponse> {
    let lastTransportError: unknown;
    while (Date.now() < deadline) {
        try {
            return await callUnity(command, params);
        } catch (e) {
            if (e instanceof Error && (e as { unityError?: boolean }).unityError) throw e;
            lastTransportError = e;
            await sleep(RECONNECT_INTERVAL_MS);
        }
    }
    throw new Error(
        `Unity did not respond within ${JOB_TIMEOUT_MS / 1000}s (last error: ${lastTransportError})`,
    );
}

async function notifyAgentConnected() {
    if (agentNotified) return;
    agentNotified = true;
    try {
        await callUnity("agent_connected");
    } catch {
        // Unity may not be ready yet; toolbar updates on the next tool call.
    }
}

function formatUnityResult(result: UnityResponse): string {
    if (!result.data) return result.message;
    return `${result.message}\n\n${JSON.stringify(result.data, null, 2)}`;
}

// Drives a Unity job that may span domain reloads: kick it off, then poll status until done.
async function runJob(
    startCommand: string,
    statusCommand: string,
    params: Record<string, unknown>,
): Promise<UnityResponse> {
    const deadline = Date.now() + JOB_TIMEOUT_MS;

    const started = await callUnityResilient(startCommand, params, deadline);
    if (started.status === "error") throw asUnityError(started.message);

    // The start call itself may already report a terminal result (e.g. nothing to compile).
    if ((started.data?.state as string) === "done") return started;

    while (Date.now() < deadline) {
        await sleep(POLL_INTERVAL_MS);
        // A status poll is never terminal: during a domain reload Unity may be unreachable
        // or briefly report "reloading". Swallow any error and keep polling until the job
        // reports a terminal state or the deadline passes.
        try {
            const status = await callUnity(statusCommand, {});
            const state = status.data?.state as string | undefined;
            if (state === "done" || state === "idle") return status;
        } catch {
            // transient — Unity is mid-reload or the socket dropped; retry.
        }
    }

    throw new Error(`Job '${startCommand}' did not finish within ${JOB_TIMEOUT_MS / 1000}s`);
}

server.registerTool(
    "unity_compile",
    {
        description:
            "Compile the Unity project's C# scripts in the Unity Editor. Triggers a Unity domain reload. wait:true (default) waits through the reload and returns compiler errors.",
        inputSchema: z.object({
            wait: z
                .boolean()
                .optional()
                .default(true)
                .describe(
                    "When true, wait for compilation to finish (across domain reload) and return errors. When false, only request compilation.",
                ),
        }),
    },
    async ({ wait }) => {
        await notifyAgentConnected();
        try {
            if (!wait) {
                const result = await callUnity("compile_start", { wait: false });
                return { content: [{ type: "text", text: formatUnityResult(result) }] };
            }

            const result = await runJob("compile_start", "compile_status", { wait: true });
            const errorCount = (result.data?.errorCount as number) ?? 0;
            return {
                content: [{ type: "text", text: formatUnityResult(result) }],
                isError: errorCount > 0,
            };
        } catch (e) {
            return {
                content: [{ type: "text", text: `Compile failed: ${e}` }],
                isError: true,
            };
        }
    },
);

server.registerTool(
    "unity_run_tests",
    {
        description:
            "Run Unity tests via the Unity Test Runner and return pass/fail counts with failure details. Defaults to running all tests (Edit Mode + Play Mode).",
        inputSchema: z.object({
            mode: z
                .enum(["all", "editmode", "playmode"])
                .optional()
                .default("all")
                .describe("Which tests to run: 'all' (default) runs both Edit Mode and Play Mode."),
            filter: z
                .string()
                .optional()
                .describe("Optional test name filter passed to the Unity Test Runner."),
        }),
    },
    async ({ mode, filter }) => {
        await notifyAgentConnected();
        try {
            const result = await runJob("run_tests_start", "run_tests_status", { mode, filter });
            const failed = (result.data?.failed as number) ?? 0;
            return {
                content: [{ type: "text", text: formatUnityResult(result) }],
                isError: failed > 0,
            };
        } catch (e) {
            return {
                content: [{ type: "text", text: `Tests failed: ${e}` }],
                isError: true,
            };
        }
    },
);

server.registerTool(
    "unity_list_tests",
    {
        description:
            "List available Unity tests (Edit Mode and Play Mode) from the Unity Test Runner. Player/build tests are not enumerable from the Unity Editor.",
        inputSchema: z.object({}),
    },
    async () => {
        await notifyAgentConnected();
        try {
            const deadline = Date.now() + JOB_TIMEOUT_MS;
            const result = await callUnityResilient("list_tests", {}, deadline);
            if (result.status === "error") throw new Error(result.message);
            return { content: [{ type: "text", text: formatUnityResult(result) }] };
        } catch (e) {
            return {
                content: [{ type: "text", text: `List tests failed: ${e}` }],
                isError: true,
            };
        }
    },
);

async function main() {
    const transport = new WebStandardStreamableHTTPServerTransport({
        sessionIdGenerator: undefined,
    });

    await server.connect(transport);

    console.error(`UniSlop MCP Server starting on port ${MCP_PORT}...`);

    Bun.serve({
        port: MCP_PORT,
        async fetch(req, srv) {
            const url = new URL(req.url);
            if (url.pathname === "/mcp") {
                srv.timeout(req, 0);
                if (req.method === "POST" || req.method === "GET") notifyAgentConnected();
                return transport.handleRequest(req);
            }
            return new Response("Not Found", { status: 404 });
        },
    });

    console.error(`UniSlop MCP Server running at http://localhost:${MCP_PORT}/mcp`);
}

main().catch((e) => {
    console.error("Unhandled exception in main():", e);
    process.exit(1);
});
