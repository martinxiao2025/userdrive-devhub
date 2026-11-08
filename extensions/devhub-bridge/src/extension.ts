/**
 * DevHub Bridge：把 VS Code 的 `vscode.lm` 暴露给 DevHub 守护进程。
 *
 * 职责只有两件（薄层，不做调度）：
 * 1) 回合桥：监听回环随机端口，接收 devhubd 的 /turns 请求，驱动 vscode.lm 生成，
 *    工具调用回调 devhubd 的 /internal/tools/call；把端口 / 令牌 / pid 写进 lm-bridge.json。
 * 2) 通知轮询：仅在开启时每 60 秒拉一次未读结果，按 id 去重，最多提示 3 条标题。
 *
 * 安全：只监听 127.0.0.1；所有请求校验 Bearer 令牌；令牌不写日志、不进设置。
 */
import * as vscode from 'vscode';
import * as http from 'node:http';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as crypto from 'node:crypto';
import type { AddressInfo } from 'node:net';

/** 回合状态。 */
interface TurnRecord {
  id: string;
  state: 'running' | 'done' | 'failed' | 'cancelled';
  text: string;
  toolCalls: number;
  inputTokens?: number;
  outputTokens?: number;
  /** provider 表示数值来自 vscode.lm 的真实 usage；none 表示拿不到，已如实留空。 */
  usageSource?: 'provider' | 'none';
  filesChanged: string[];
  errorKind?: string;
  errorCode?: string;
  errorMessage?: string;
  cts: vscode.CancellationTokenSource;
  finished: Promise<void>;
  resolveFinished: () => void;
}

/** devhubd 下发的工具描述。 */
interface TurnTool {
  name: string;
  description: string;
  inputSchema: string;
}

/** devhubd 下发的回合请求。 */
interface TurnRequest {
  turnId: string;
  sessionId: string;
  model?: string;
  systemPrompt: string;
  userPrompt: string;
  maxRounds: number;
  timeoutSeconds: number;
  tools: TurnTool[];
  toolCallback: { url: string; token: string };
}

/** 工具回调的返回体。 */
interface ToolCallResponse {
  content?: string;
  isError?: boolean;
  code?: string;
  message?: string;
  changedFiles?: string[];
}

let bridgeServer: http.Server | undefined;
let bridgeToken: string | undefined;
let notificationTimer: NodeJS.Timeout | undefined;
/** 通知序号游标（globalState 持久化），保证窗口重载与守护进程重启后不重复提醒。 */
const notificationCursorKey = 'devhub.noticeSeq';
/** 扩展侧内存去重：只作为二次保护，权威依据是数据库的 seq 与 acked_at。 */
const seenNotificationSeqs = new Set<number>();

/**
 * 扩展激活入口。
 * @param context 扩展上下文
 */
export function activate(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.commands.registerCommand('devhub.bridge.restart', async () => {
      await stopBridge();
      await startBridge(context);
    }),
  );

  vscode.workspace.onDidChangeConfiguration((event) => {
    if (event.affectsConfiguration('devhub.bridge.enabled')) {
      void (isBridgeEnabled() ? startBridge(context) : stopBridge());
    }
    if (event.affectsConfiguration('devhub.notifications.enabled')) {
      restartNotificationPolling(context);
    }
  }, undefined, context.subscriptions);

  if (isBridgeEnabled()) {
    void startBridge(context);
  }
  restartNotificationPolling(context);
}

/**
 * 扩展停用：关闭桥并清理发现文件。
 */
export async function deactivate(): Promise<void> {
  await stopBridge();
  if (notificationTimer) {
    clearInterval(notificationTimer);
    notificationTimer = undefined;
  }
}

/** 桥开关。 */
function isBridgeEnabled(): boolean {
  return vscode.workspace.getConfiguration('devhub').get<boolean>('bridge.enabled', false);
}

/** 通知开关。 */
function isNotificationsEnabled(): boolean {
  return vscode.workspace.getConfiguration('devhub').get<boolean>('notifications.enabled', false);
}

/**
 * DevHub 数据目录：优先设置项，其次 %LOCALAPPDATA%\userdrive-devhub。
 */
function dataDirectory(): string {
  const configured = vscode.workspace.getConfiguration('devhub').get<string>('dataDir', '');
  if (configured.trim().length > 0) {
    return configured.trim();
  }
  const localAppData = process.env.LOCALAPPDATA;
  if (localAppData) {
    return path.join(localAppData, 'userdrive-devhub');
  }
  return path.join(os.homedir(), '.userdrive-devhub');
}

/**
 * 启动回合桥：选模型 → 监听 127.0.0.1:0 → 写 lm-bridge.json。
 * @param context 扩展上下文
 */
async function startBridge(context: vscode.ExtensionContext): Promise<void> {
  if (bridgeServer) {
    return;
  }

  const model = await selectTargetModel();
  if (!model) {
    return;
  }

  bridgeToken = crypto.randomBytes(32).toString('hex');
  const token = bridgeToken;

  const server = http.createServer((request, response) => {
    void handleRequest(request, response, token, model);
  });

  try {
    await new Promise<void>((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', () => resolve());
    });
  } catch (error) {
    void vscode.window.showErrorMessage(`DevHub：回合桥监听失败（${(error as Error).message}）。`);
    return;
  }

  bridgeServer = server;
  const port = (server.address() as AddressInfo).port;
  writeDiscoveryFile(port, token, model);
  context.subscriptions.push({ dispose: () => void stopBridge() });
  void vscode.window.showInformationMessage(
    `DevHub 回合桥已启动（端口 ${port}，模型 ${model.name} / ${model.id}）。`,
  );
}

/** 目标模型 id：优先精确匹配该值。 */
const TARGET_MODEL_ID = 'deepseek-flash';

/** 目标模型显示名：id / family 都匹配不上时的最后兜底。 */
const TARGET_MODEL_NAME = 'DeepSeek V4.1 Flash';

/** 模型的可读描述（用于失败时输出完整清单）。 */
interface ModelDescription {
  id: string;
  family: string;
  name: string;
  vendor: string;
}

/**
 * 按 id → family → name 的顺序选择目标模型。
 * 找不到目标时不回退到其它模型，写入可用模型清单并提示用户。
 */
async function selectTargetModel(): Promise<vscode.LanguageModelChat | undefined> {
  let models: vscode.LanguageModelChat[];
  try {
    // 不限定 vendor：第三方 provider 注册的模型也能被纳入清单。
    models = await vscode.lm.selectChatModels({});
  } catch (error) {
    writeModelDiagnostics([], `selectChatModels 失败：${(error as Error).message}`);
    void vscode.window.showErrorMessage(`DevHub：读取 vscode.lm 模型失败（${(error as Error).message}）。`);
    return undefined;
  }

  const chosen =
    models.find((candidate) => candidate.id === TARGET_MODEL_ID) ??
    models.find((candidate) => candidate.family === TARGET_MODEL_ID) ??
    models.find((candidate) => candidate.name === TARGET_MODEL_NAME);

  if (chosen) {
    return chosen;
  }

  const available = models.map(describeModel);
  writeModelDiagnostics(available, `未找到目标模型 ${TARGET_MODEL_ID} / ${TARGET_MODEL_NAME}`);
  const listing =
    available.length === 0
      ? '（没有任何可用模型）'
      : available.map((item) => `${item.id}｜${item.family}｜${item.name}｜${item.vendor}`).join('；');
  void vscode.window.showErrorMessage(
    `DevHub：未找到 ${TARGET_MODEL_NAME}（id=${TARGET_MODEL_ID}），回合桥未启动。可用模型：${listing}`,
  );
  return undefined;
}

/** 把 vscode.lm 模型转成可输出的描述。 */
function describeModel(model: vscode.LanguageModelChat): ModelDescription {
  return { id: model.id, family: model.family, name: model.name, vendor: model.vendor };
}

/**
 * 选不到模型时写出诊断文件，便于在不看扩展宿主日志的情况下定位。
 * @param available 实际可用模型清单
 * @param reason 失败原因
 */
function writeModelDiagnostics(available: ModelDescription[], reason: string): void {
  try {
    const directory = dataDirectory();
    fs.mkdirSync(directory, { recursive: true });
    const target = path.join(directory, 'lm-bridge.error.json');
    const temporary = `${target}.${process.pid}.tmp`;
    const payload = {
      reason,
      targetId: TARGET_MODEL_ID,
      targetName: TARGET_MODEL_NAME,
      availableModels: available,
      at: new Date().toISOString(),
    };
    fs.writeFileSync(temporary, JSON.stringify(payload, null, 2), { encoding: 'utf8' });
    fs.renameSync(temporary, target);
  } catch {
    // 诊断文件写失败不影响主流程。
  }
}

/**
 * 停止桥并删除发现文件（仅删除自己写的那份）。
 */
async function stopBridge(): Promise<void> {
  if (bridgeServer) {
    const server = bridgeServer;
    bridgeServer = undefined;
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }

  const discoveryPath = path.join(dataDirectory(), 'lm-bridge.json');
  try {
    if (bridgeToken && fs.existsSync(discoveryPath)) {
      const payload = JSON.parse(fs.readFileSync(discoveryPath, 'utf8')) as { token?: string };
      if (payload.token === bridgeToken) {
        fs.unlinkSync(discoveryPath);
      }
    }
  } catch {
    // 清理失败不影响停用；下次启动会覆盖。
  }

  bridgeToken = undefined;
}

/**
 * 原子写出发现文件。
 * @param port 监听端口
 * @param token 访问令牌
 * @param model 选中的模型
 */
function writeDiscoveryFile(port: number, token: string, model: vscode.LanguageModelChat): void {
  const directory = dataDirectory();
  fs.mkdirSync(directory, { recursive: true });
  const target = path.join(directory, 'lm-bridge.json');
  const temporary = `${target}.${process.pid}.tmp`;
  const payload = {
    pid: process.pid,
    port,
    token,
    model: model.name,
    modelId: model.id,
    modelFamily: model.family,
    modelVendor: model.vendor,
    startedAt: new Date().toISOString(),
  };
  fs.writeFileSync(temporary, JSON.stringify(payload), { encoding: 'utf8' });
  fs.renameSync(temporary, target);
}

/**
 * 分发 HTTP 请求。
 */
async function handleRequest(
  request: http.IncomingMessage,
  response: http.ServerResponse,
  token: string,
  model: vscode.LanguageModelChat,
): Promise<void> {
  const url = new URL(request.url ?? '/', 'http://127.0.0.1');
  if (!isAuthorized(request, token)) {
    sendJson(response, 401, { code: 'UNAUTHORIZED', message: '令牌无效' });
    return;
  }

  const turns = activeTurns;

  if (request.method === 'GET' && url.pathname === '/health') {
    sendJson(response, 200, { ok: true, pid: process.pid, model: model.name });
    return;
  }

  if (request.method === 'POST' && url.pathname === '/turns') {
    const body = await readBody(request);
    const turnRequest = JSON.parse(body) as TurnRequest;
    const record = createTurn(turnRequest);
    void runTurn(record, turnRequest, model);
    sendJson(response, 202, { turnId: record.id });
    return;
  }

  if (request.method === 'GET' && url.pathname.startsWith('/turns/')) {
    const turnId = url.pathname.slice('/turns/'.length);
    const record = turns.get(turnId);
    if (!record) {
      sendJson(response, 404, { state: 'failed', errorKind: 'bridge_down', errorCode: 'UNKNOWN_TURN' });
      return;
    }
    const waitMs = Math.min(Number(url.searchParams.get('wait') ?? '0') * 1000, 30_000);
    if (record.state === 'running' && waitMs > 0) {
      await Promise.race([record.finished, delay(waitMs)]);
    }
    sendJson(response, 200, serializeTurn(record));
    return;
  }

  if (request.method === 'DELETE' && url.pathname.startsWith('/turns/')) {
    const turnId = url.pathname.slice('/turns/'.length);
    const record = turns.get(turnId);
    if (record) {
      record.cts.cancel();
      record.state = 'cancelled';
      record.errorKind = 'user_cancelled';
      record.errorCode = 'USER_CANCELLED';
      record.resolveFinished();
    }
    sendJson(response, 200, { ok: true });
    return;
  }

  sendJson(response, 404, { code: 'NOT_FOUND' });
}

/** 活跃回合表。 */
const activeTurns = new Map<string, TurnRecord>();

/**
 * 创建回合记录。
 */
function createTurn(request: TurnRequest): TurnRecord {
  let resolveFinished: () => void = () => undefined;
  const finished = new Promise<void>((resolve) => {
    resolveFinished = resolve;
  });

  const record: TurnRecord = {
    id: request.turnId,
    state: 'running',
    text: '',
    toolCalls: 0,
    filesChanged: [],
    cts: new vscode.CancellationTokenSource(),
    finished,
    resolveFinished,
  };
  activeTurns.set(record.id, record);
  return record;
}

/** 回合结果的线上形态（与 C# 端 camelCase 契约一致）。 */
function serializeTurn(record: TurnRecord): Record<string, unknown> {
  return {
    state: record.state,
    text: record.text,
    toolCalls: record.toolCalls,
    inputTokens: record.inputTokens,
    outputTokens: record.outputTokens,
    usageSource: record.usageSource,
    filesChanged: record.filesChanged,
    errorKind: record.errorKind,
    errorCode: record.errorCode,
    errorMessage: record.errorMessage,
  };
}

/**
 * 读取 vscode.lm 响应上的 usage。当前 d.ts（1.95）未声明该字段，
 * 用运行时探测兼容新版本：存在且字段完整才返回，否则返回 undefined。
 * @param response 模型响应
 */
function readResponseUsage(
  response: vscode.LanguageModelChatResponse,
): { promptTokens: number; completionTokens: number } | undefined {
  const usage = (response as unknown as {
    usage?: { promptTokens?: number; completionTokens?: number };
  }).usage;
  if (!usage || typeof usage.promptTokens !== 'number' || typeof usage.completionTokens !== 'number') {
    return undefined;
  }
  return { promptTokens: usage.promptTokens, completionTokens: usage.completionTokens };
}

/**
 * 驱动一个回合：循环请求模型，遇到工具调用就回调 devhubd 执行，直到无工具调用或达到轮数上限。
 */
async function runTurn(record: TurnRecord, request: TurnRequest, model: vscode.LanguageModelChat): Promise<void> {
  const tools: vscode.LanguageModelChatTool[] = request.tools.map((tool) => ({
    name: tool.name,
    description: tool.description,
    inputSchema: safeParseSchema(tool.inputSchema),
  }));

  const messages: vscode.LanguageModelChatMessage[] = [
    vscode.LanguageModelChatMessage.User(`${request.systemPrompt}\n\n${request.userPrompt}`),
  ];

  const deadline = Date.now() + Math.max(1, request.timeoutSeconds) * 1000;
  // 多轮请求各自带回 usage，需要跨轮累加；任意一轮缺失则整回合按"拿不到"处理。
  let promptTokens = 0;
  let completionTokens = 0;
  let usageSeen = false;
  let usageMissing = false;

  try {
    for (let round = 0; round < Math.max(1, request.maxRounds); round++) {
      if (Date.now() > deadline) {
        fail(record, 'budget_exceeded', 'TURN_TIMEOUT', '回合超过时间预算');
        return;
      }

      const response = await model.sendRequest(
        messages,
        { justification: 'DevHub 定时任务需要调用当前 VS Code 语言模型。', tools },
        record.cts.token,
      );

      let text = '';
      const calls: vscode.LanguageModelToolCallPart[] = [];
      for await (const part of response.stream) {
        if (part instanceof vscode.LanguageModelTextPart) {
          text += part.value;
        } else if (part instanceof vscode.LanguageModelToolCallPart) {
          calls.push(part);
        }
      }

      const roundUsage = readResponseUsage(response);
      if (roundUsage) {
        promptTokens += roundUsage.promptTokens;
        completionTokens += roundUsage.completionTokens;
        usageSeen = true;
      } else {
        usageMissing = true;
      }

      if (calls.length === 0) {
        record.text = text;
        if (usageSeen && !usageMissing) {
          record.inputTokens = promptTokens;
          record.outputTokens = completionTokens;
          record.usageSource = 'provider';
        } else {
          // 拿不到真实 usage 时如实留空，不用字符数估算冒充 token。
          record.usageSource = 'none';
        }
        record.state = 'done';
        record.resolveFinished();
        return;
      }

      const results: vscode.LanguageModelToolResultPart[] = [];
      for (const call of calls) {
        const result = await callTool(request.toolCallback, request.turnId, call.name, call.input);
        record.toolCalls++;
        if (Array.isArray(result.changedFiles)) {
          record.filesChanged.push(...result.changedFiles);
        }
        const content = result.isError
          ? `工具执行失败（${result.code ?? 'ERROR'}）：${result.message ?? ''}`
          : result.content ?? '';
        results.push(
          new vscode.LanguageModelToolResultPart(call.callId, [
            new vscode.LanguageModelTextPart(content),
          ]),
        );
      }

      // 工具调用由助手发出，工具结果必须以用户消息回填（LanguageModelChatMessage.Assistant 不接受 ToolResultPart）。
      messages.push(vscode.LanguageModelChatMessage.Assistant(calls));
      messages.push(vscode.LanguageModelChatMessage.User(results));
    }

    fail(record, 'budget_exceeded', 'MAX_TOOL_ROUNDS', '达到工具轮数上限');
  } catch (error) {
    if (record.cts.token.isCancellationRequested) {
      fail(record, 'user_cancelled', 'USER_CANCELLED', '回合被取消');
      return;
    }

    const mapped = mapLanguageModelError(error);
    fail(record, mapped.kind, mapped.code, mapped.message);
  } finally {
    record.cts.dispose();
    // 回合保留 5 分钟供轮询取终态，随后回收，避免内存无界增长。
    setTimeout(() => activeTurns.delete(record.id), 5 * 60_000).unref();
  }
}

/** 标记回合失败。 */
function fail(record: TurnRecord, kind: string, code: string, message: string): void {
  record.state = 'failed';
  record.errorKind = kind;
  record.errorCode = code;
  record.errorMessage = message;
  record.resolveFinished();
}

/**
 * 调用 devhubd 内部工具端点。
 */
async function callTool(
  callback: { url: string; token: string },
  turnId: string,
  name: string,
  input: unknown,
): Promise<ToolCallResponse> {
  const response = await fetch(callback.url, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${callback.token}` },
    body: JSON.stringify({ turnId, name, arguments: input ?? {} }),
  });
  if (!response.ok) {
    return { isError: true, code: `HTTP_${response.status}`, message: '内部工具端点拒绝请求' };
  }
  return (await response.json()) as ToolCallResponse;
}

/**
 * 把 vscode.lm 的错误映射到 devhubd 的错误分类。
 */
function mapLanguageModelError(error: unknown): { kind: string; code: string; message: string } {
  if (error instanceof vscode.LanguageModelError) {
    const code = error.code;
    switch (code) {
      case 'NoPermissions':
      case 'Blocked':
        return { kind: 'auth', code: 'VSCODE_AUTH', message: error.message };
      case 'ConsentRequired':
        return { kind: 'auth', code: 'VSCODE_CONSENT', message: '需要在 VS Code 中授权语言模型访问。' };
      case 'NotFound':
        return { kind: 'no_model', code: 'VSCODE_NO_MODEL', message: error.message };
      default:
        return { kind: 'model_error', code: 'VSCODE_ERROR', message: error.message };
    }
  }
  return { kind: 'model_error', code: 'VSCODE_ERROR', message: (error as Error).message ?? '未知错误' };
}

/** 安全解析工具 schema；坏 schema 退化为空对象。 */
function safeParseSchema(raw: string): Record<string, unknown> {
  try {
    return JSON.parse(raw) as Record<string, unknown>;
  } catch {
    return { type: 'object', properties: {} };
  }
}

/**
 * 校验 Bearer 令牌（常量时间比较）。
 */
function isAuthorized(request: http.IncomingMessage, token: string): boolean {
  const header = request.headers.authorization ?? '';
  const prefix = 'Bearer ';
  if (!header.startsWith(prefix)) {
    return false;
  }
  const provided = Buffer.from(header.slice(prefix.length).trim(), 'utf8');
  const expected = Buffer.from(token, 'utf8');
  return provided.length === expected.length && crypto.timingSafeEqual(provided, expected);
}

/** 读取请求体（上限 1 MiB）。 */
async function readBody(request: http.IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const chunk of request) {
    total += chunk.length;
    if (total > 1024 * 1024) {
      throw new Error('请求体过大');
    }
    chunks.push(chunk as Buffer);
  }
  return Buffer.concat(chunks).toString('utf8');
}

/** 统一写出 JSON。 */
function sendJson(response: http.ServerResponse, status: number, payload: unknown): void {
  const body = JSON.stringify(payload);
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
  });
  response.end(body);
}

/** 延时。 */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * 通知轮询：关闭时清除定时器（零轮询），开启时每 60 秒拉一次。
 */
function restartNotificationPolling(context: vscode.ExtensionContext): void {
  if (notificationTimer) {
    clearInterval(notificationTimer);
    notificationTimer = undefined;
  }

  if (!isNotificationsEnabled()) {
    return;
  }

  void pollNotifications(context);
  notificationTimer = setInterval(() => void pollNotifications(context), 60_000);
  context.subscriptions.push({
    dispose: () => {
      if (notificationTimer) {
        clearInterval(notificationTimer);
        notificationTimer = undefined;
      }
    },
  });
}

/**
 * 读取 runtime.json 并按数据库 seq 游标拉取通知：
 * - 首次轮询只把 latestSeq 写成基线，不把历史积压当新工作；
 * - 之后只提醒 seq 大于游标且未确认的通知，弹窗后回写 ack 并推进游标；
 * - 游标存在 globalState，崩溃/重载后不会重复弹出同一条。
 * @param context 扩展上下文（持久化游标）
 */
async function pollNotifications(context: vscode.ExtensionContext): Promise<void> {
  try {
    const runtimePath = path.join(dataDirectory(), 'runtime.json');
    if (!fs.existsSync(runtimePath)) {
      return;
    }
    const runtime = JSON.parse(fs.readFileSync(runtimePath, 'utf8')) as {
      apiEnabled?: boolean;
      apiPort?: number;
      token?: string;
    };
    if (!runtime.apiEnabled || !runtime.apiPort || !runtime.token) {
      return;
    }

    const stored = context.globalState.get<number>(notificationCursorKey);
    const query = stored === undefined ? '' : `?sinceSeq=${stored}`;
    const response = await fetch(
      `http://127.0.0.1:${runtime.apiPort}/internal/notifications${query}`,
      { headers: { authorization: `Bearer ${runtime.token}` } },
    );
    if (!response.ok) {
      return;
    }

    const payload = (await response.json()) as {
      unread: number;
      failures: number;
      latestSeq: number;
      items: Array<{ seq: number; taskId: string; kind: string; title: string }>;
    };

    if (stored === undefined) {
      // 首次只建立基线：不提醒任何历史通知。
      await context.globalState.update(notificationCursorKey, payload.latestSeq);
      return;
    }

    const fresh = payload.items.filter((item) => item.seq > stored && !seenNotificationSeqs.has(item.seq));
    if (fresh.length === 0) {
      return;
    }

    for (const item of fresh) {
      seenNotificationSeqs.add(item.seq);
    }

    const titles = fresh.slice(0, 3).map((item) => `${item.taskId}：${item.title}`);
    const suffix = fresh.length > 3 ? ` 等 ${fresh.length} 条` : '';
    const message = `DevHub 有 ${payload.unread} 条未读结果（失败 ${payload.failures}）：${titles.join('；')}${suffix}`;
    void vscode.window.showInformationMessage(message, '打开结果').then((choice) => {
      if (choice === '打开结果') {
        void vscode.commands.executeCommand('workbench.view.extension.devhub');
      }
    });

    // 先确认再推进游标：确认失败时下次轮询仍会提醒，不会漏报。
    const ackResponse = await fetch(
      `http://127.0.0.1:${runtime.apiPort}/internal/notifications/ack`,
      {
        method: 'POST',
        headers: {
          authorization: `Bearer ${runtime.token}`,
          'content-type': 'application/json',
        },
        body: JSON.stringify({ seq: fresh.map((item) => item.seq) }),
      },
    );
    if (!ackResponse.ok) {
      return;
    }

    const nextCursor = Math.max(stored, ...fresh.map((item) => item.seq));
    await context.globalState.update(notificationCursorKey, nextCursor);
  } catch {
    // 轮询失败静默：下次重试，不在编辑器里刷错误提示。
  }
}
