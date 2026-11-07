#!/usr/bin/env node
/**
 * 编码门禁：仓库内所有文本文件必须是 UTF-8、无 BOM、无替换字符（U+FFFD）。
 * 覆盖率靠扩展名白名单控制，避免把图片、dll、db 当文本读。
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SKIP_DIRS = new Set(['.git', '.vs', 'bin', 'obj', 'node_modules', 'dist', 'TestResults', 'coverage', '.screenshots']);
const TEXT_EXTENSIONS = new Set([
  '.cs', '.csproj', '.sln', '.props', '.targets', '.json', '.jsonc', '.md', '.mjs', '.js',
  '.ts', '.tsx', '.yml', '.yaml', '.ps1', '.sh', '.txt', '.editorconfig', '.gitignore', '.gitattributes',
]);
const TEXT_FILENAMES = new Set(['.editorconfig', '.gitignore', '.gitattributes']);

const failures = [];
let checked = 0;

/**
 * 判断是否需要按文本校验。
 * @param {string} name 文件名
 */
function isTextFile(name) {
  return TEXT_FILENAMES.has(name) || TEXT_EXTENSIONS.has(extname(name).toLowerCase());
}

/**
 * 递归遍历并校验编码。
 * @param {string} directory 目录绝对路径
 */
function walk(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.isSymbolicLink()) continue;
    const fullPath = join(directory, entry.name);
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      walk(fullPath);
      continue;
    }
    if (!entry.isFile() || !isTextFile(entry.name)) continue;

    checked++;
    const buffer = readFileSync(fullPath);
    const shown = relative(root, fullPath);

    if (buffer.length >= 3 && buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) {
      failures.push(`${shown}: UTF-8 BOM`);
      continue;
    }
    try {
      const text = new TextDecoder('utf-8', { fatal: true }).decode(buffer);
      if (text.includes('\uFFFD')) failures.push(`${shown}: 含替换字符 U+FFFD`);
    } catch {
      failures.push(`${shown}: 非法 UTF-8`);
    }
  }
}

walk(root);

if (failures.length > 0) {
  console.error(`编码校验失败（${failures.length}/${checked}）：`);
  for (const failure of failures) console.error(`  ${failure}`);
  process.exit(1);
}

console.log(`编码校验通过：${checked} 个文本文件均为 UTF-8 无 BOM。`);
