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

/** 期望的工具名（P3 工具面）。 */
const EXPECTED_TOOLS = [
  'dispatch_now',
  'input_request_answer',
  'input_request_cancel',
  'input_request_get',
  'input_request_list',
  'queue_get',
  'queue_list',
  'result_ack',
  'result_list',
  'scheduler_control',
  'scheduler_status',
  'task_upsert',
];

/** 期望的只读资源 URI（P3 资源面）。 */
const EXPECTED_RESOURCES = [
  'devhub://limits',
  'devhub://results/unread',
  'devhub://scheduler/queue',
  'devhub://scheduler/status',
  'devhub://tasks',
];

/** 摘要前缀；每个工具结果的最后一个文本块都必须是它。 */
const DIGEST_PREFIX = '— devhub · ';

/**
 * 从 tools/call 结果中取正文（第一个文本块），并断言尾部摘要存在且不超过 240 字符。
 * @param {any} result tools/call 的 result
 * @param {string} toolName 工具名（用于错误信息）
 * @returns {string} 正文文本
 */
function assertToolResult(result, toolName) {
  const blocks = (result?.content ?? []).filter((item) => item.type === 'text');
  if (blocks.length < 2) {
    throw new Error(`${toolName} 缺少尾部摘要块（实际文本块 ${blocks.length} 个）`);
  }

  const digest = blocks[blocks.length - 1].text;
  if (!digest.startsWith(DIGEST_PREFIX)) {
    throw new Error(`${toolName} 摘要块格式不符：${digest.slice(0, 60)}`);
  }
  if (digest.length > 240) {
    throw new Error(`${toolName} 摘要长度 ${digest.length} 超过 240`);
  }

  return blocks[0].text;
}

/** 主流程：握手 → 列工具/资源 → 调状态与队列 → 断言结构。 */
async function main() {
  const init = await waitForServer();
  console.log(`initialize OK：server=${init?.serverInfo?.name}@${init?.serverInfo?.version} protocol=${init?.protocolVersion}`);
  await notifyInitialized();

  const tools = await request('tools/list', {});
  const names = (tools?.tools ?? []).map((tool) => tool.name).sort();
  if (names.length === 0) throw new Error('tools/list 返回空');
  if (JSON.stringify(names) !== JSON.stringify(EXPECTED_TOOLS)) {
    throw new Error(`tools/list 不符合预期：${names.join(', ')}`);
  }
  console.log(`tools/list OK：${names.join(', ')}`);

  const resources = await request('resources/list', {});
  const uris = (resources?.resources ?? []).map((resource) => resource.uri).sort();
  if (JSON.stringify(uris) !== JSON.stringify(EXPECTED_RESOURCES)) {
    throw new Error(`resources/list 不符合预期：${uris.join(', ')}`);
  }
  console.log(`resources/list OK：${uris.join(', ')}`);

  const statusTool = tools.tools.find((tool) => tool.name === 'scheduler_status');
  if (statusTool?.inputSchema?.properties?.verbose) {
    throw new Error('scheduler_status 不应再有 verbose 参数（B2 已删除）');
  }

  const call = await request('tools/call', { name: 'scheduler_status', arguments: {} });
  const text = assertToolResult(call, 'scheduler_status');

  const snapshot = JSON.parse(text);
  for (const key of ['enabled', 'mode', 'phase', 'intervalSeconds', 'queue', 'providers', 'mcp', 'host', 'unread']) {
    if (!(key in snapshot)) throw new Error(`scheduler_status 缺少字段：${key}`);
  }
  if (snapshot.mcp?.port !== 3091) throw new Error(`mcp.port 应为 3091，实际：${snapshot.mcp?.port}`);
  if (snapshot.mode !== 'dispatch_only') throw new Error(`默认档位应为 dispatch_only，实际：${snapshot.mode}`);
  if (!Array.isArray(snapshot.providers) || snapshot.providers.length !== 2) {
    throw new Error(`providers 应包含 openai 与 vscode 两项，实际：${JSON.stringify(snapshot.providers)}`);
  }
  if (snapshot.host?.startedAt && !snapshot.host.startedAt.endsWith('Z')) {
    throw new Error(`时间字段必须统一 UTC + Z，实际：${snapshot.host.startedAt}`);
  }

  console.log(`tools/call OK：mode=${snapshot.mode} phase=${snapshot.phase} mcp.port=${snapshot.mcp.port} schema=v${snapshot.host?.schemaVersion}`);
  console.log(`providers = ${snapshot.providers.map((item) => `${item.provider}:${item.state}`).join(', ')}`);

  const queue = await request('tools/call', { name: 'queue_list', arguments: { limit: 5 } });
  const page = JSON.parse(assertToolResult(queue, 'queue_list'));
  if (!Array.isArray(page.items)) throw new Error('queue_list 返回体缺少 items 数组');
  console.log(`queue_list OK：items=${page.items.length} nextCursor=${page.nextCursor ?? 'null'}`);

  const results = await request('tools/call', { name: 'result_list', arguments: { unreadOnly: true, limit: 5 } });
  const resultPage = JSON.parse(assertToolResult(results, 'result_list'));
  if (!Array.isArray(resultPage.items)) throw new Error('result_list 返回体缺少 items 数组');
  console.log(`result_list OK：items=${resultPage.items.length} nextCursor=${resultPage.nextCursor ?? 'null'}`);

  const control = await request('tools/call', { name: 'scheduler_control', arguments: { action: 'status' } });
  const controlSnapshot = JSON.parse(assertToolResult(control, 'scheduler_control'));
  if (typeof controlSnapshot.enabled !== 'boolean') throw new Error('scheduler_control.status 缺少 enabled');
  console.log(`scheduler_control OK：action=status enabled=${controlSnapshot.enabled} mode=${controlSnapshot.mode}`);

  const statusResource = await request('resources/read', { uri: 'devhub://scheduler/status' });
  const resourceText = statusResource?.contents?.[0]?.text;
  if (!resourceText) throw new Error('resources/read 未返回内容');
  const resourceSnapshot = JSON.parse(resourceText);
  if (resourceSnapshot.mcp?.port !== 3091) throw new Error('devhub://scheduler/status 的 mcp.port 不为 3091');
  console.log('resources/read OK：devhub://scheduler/status');

  console.log('\nMCP 冒烟通过。');
}

main().catch((error) => {
  console.error(`MCP 冒烟失败：${error.message}`);
  process.exit(1);
});
