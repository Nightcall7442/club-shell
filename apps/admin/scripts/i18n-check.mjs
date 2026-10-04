#!/usr/bin/env node
/**
 * Completeness check of the console's translations (`pnpm --filter @clubshell/admin i18n:check`, run in CI).
 *
 * `t()` falls back to the Russian key when a line is missing (`src/i18n.ts`), so a forgotten translation shows up as
 * Russian on an Uzbek or English screen and nothing fails. This script fails instead:
 * - every Russian string in `src` (a string literal or JSX text with Cyrillic letters: `t('…')` keys, and the labels
 *   kept in tables and passed to `t()` later) must have a line in both `UZ` and `EN` of `src/i18n.tables.ts`;
 * - every `{placeholder}` of a key must be in its translations, and no other;
 * - a `t()` key must be a fixed string, not a template with `${…}`.
 * `src/platform` (the platform administration, Russian only) is not checked. A line marked `i18n-ignore` is skipped
 * (a language's own name in the language picker).
 *
 * The tables are TypeScript: they are transpiled in memory with the workspace's `typescript`, so the script runs on
 * plain Node without a loader.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const src = path.join(root, 'src');
const tablesFile = path.join(src, 'i18n.tables.ts');

const tablesJs = ts.transpileModule(fs.readFileSync(tablesFile, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
}).outputText;
const { UZ, EN } = await import(`data:text/javascript;base64,${Buffer.from(tablesJs).toString('base64')}`);

const cyrillic = /[а-яё]/i;
const placeholders = (s) =>
  [...s.matchAll(/\{(\w+)\}/g)]
    .map((m) => m[1])
    .sort()
    .join(',');

/** Source files of the console, without the platform app and the tables themselves. */
function sources(dir) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (p !== path.join(src, 'platform')) out.push(...sources(p));
    } else if (/\.tsx?$/.test(e.name) && p !== tablesFile) {
      out.push(p);
    }
  }
  return out;
}

const problems = [];
const keys = new Map();

for (const file of sources(src)) {
  const text = fs.readFileSync(file, 'utf8');
  const lines = text.split('\n');
  const sf = ts.createSourceFile(
    file,
    text,
    ts.ScriptTarget.ES2022,
    true,
    file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const where = (node) => {
    const line = sf.getLineAndCharacterOfPosition(node.getStart(sf)).line;
    return {
      at: `${path.relative(root, file).replace(/\\/g, '/')}:${line + 1}`,
      ignored: /i18n-ignore/.test(lines[line] ?? ''),
    };
  };
  const add = (key, node) => {
    const w = where(node);
    if (w.ignored) return;
    if (!keys.has(key)) keys.set(key, w.at);
  };
  const visit = (node) => {
    if ((ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) && cyrillic.test(node.text)) {
      // A module specifier or a regular expression source is not copy.
      if (!ts.isImportDeclaration(node.parent)) add(node.text, node);
    } else if (ts.isJsxText(node) && cyrillic.test(node.text)) {
      const w = where(node);
      if (!w.ignored) problems.push(`${w.at}: Russian JSX text outside t(): ${JSON.stringify(node.text.trim())}`);
    } else if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 't' &&
      node.arguments[0] &&
      ts.isTemplateExpression(node.arguments[0])
    ) {
      problems.push(`${where(node).at}: t() key must be a fixed string, not a template`);
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
}

for (const [key, at] of keys) {
  for (const [name, table] of [
    ['UZ', UZ],
    ['EN', EN],
  ]) {
    if (!(key in table)) problems.push(`${at}: no ${name} line for ${JSON.stringify(key)}`);
  }
}

for (const [name, table] of [
  ['UZ', UZ],
  ['EN', EN],
]) {
  for (const [key, value] of Object.entries(table)) {
    if (typeof value !== 'string' || value.trim() === '')
      problems.push(`${name}: empty line for ${JSON.stringify(key)}`);
    else if (placeholders(key) !== placeholders(value))
      problems.push(`${name}: placeholders differ for ${JSON.stringify(key)}: ${JSON.stringify(value)}`);
  }
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  console.error(`\ni18n check: ${problems.length} problem(s). Add the lines to src/i18n.tables.ts.`);
  process.exit(1);
}
console.log(`i18n check: ${keys.size} Russian strings, every one in UZ and EN.`);
