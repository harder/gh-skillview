// Only positively identified documentation/site changes can omit .NET proofs.
const { spawnSync } = require('node:child_process');
const { appendFileSync } = require('node:fs');
const { TextDecoder } = require('node:util');

const MAX_DIFF_BYTES = 1024 * 1024;
const SITE_FILES = new Set([
  'site/index.html', 'site/docs.html', 'site/styles.css', 'site/app.js',
  'site/favicon.svg', 'site/robots.txt', 'site/sitemap.xml', 'site/build.py',
]);

function isDocumentationPath(path) {
  // Git paths are case-sensitive and slash-separated. Never normalize unknown paths.
  if (path === 'AGENTS.md') return true;
  if (!path || path.split('/').some(part => !part || part.startsWith('.')) || path.includes('\\')
    || /(?:^|\/)(?:AGENTS|SKILL)\.md$/.test(path)) return false;
  return path === 'README.md' || path === 'CONTRIBUTING.md' || SITE_FILES.has(path)
    || ((path.startsWith('docs/') || path.startsWith('agent_docs/')) && path.endsWith('.md'))
    || /^assets\/screenshots\/[^/]+\.(png|txt)$/.test(path)
    || /^assets\/animations\/[^/]+\.gif$/.test(path);
}

function classifyDiff(bytes) {
  const full = reason => ({ fullProof: true, reason });
  if (!Buffer.isBuffer(bytes) || bytes.length === 0 || bytes.length > MAX_DIFF_BYTES) return full('empty or bounded diff');
  let fields;
  try {
    fields = new TextDecoder('utf-8', { fatal: true }).decode(bytes).split('\0');
  } catch {
    return full('invalid diff encoding');
  }
  if (fields.pop() !== '' || fields.length % 2 !== 0) return full('incomplete diff');
  for (let index = 0; index < fields.length; index += 2) {
    const header = /^:(\d{6}) (\d{6}) ([a-f0-9]{40}|[a-f0-9]{64}) ([a-f0-9]{40}|[a-f0-9]{64}) ([AMD])$/.exec(fields[index]);
    if (!header) return full('unsupported diff record');
    const [, before, after, , , status] = header;
    // Mode changes, executable files, links and submodules always receive full proof.
    if (!((status === 'A' && before === '000000' && after === '100644')
      || (status === 'D' && before === '100644' && after === '000000')
      || (status === 'M' && before === '100644' && after === '100644'))) return full('unsupported file mode');
    if (!isDocumentationPath(fields[index + 1])) return full('change outside documentation/site allowlist');
  }
  return { fullProof: false, reason: 'only documentation, screenshots or static site files changed' };
}

function determineScope(base, head, { cwd = process.cwd(), run = spawnSync } = {}) {
  // Accept full object IDs only, never option-like input or ambiguous branch names.
  const validCommit = value => typeof value === 'string' && /^(?:[a-f0-9]{40}|[a-f0-9]{64})$/.test(value) && !/^0+$/.test(value);
  const full = reason => ({ fullProof: true, reason });
  if (!validCommit(base) || !validCommit(head)) return full('missing or invalid base/head commit');
  const git = args => run('git', ['--no-pager', ...args], { cwd, encoding: null, timeout: 30000, maxBuffer: MAX_DIFF_BYTES });
  try {
    for (const commit of [base, head]) {
      const result = git(['cat-file', '-e', `${commit}^{commit}`]);
      if (result.error || result.status !== 0) return full('base/head commit unavailable');
    }
    // Disable rename folding so both old and new paths are classified, including
    // a code-to-docs rename. NUL records preserve spaces, tabs and newlines.
    const result = git(['diff', '--raw', '-z', '--abbrev=64', '--no-renames', '--no-ext-diff',
      '--no-textconv', '--ignore-submodules=none', '--no-relative', '--no-color', base, head, '--']);
    if (result.error || result.status !== 0) return full('diff failed or exceeded its bound');
    return classifyDiff(result.stdout);
  } catch {
    return full('unable to inspect diff');
  }
}

if (require.main === module) {
  const args = process.argv.slice(2);
  const result = args.length === 4 && args[0] === '--base' && args[2] === '--head'
    ? determineScope(args[1], args[3])
    : { fullProof: true, reason: 'expected --base SHA --head SHA' };
  const output = `full_proof=${result.fullProof}\n`;
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, output);
  process.stdout.write(`${output}${result.reason}\n`);
}

module.exports = { MAX_DIFF_BYTES, isDocumentationPath, classifyDiff, determineScope };
