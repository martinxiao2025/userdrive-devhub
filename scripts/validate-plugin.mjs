#!/usr/bin/env node
/**
 * Agent Plugins 1.0.0 离线校验：只校验 plugin/ 一个目标，避免"开发能过、发布不过"。
 * 校验项：封闭字段、$schema 版本、name 规则、技能不递归、mcp.json 结构、UTF-8 无 BOM、版本号一致。
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

/** 包内不得出现解决方案、源码、二进制与构建脚本。 */
function validatePackageContents() {
  const forbidden = ['bin', 'obj', 'node_modules'];
  for (const name of forbidden) {
    if (existsSync(join(pluginDir, name))) fail(`plugin/ 不得包含 ${name}/`);
  }
  if (existsSync(join(pluginDir, 'Devhubd.sln'))) fail('plugin/ 不得包含解决方案文件');

  for (const entry of readdirSync(pluginDir, { withFileTypes: true })) {
    const allowedRoots = new Set(['plugin.json', 'mcp.json', 'skills']);
    if (!allowedRoots.has(entry.name)) {
      fail(`plugin/ 顶层出现非组件内容：${entry.name}`);
    }
  }
  pass('plugin/ 顶层只含规范组件');
}

console.log(`校验目标：${pluginDir}`);
validatePluginManifest();
validateMcpManifest();
validateSkills();
validatePackageContents();

for (const message of passes) console.log(`  PASS ${message}`);
if (errors.length > 0) {
  console.error(`\n校验失败（${errors.length} 项）：`);
  for (const message of errors) console.error(`  FAIL ${message}`);
  process.exit(1);
}
console.log('\nAgent Plugins 1.0.0 校验通过。');
