#!/usr/bin/env node
/**
 * Agent Plugins 1.0.0 离线校验：只校验 plugin/ 一个目标，避免"开发能过、发布不过"。
 * 校验项：封闭字段、$schema 版本、name 规则、技能不递归、角色字段完整性与引用、mcp.json 结构、UTF-8 无 BOM、版本号一致。
 */
import { existsSync, lstatSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const pluginDir = join(root, 'plugin');

const PLUGIN_SCHEMA = 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json';
const MCP_SCHEMA = 'https://agent-plugins.org/schemas/1.0.0/mcp.schema.json';
const EXPECTED_MCP_URL = 'http://127.0.0.1:3091/mcp';

const PLUGIN_KEYS = new Set([
  '$schema', 'name', 'version', 'description', 'author', 'homepage', 'repository', 'license', 'keywords', 'extensions',
]);
const MCP_KEYS = new Set(['$schema', 'mcpServers']);
const SERVER_KEYS = {
  stdio: new Set(['type', 'command', 'args', 'env', 'cwd']),
  'streamable-http': new Set(['type', 'url', 'headers']),
  sse: new Set(['type', 'url', 'headers']),
};
const NAME_PATTERN = /^(?!.*(?:--|\.\.))[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/;

/** 角色文件允许的键（封闭字段，缺字段或多余键都失败）。 */
const ROLE_KEYS = new Set([
  'id', 'name', 'description', 'skills', 'tools', 'delivery',
  'requiresApproval', 'canRequestInput', 'concurrency', 'budget',
]);
const ROLE_CONCURRENCY_KEYS = new Set(['maxParallel', 'maxAttempts']);
const ROLE_BUDGET_KEYS = new Set(['maxRounds', 'maxToolCalls', 'maxDurationSeconds', 'maxInputTokens']);
const ROLE_ID_PATTERN = /^[a-z][a-z0-9-]{0,31}$/;
const TOOL_NAME_PATTERN = /^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$/;

/**
 * 已知 MCP 工具表：新增工具必须先更新本表与 docs/mcp-tool-contract.md，否则角色引用会失败。
 * 前 7 个为已实现工具；后 5 个为方案已给契约但尚未上线的工具。
 */
const KNOWN_TOOLS = new Set([
  'scheduler_status', 'queue_list', 'queue_get', 'dispatch_now', 'scheduler_control',
  'result_list', 'result_ack',
  'input_request_list', 'input_request_get', 'input_request_answer', 'input_request_cancel',
  'task_upsert', 'result_approve', 'result_reject',
]);


const errors = [];
const passes = [];

/** 记录一条校验错误。 */
function fail(message) {
  errors.push(message);
}

/** 记录一条校验通过项。 */
function pass(message) {
  passes.push(message);
}

/**
 * 以严格 UTF-8 读取文本文件，并检查 BOM 与替换字符。
 * @param {string} path 文件路径
 * @returns {string} 文件文本
 */
function readUtf8Strict(path) {
  const buffer = readFileSync(path);
  if (buffer.length >= 3 && buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) {
    fail(`${path} 带有 UTF-8 BOM，规范要求无 BOM`);
  }
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(buffer);
  } catch {
    fail(`${path} 不是合法 UTF-8`);
    return '';
  }
  if (text.includes('\uFFFD')) {
    fail(`${path} 含替换字符 U+FFFD，可能已乱码`);
  }
  return text.replace(/^\uFEFF/, '');
}

/** 读取并解析 JSON。 */
function readJson(path) {
  if (!existsSync(path)) {
    fail(`缺少文件：${path}`);
    return null;
  }
  try {
    return JSON.parse(readUtf8Strict(path));
  } catch (error) {
    fail(`${path} JSON 解析失败：${error.message}`);
    return null;
  }
}

/** 从 Directory.Build.props 读取统一版本号。 */
function readBuildVersion() {
  const propsPath = join(root, 'Directory.Build.props');
  if (!existsSync(propsPath)) {
    fail('缺少 Directory.Build.props');
    return null;
  }
  const match = readUtf8Strict(propsPath).match(/<Version>([^<]+)<\/Version>/);
  if (!match) {
    fail('Directory.Build.props 缺少 <Version>');
    return null;
  }
  return match[1].trim();
}

/** 校验 plugin.json。 */
function validatePluginManifest() {
  const manifest = readJson(join(pluginDir, 'plugin.json'));
  if (!manifest) return null;

  for (const key of Object.keys(manifest)) {
    if (!PLUGIN_KEYS.has(key)) fail(`plugin.json 含封闭字段外的键：${key}`);
  }
  if (manifest.$schema !== PLUGIN_SCHEMA) {
    fail(`plugin.json $schema 必须是 ${PLUGIN_SCHEMA}，实际：${manifest.$schema}`);
  }
  if (typeof manifest.name !== 'string' || !NAME_PATTERN.test(manifest.name) || manifest.name.length > 64) {
    fail(`plugin.json name 不合规：${manifest.name}`);
  }
  if (typeof manifest.version !== 'string' || !/^\d+\.\d+\.\d+/.test(manifest.version)) {
    fail(`plugin.json version 不合规：${manifest.version}`);
  }
  if (manifest.author !== undefined) {
    if (typeof manifest.author !== 'object' || Array.isArray(manifest.author)) {
      fail('plugin.json author 必须是对象');
    } else {
      for (const key of Object.keys(manifest.author)) {
        if (!['name', 'email', 'url'].includes(key)) fail(`plugin.json author 含非法键：${key}`);
      }
    }
  }
  if (manifest.extensions !== undefined && typeof manifest.extensions !== 'object') {
    fail('plugin.json extensions 必须是对象');
  }

  const buildVersion = readBuildVersion();
  if (buildVersion && manifest.version !== buildVersion) {
    fail(`plugin.json version(${manifest.version}) 与 Directory.Build.props(${buildVersion}) 不一致`);
  }

  pass('plugin.json 封闭字段与 $schema 合规');
  return manifest;
}

/**
 * 校验 MCP 服务器条目。
 * @param {string} name 服务器名
 * @param {Record<string, unknown>} server 服务器定义
 */
function validateServer(name, server) {
  if (typeof server !== 'object' || server === null || Array.isArray(server)) {
    fail(`mcpServers.${name} 必须是对象`);
    return;
  }

  const type = server.type;
  const allowed = SERVER_KEYS[type];
  if (!allowed) {
    fail(`mcpServers.${name}.type 非法：${type}（允许 stdio / streamable-http / sse）`);
    return;
  }
  for (const key of Object.keys(server)) {
    if (!allowed.has(key)) fail(`mcpServers.${name} 含 ${type} 不允许的键：${key}`);
  }

  if (type === 'stdio') {
    if (typeof server.command !== 'string' || !server.command) {
      fail(`mcpServers.${name} 缺少 command`);
    }
    return;
  }

  if (typeof server.url !== 'string') {
    fail(`mcpServers.${name} 缺少 url`);
    return;
  }
  let url;
  try {
    url = new URL(server.url);
  } catch {
    fail(`mcpServers.${name}.url 不是绝对 URL：${server.url}`);
    return;
  }
  if (url.username || url.password) fail(`mcpServers.${name}.url 不得含用户信息`);
  if (url.hash) fail(`mcpServers.${name}.url 不得含片段`);
  const loopback = ['127.0.0.1', 'localhost', '[::1]', '::1'];
  if (url.protocol === 'http:' && !loopback.includes(url.hostname)) {
    fail(`mcpServers.${name}.url 使用 http 时必须是回环地址`);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    fail(`mcpServers.${name}.url 必须是 http(s)`);
  }
  if (server.headers && typeof server.headers === 'object') {
    for (const [key, value] of Object.entries(server.headers)) {
      if (/authorization|token|secret|api[-_]?key|credential/i.test(key) || /^(bearer|basic)\s/i.test(String(value))) {
        fail(`mcpServers.${name}.headers 不得携带凭据（${key}）`);
      }
    }
  }
  if (type === 'sse') {
    console.warn(`[warn] mcpServers.${name} 使用已弃用的 sse 传输`);
  }
}

/** 校验 mcp.json 并确认 URL 指向固定端口。 */
function validateMcpManifest() {
  const manifest = readJson(join(pluginDir, 'mcp.json'));
  if (!manifest) return;

  for (const key of Object.keys(manifest)) {
    if (!MCP_KEYS.has(key)) fail(`mcp.json 含封闭字段外的键：${key}`);
  }
  if (manifest.$schema !== MCP_SCHEMA) {
    fail(`mcp.json $schema 必须是 ${MCP_SCHEMA}，实际：${manifest.$schema}`);
  }

  const servers = manifest.mcpServers;
  if (typeof servers !== 'object' || servers === null || Object.keys(servers).length === 0) {
    fail('mcp.json mcpServers 必须是非空对象');
    return;
  }
  for (const [name, server] of Object.entries(servers)) {
    validateServer(name, server);
  }

  const devhubd = servers.devhubd;
  if (!devhubd || devhubd.url !== EXPECTED_MCP_URL) {
    fail(`mcpServers.devhubd.url 必须固定为 ${EXPECTED_MCP_URL}，实际：${devhubd?.url}`);
  }
  pass('mcp.json 结构与固定端点合规');
}

/**
 * 校验 SKILL.md 的 frontmatter：只允许 name 与 description，且 name 与目录同名。
 * @param {string} skillName 目录名
 * @param {string} skillPath SKILL.md 路径
 */
function validateSkillFrontmatter(skillName, skillPath) {
  const text = readUtf8Strict(skillPath);
  const match = text.match(/^---\r?\n([\s\S]*?)\r?\n---\r?\n/);
  if (!match) {
    fail(`${skillPath} 缺少 frontmatter`);
    return;
  }
  const lines = match[1].split(/\r?\n/).filter((line) => line.trim().length > 0);
  const keys = lines.map((line) => line.split(':')[0].trim());
  for (const key of keys) {
    if (!['name', 'description'].includes(key)) fail(`${skillPath} frontmatter 含非法键：${key}`);
  }
  const nameLine = lines.find((line) => line.startsWith('name:'));
  if (!nameLine) {
    fail(`${skillPath} frontmatter 缺少 name`);
    return;
  }
  const name = nameLine.slice(5).trim().replace(/^['"]|['"]$/g, '');
  if (name !== skillName) {
    fail(`${skillPath} frontmatter name(${name}) 与目录名(${skillName}) 不一致`);
  }
  if (!lines.some((line) => line.startsWith('description:'))) {
    fail(`${skillPath} frontmatter 缺少 description`);
  }
}

/** 校验 skills/ 只扫一级、且每个技能都有常规文件 SKILL.md。 */
function validateSkills() {
  const skillsDir = join(pluginDir, 'skills');
  if (!existsSync(skillsDir)) {
    fail('缺少 plugin/skills 目录');
    return;
  }

  const entries = readdirSync(skillsDir, { withFileTypes: true });
  if (entries.length === 0) {
    fail('plugin/skills 为空');
    return;
  }

  for (const entry of entries) {
    const entryPath = join(skillsDir, entry.name);
    const stat = lstatSync(entryPath);
    if (stat.isSymbolicLink()) {
      fail(`技能目录不得是符号链接：${entry.name}`);
      continue;
    }
    if (!stat.isDirectory()) {
      fail(`skills/ 下只允许技能目录，发现文件：${entry.name}`);
      continue;
    }

    const skillFile = join(entryPath, 'SKILL.md');
    if (!existsSync(skillFile) || !lstatSync(skillFile).isFile()) {
      fail(`技能 ${entry.name} 缺少常规文件 SKILL.md`);
      continue;
    }
    validateSkillFrontmatter(entry.name, skillFile);

    for (const nested of readdirSync(entryPath, { withFileTypes: true })) {
      if (nested.isDirectory() && existsSync(join(entryPath, nested.name, 'SKILL.md'))) {
        fail(`技能 ${entry.name} 内出现嵌套技能：${nested.name}（规范不递归）`);
      }
    }
  }

  pass(`skills/ 校验通过（${entries.length} 个技能）`);
}

/** 判断是否为普通 JSON 对象（非数组、非 null）。 */
function isPlainObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * 读取非空字符串数组，可选校验命名规则。
 * @param {unknown} value 待校验值
 * @param {string} label 报错前缀
 * @param {RegExp} [pattern] 可选取值规则
 * @returns {string[]|null} 合法元素数组；结构本身非法时返回 null
 */
function readStringArray(value, label, pattern) {
  if (!Array.isArray(value) || value.length === 0) {
    fail(`${label} 必须是非空数组`);
    return null;
  }
  const items = [];
  for (const item of value) {
    if (typeof item !== 'string' || item.length === 0) {
      fail(`${label} 含非字符串元素`);
      continue;
    }
    if (pattern && !pattern.test(item)) fail(`${label} 含非法取值：${item}`);
    items.push(item);
  }
  return items;
}

/**
 * 校验单个角色文件：封闭字段、必填项、类型与取值范围。
 * @param {string} fileName 文件名（含 .json）
 * @param {Record<string, unknown>} role 角色内容
 * @param {Set<string>} skillNames 已存在的技能目录名
 * @returns {string|null} 合法角色 id
 */
function validateRoleFile(fileName, role, skillNames) {
  const label = `roles/${fileName}`;
  if (!isPlainObject(role)) {
    fail(`${label} 必须是 JSON 对象`);
    return null;
  }

  for (const key of Object.keys(role)) {
    if (!ROLE_KEYS.has(key)) fail(`${label} 含封闭字段外的键：${key}`);
  }
  for (const key of ROLE_KEYS) {
    if (!(key in role)) fail(`${label} 缺少必填字段：${key}`);
  }

  const expectedId = fileName.replace(/\.json$/i, '');
  if (typeof role.id !== 'string' || !ROLE_ID_PATTERN.test(role.id)) {
    fail(`${label} id 不合规：${role.id}`);
  } else if (role.id !== expectedId) {
    fail(`${label} id(${role.id}) 与文件名(${expectedId}) 不一致`);
  }
  if (typeof role.name !== 'string' || role.name.trim().length === 0) {
    fail(`${label} name 必须是非空字符串`);
  }
  if (typeof role.description !== 'string' || role.description.trim().length === 0) {
    fail(`${label} description 必须是非空字符串`);
  }

  const skills = readStringArray(role.skills, `${label}.skills`);
  if (skills) {
    for (const skill of skills) {
      if (!skillNames.has(skill)) fail(`${label}.skills 引用不存在的技能：${skill}`);
    }
  }

  const tools = readStringArray(role.tools, `${label}.tools`, TOOL_NAME_PATTERN);
  if (tools) {
    for (const tool of tools) {
      if (!KNOWN_TOOLS.has(tool)) fail(`${label}.tools 含未知工具：${tool}（需先更新本脚本与契约文档）`);
    }
  }

  if (role.delivery !== 'auto' && role.delivery !== 'dispatch_only') {
    fail(`${label}.delivery 必须是 auto 或 dispatch_only，实际：${role.delivery}`);
  }
  if (typeof role.requiresApproval !== 'boolean') fail(`${label}.requiresApproval 必须是布尔`);
  if (typeof role.canRequestInput !== 'boolean') fail(`${label}.canRequestInput 必须是布尔`);

  if (!isPlainObject(role.concurrency)) {
    fail(`${label}.concurrency 必须是对象`);
  } else {
    for (const key of Object.keys(role.concurrency)) {
      if (!ROLE_CONCURRENCY_KEYS.has(key)) fail(`${label}.concurrency 含非法键：${key}`);
    }
    const { maxParallel, maxAttempts } = role.concurrency;
    if (!Number.isInteger(maxParallel) || maxParallel < 1 || maxParallel > 16) {
      fail(`${label}.concurrency.maxParallel 必须是 1–16 的整数`);
    }
    if (!Number.isInteger(maxAttempts) || maxAttempts < 1 || maxAttempts > 10) {
      fail(`${label}.concurrency.maxAttempts 必须是 1–10 的整数`);
    }
  }

  if (!isPlainObject(role.budget)) {
    fail(`${label}.budget 必须是对象`);
  } else {
    for (const key of Object.keys(role.budget)) {
      if (!ROLE_BUDGET_KEYS.has(key)) fail(`${label}.budget 含非法键：${key}`);
    }
    const { maxRounds, maxToolCalls, maxDurationSeconds, maxInputTokens } = role.budget;
    if (!Number.isInteger(maxRounds) || maxRounds < 1 || maxRounds > 50) {
      fail(`${label}.budget.maxRounds 必须是 1–50 的整数`);
    }
    if (!Number.isInteger(maxToolCalls) || maxToolCalls < 1 || maxToolCalls > 200) {
      fail(`${label}.budget.maxToolCalls 必须是 1–200 的整数`);
    }
    if (!Number.isInteger(maxDurationSeconds) || maxDurationSeconds < 30 || maxDurationSeconds > 3600) {
      fail(`${label}.budget.maxDurationSeconds 必须是 30–3600 的整数`);
    }
    if (maxInputTokens !== undefined && (!Number.isInteger(maxInputTokens) || maxInputTokens < 1)) {
      fail(`${label}.budget.maxInputTokens 必须是正整数`);
    }
  }

  return typeof role.id === 'string' ? role.id : null;
}

/**
 * 校验角色目录与 plugin.json 的 extensions.devhub 声明：
 * 声明必须覆盖实际文件，技能入口必须与实际技能目录一致。
 * @param {Record<string, unknown>|null} manifest plugin.json 内容
 */
function validateRoles(manifest) {
  const errorsBefore = errors.length;
  const extension = manifest?.extensions?.devhub;
  if (!isPlainObject(extension)) {
    fail('plugin.json 缺少 extensions.devhub（角色与技能入口）');
    return;
  }
  for (const key of Object.keys(extension)) {
    if (!['roles', 'skills'].includes(key)) fail(`plugin.json extensions.devhub 含非法键：${key}`);
  }

  const declaredRoles = readStringArray(extension.roles, 'plugin.json extensions.devhub.roles');
  const declaredSkills = readStringArray(extension.skills, 'plugin.json extensions.devhub.skills');

  const skillsDir = join(pluginDir, 'skills');
  const skillNames = new Set();
  if (existsSync(skillsDir)) {
    const actualSkillPaths = new Set();
    for (const entry of readdirSync(skillsDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      skillNames.add(entry.name);
      actualSkillPaths.add(`skills/${entry.name}`);
    }
    if (declaredSkills) {
      for (const skill of declaredSkills) {
        if (!actualSkillPaths.has(skill)) fail(`plugin.json 声明的技能入口不存在：${skill}`);
      }
      for (const skill of actualSkillPaths) {
        if (!declaredSkills.includes(skill)) fail(`技能目录未在 plugin.json 声明：${skill}`);
      }
    }
  }

  const rolesDir = join(pluginDir, 'roles');
  if (!existsSync(rolesDir)) {
    fail('缺少 plugin/roles 目录');
    return;
  }

  const entries = readdirSync(rolesDir, { withFileTypes: true });
  if (entries.length === 0) {
    fail('plugin/roles 为空');
    return;
  }

  const declaredSet = new Set(declaredRoles ?? []);
  const roleIds = new Set();
  for (const entry of entries) {
    const entryPath = join(rolesDir, entry.name);
    if (lstatSync(entryPath).isSymbolicLink()) {
      fail(`角色文件不得是符号链接：${entry.name}`);
      continue;
    }
    if (!entry.isFile() || !entry.name.toLowerCase().endsWith('.json')) {
      fail(`roles/ 下只允许 .json 角色文件，发现：${entry.name}`);
      continue;
    }
    const relativePath = `roles/${entry.name}`;
    if (declaredRoles && !declaredSet.has(relativePath)) {
      fail(`角色文件未在 plugin.json 声明：${relativePath}`);
    }

    const role = readJson(entryPath);
    if (!role) continue;
    const id = validateRoleFile(entry.name, role, skillNames);
    if (!id) continue;
    if (roleIds.has(id)) fail(`角色 id 重复：${id}`);
    roleIds.add(id);
  }

  if (declaredRoles) {
    for (const declared of declaredRoles) {
      if (!existsSync(join(pluginDir, declared))) fail(`plugin.json 声明的角色文件不存在：${declared}`);
    }
  }

  if (errors.length === errorsBefore) {
    pass(`roles/ 校验通过（${entries.length} 个角色，${roleIds.size} 个合法 id）`);
  }
}

/** 包内不得出现解决方案、源码、二进制与构建脚本。 */
function validatePackageContents() {
  const forbidden = ['bin', 'obj', 'node_modules'];
  for (const name of forbidden) {
    if (existsSync(join(pluginDir, name))) fail(`plugin/ 不得包含 ${name}/`);
  }
  if (existsSync(join(pluginDir, 'DevHubd.sln'))) fail('plugin/ 不得包含解决方案文件');

  for (const entry of readdirSync(pluginDir, { withFileTypes: true })) {
    const allowedRoots = new Set(['plugin.json', 'mcp.json', 'skills', 'roles']);
    if (!allowedRoots.has(entry.name)) {
      fail(`plugin/ 顶层出现非组件内容：${entry.name}`);
    }
  }
  pass('plugin/ 顶层只含规范组件');
}

console.log(`校验目标：${pluginDir}`);
const manifest = validatePluginManifest();
validateMcpManifest();
validateSkills();
validateRoles(manifest);
validatePackageContents();

for (const message of passes) console.log(`  PASS ${message}`);
if (errors.length > 0) {
  console.error(`\n校验失败（${errors.length} 项）：`);
  for (const message of errors) console.error(`  FAIL ${message}`);
  process.exit(1);
}
console.log('\nAgent Plugins 1.0.0 校验通过。');
