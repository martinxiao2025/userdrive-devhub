#!/usr/bin/env node
/**
 * MCP 冒烟：对运行中的 devhubd 做真实 JSON-RPC 握手（initialize → notifications/initialized → tools/list → tools/call）。
 * 支持 JSON 与 SSE 两种响应形态；无状态 streamable-http 下不依赖 Mcp-Session-Id。
 *
 * 用法：node scripts/smoke-mcp.mjs [url]  默认 http://127.0.0.1:3091/mcp
 */
const url = process.argv[2] ?? 'http://127.0.0.1:3091/mcp';
const PROTOCOL_VERSION = '2025-06-18';

let sessionId = null;
let nextId = 1;

/**
 * 解析响应体，兼容 application/json 与 text/event-stream。
 * @param {Response} response fetch 响应
 * @returns {Promise<unknown>} JSON-RPC 负载
 */
async function readJsonRpc(response) {
  const contentType = response.headers.get('content-type') ?? '';
  const text = await response.text();

  if (contentType.includes('text/event-stream')) {
    const payloads = text
      .split(/\r?\n/)
      .filter((line) => line.startsWith('data:'))
      .map((line) => line.slice(5).trim())
      .filter((line) => line.length > 0);
    if (payloads.length === 0) throw new Error(`SSE 响应为空：${text.slice(0, 200)}`);
    return JSON.parse(payloads[payloads.length - 1]);
  }

  if (!text) return null;
  return JSON.parse(text);
}

/**
 * 发送一条 JSON-RPC 请求。
 * @param {string} method 方法名
 * @param {unknown} params 参数
 * @returns {Promise<any>} result
 */
async function request(method, params) {
  const response = await fetch(url, {
    method: 'POST',
    headers: buildHeaders(),
    body: JSON.stringify({ jsonrpc: '2.0', id: nextId++, method, params }),
  });

  sessionId = response.headers.get('mcp-session-id') ?? sessionId;

  if (!response.ok) {
    const body = await response.text();
    throw new Error(`${method} HTTP ${response.status}：${body.slice(0, 300)}`);
  }

  const payload = await readJsonRpc(response);
  if (!payload) throw new Error(`${method} 响应为空`);
  if (payload.error) throw new Error(`${method} 返回错误：${JSON.stringify(payload.error)}`);
  return payload.result;
}

/** 构造请求头；有会话时透传 Mcp-Session-Id。 */
function buildHeaders() {
  const headers = {
    'content-type': 'application/json',
    accept: 'application/json, text/event-stream',
  };
  if (sessionId) headers['mcp-session-id'] = sessionId;
  return headers;
}

/** 等待守护进程就绪，最多约 15 秒。 */
async function waitForServer() {
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' },
        body: JSON.stringify({
          jsonrpc: '2.0',
          id: nextId++,
          method: 'initialize',
          params: {
            protocolVersion: PROTOCOL_VERSION,
            capabilities: {},
            clientInfo: { name: 'devhub-smoke', version: '0.0.0' },
          },
        }),
      });
      if (response.ok) {
        sessionId = response.headers.get('mcp-session-id') ?? sessionId;
        const payload = await readJsonRpc(response);
        return payload.result;
      }
    } catch {
      // 服务未就绪，继续等待。
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`连接超时：${url}`);
}

/** 发送 initialized 通知（无 id，不需要响应体）。 */
async function notifyInitialized() {
  await fetch(url, {
    method: 'POST',
    headers: buildHeaders(),
    body: JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }),
  });
}

/** 主流程：握手 → 列工具 → 调 scheduler_status → 断言结构。 */
async function main() {
  const init = await waitForServer();
  console.log(`initialize OK：server=${init?.serverInfo?.name}@${init?.serverInfo?.version} protocol=${init?.protocolVersion}`);
  await notifyInitialized();

  const tools = await request('tools/list', {});
  const names = (tools?.tools ?? []).map((tool) => tool.name);
  if (names.length === 0) throw new Error('tools/list 返回空');
  if (!names.includes('scheduler_status')) throw new Error(`缺少 scheduler_status，实际：${names.join(', ')}`);
  console.log(`tools/list OK：${names.join(', ')}`);

  const statusTool = tools.tools.find((tool) => tool.name === 'scheduler_status');
  const verbose = statusTool?.inputSchema?.properties?.verbose;
  if (!verbose || verbose.type !== 'boolean') throw new Error('scheduler_status.inputSchema 缺少 boolean verbose');

  const call = await request('tools/call', { name: 'scheduler_status', arguments: {} });
  const text = call?.content?.find((item) => item.type === 'text')?.text;
  if (!text) throw new Error('scheduler_status 未返回 text 内容');

  const snapshot = JSON.parse(text);
  for (const key of ['enabled', 'mode', 'phase', 'intervalSeconds', 'queue', 'providers', 'mcp', 'host', 'unread']) {
    if (!(key in snapshot)) throw new Error(`scheduler_status 缺少字段：${key}`);
  }
  if (snapshot.mcp?.port !== 3091) throw new Error(`mcp.port 应为 3091，实际：${snapshot.mcp?.port}`);
  if (snapshot.mode !== 'dispatch_only') throw new Error(`默认档位应为 dispatch_only，实际：${snapshot.mode}`);

  console.log(`tools/call OK：mode=${snapshot.mode} phase=${snapshot.phase} mcp.port=${snapshot.mcp.port} schema=v${snapshot.host?.schemaVersion}`);
  console.log(`scheduler_status = ${text}`);
  console.log('\nMCP 冒烟通过。');
}

main().catch((error) => {
  console.error(`MCP 冒烟失败：${error.message}`);
  process.exit(1);
});
