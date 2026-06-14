import {
    McpServer,
    WebStandardStreamableHTTPServerTransport,
} from "@modelcontextprotocol/server";
import * as z from "zod";

const UNITY_API_URL = "http://127.0.0.1:5108/";
const MCP_PORT = Number(process.env.MCP_PORT) || 5107;
const UNITY_TIMEOUT_MS = 300_000;

let agentNotified = false;

interface UnityResponse {
    status: "success" | "error";
    message: string;
    data?: Record<string, unknown>;
}

async function notifyAgentConnected() {
    if (agentNotified) return;
    agentNotified = true;
    console.error("UniSlop: Agent connected");
    try {
        await callUnity("agent_connected");
    } catch {
        // Unity may not be ready yet; toolbar updates on next tool call.
    }
}

const server = new McpServer({
    name: "UniSlop",
    version: "1.0.0",
});

async function callUnity(
    command: string,
    params: Record<string, unknown> = {},
): Promise<UnityResponse> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), UNITY_TIMEOUT_MS);

    try {
        const response = await fetch(UNITY_API_URL, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ command, ...params }),
            signal: controller.signal,
        });

        const result = (await response.json()) as UnityResponse;
        if (result.status === "error") {
            throw new Error(result.message);
        }
        return result;
    } catch (e) {
        if (e instanceof Error && e.name === "AbortError") {
            throw new Error(`Unity API timed out after ${UNITY_TIMEOUT_MS / 1000}s`);
        }
        throw new Error(`Unity API connection failed: ${e}`);
    } finally {
        clearTimeout(timeout);
    }
}

function formatUnityResult(result: UnityResponse): string {
    if (!result.data) return result.message;
    return `${result.message}\n\n${JSON.stringify(result.data, null, 2)}`;
}

server.registerTool(
    "compile",
    {
        description:
            "Compile Unity C# scripts. By default waits for compilation to finish and returns compiler errors.",
        inputSchema: z.object({
            wait: z
                .boolean()
                .optional()
                .default(true)
                .describe(
                    "When true, wait for compilation to finish and return errors. When false, only request compilation.",
                ),
        }),
    },
    async ({ wait }) => {
        try {
            const result = await callUnity("compile", { wait });
            return {
                content: [{ type: "text", text: formatUnityResult(result) }],
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
    "run_tests",
    {
        description:
            "Run Unity tests via the Test Runner and return pass/fail counts with failure details.",
        inputSchema: z.object({
            mode: z
                .enum(["editmode", "playmode"])
                .optional()
                .default("editmode")
                .describe("Which tests to run."),
            filter: z
                .string()
                .optional()
                .describe("Optional test name filter passed to the Unity Test Runner."),
        }),
    },
    async ({ mode, filter }) => {
        try {
            const result = await callUnity("run_tests", { mode, filter });
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

async function main() {
    const transport = new WebStandardStreamableHTTPServerTransport({
        sessionIdGenerator: undefined,
    });

    await server.connect(transport);

    console.error(`UniSlop MCP Server starting on port ${MCP_PORT}...`);

    Bun.serve({
        port: MCP_PORT,
        async fetch(req, server) {
            const url = new URL(req.url);
            if (url.pathname === "/mcp") {
                server.timeout(req, 0);
                if (req.method === "POST" || req.method === "GET") {
                    notifyAgentConnected();
                }
                return transport.handleRequest(req);
            }
            return new Response("Not Found", { status: 404 });
        },
    });

    console.error(
        `UniSlop MCP Server running at http://localhost:${MCP_PORT}/mcp`,
    );
}

main().catch((e) => {
    console.error("Unhandled exception in main():", e);
    process.exit(1);
});
