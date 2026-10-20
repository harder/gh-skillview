const test = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { mkdtempSync, mkdirSync, writeFileSync, renameSync, rmSync, readFileSync } = require('node:fs');
const { tmpdir, devNull } = require('node:os');
const { join } = require('node:path');
const scope = require('./ci-change-scope.js');

const SHA = '1'.repeat(40);
const OTHER_SHA = '2'.repeat(40);
function record(path, status = 'M', before = '100644', after = '100644') {
  return Buffer.from(`:${before} ${after} ${SHA} ${OTHER_SHA} ${status}\0${path}\0`);
}

test('only explicit user docs, media and known site paths can omit full proof', () => {
  const paths = ['README.md', 'CONTRIBUTING.md', 'docs/usage.md', 'docs/runbooks/release-rollback.md',
    'docs/a file\nwith tabs\t.md', 'assets/screenshots/01-discover.png',
    'assets/screenshots/pr75-changes-80x24-after.txt', 'assets/animations/g1-discover.gif',
    'site/app.js', 'site/build.py', 'site/docs.html', 'site/styles.css', 'site/favicon.svg',
    'site/robots.txt', 'site/sitemap.xml', 'site/index.html'];
  for (const path of paths) assert.equal(scope.classifyDiff(record(path)).fullProof, false, JSON.stringify(path));
  assert.equal(scope.classifyDiff(Buffer.concat(paths.map(path => record(path)))).fullProof, false);
});

test('code, dependencies, workflows, proof inputs and all unknown paths retain full proof', () => {
  const paths = ['src/SkillView.Core/Foo.cs', 'tests/fixtures/README.md', 'global.json',
    'src/SkillView.Core/packages.lock.json', 'Directory.Build.props', 'SkillView.sln',
    '.github/workflows/ci.yml', '.github/scripts/ci-change-scope.js', '.github/workflows/README.md',
    'AGENTS.md', 'agent_docs/running-tests.md', 'agent_docs/evidence/probe-linux-abi.c',
    'agent_docs/evidence/test_old_native_contract.py', 'agent_docs/evidence/fixture.json',
    'docs/proof.py', 'docs/fixture.json', 'docs/.hidden.md', 'docs/../source.md', 'docs//file.md',
    'docs/AGENTS.md', 'site/new-proof.py', 'assets/screenshots/proof.py', 'assets/screenshots/fixture.json',
    'assets/animations/script.js', 'assets/new.png', 'README.MD', 'Docs/usage.md',
    'new-area/README.md', 'packaging/winget/README.md', 'docs\\usage.md', ''];
  for (const path of paths) assert.equal(scope.classifyDiff(record(path)).fullProof, true, JSON.stringify(path));
  assert.equal(scope.classifyDiff(Buffer.concat([record('docs/usage.md'), record('src/Foo.cs')])).fullProof, true);
});

test('ordinary additions and deletions qualify, while mode/type and unsupported records fail closed', () => {
  assert.equal(scope.classifyDiff(record('README.md', 'A', '000000', '100644')).fullProof, false);
  assert.equal(scope.classifyDiff(record('README.md', 'D', '100644', '000000')).fullProof, false);
  for (const [status, before, after] of [['M', '100644', '100755'], ['A', '000000', '120000'],
    ['D', '120000', '000000'], ['M', '160000', '160000'], ['T', '100644', '120000'],
    ['R100', '100644', '100644'], ['U', '100644', '100644'], ['A', '100644', '100644']]) {
    assert.equal(scope.classifyDiff(record('README.md', status, before, after)).fullProof, true);
  }
});

test('empty, truncated, malformed, invalid UTF-8 and oversized diffs fail closed', () => {
  const valid = record('README.md');
  for (const bytes of [Buffer.alloc(0), valid.subarray(0, valid.length - 1),
    Buffer.concat([valid, Buffer.from('partial')]), Buffer.from('README.md\0'),
    Buffer.concat([record('README.md').subarray(0, -1), Buffer.from([0xff, 0])]),
    Buffer.alloc(scope.MAX_DIFF_BYTES + 1), 'README.md']) {
    assert.equal(scope.classifyDiff(bytes).fullProof, true);
  }
});

test('missing/invalid commits, failed inspection and bounded output cannot authorize a skip', () => {
  const success = { status: 0, stdout: record('README.md') };
  for (const [base, head] of [[undefined, SHA], ['main', SHA], ['0'.repeat(40), SHA], [SHA, '--help']]) {
    assert.equal(scope.determineScope(base, head, { run: () => { throw new Error('must not run'); } }).fullProof, true);
  }
  for (const failure of [{ status: 128, stdout: record('README.md') },
    { status: 0, stdout: record('README.md'), error: new Error('ENOBUFS') },
    { status: null, stdout: record('README.md') }]) {
    assert.equal(scope.determineScope(SHA, OTHER_SHA, { run: () => failure }).fullProof, true);
    assert.equal(scope.determineScope(SHA, OTHER_SHA, {
      run: (command, args) => args.includes('diff') ? failure : success,
    }).fullProof, true);
  }
  assert.equal(scope.determineScope(SHA, OTHER_SHA, { run: () => { throw new Error('unavailable'); } }).fullProof, true);
  assert.equal(scope.determineScope(SHA, OTHER_SHA, { run: (command, args) => {
    if (args.includes('diff')) {
      assert.ok(args.includes('--no-renames'));
      assert.ok(args.includes('-z'));
    }
    return success;
  } }).fullProof, false);
});

function repository(t) {
  const cwd = mkdtempSync(join(tmpdir(), 'skillview-ci-scope-'));
  t.after(() => rmSync(cwd, { recursive: true, force: true }));
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('GIT_')));
  env.GIT_CONFIG_NOSYSTEM = '1';
  env.GIT_CONFIG_GLOBAL = devNull;
  const run = (command, args, options) => spawnSync(command, args, { ...options, env });
  const git = (...args) => {
    const result = run('git', ['-c', 'user.name=CI scope test', '-c', 'user.email=ci@example.invalid',
      '-c', 'commit.gpgsign=false', ...args], { cwd, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  };
  git('init', '--template=', '--initial-branch=main');
  const write = (path, text = path) => {
    mkdirSync(join(cwd, path, '..'), { recursive: true });
    writeFileSync(join(cwd, path), text);
  };
  const commit = () => {
    git('add', '--all');
    git('commit', '-m', 'scope fixture');
    return git('rev-parse', 'HEAD');
  };
  return { cwd, run, git, write, commit };
}

test('shallow merge checkout contains PR comparison parents and missing older history runs full proof', t => {
  const repo = repository(t);
  repo.write('README.md', 'initial');
  const ancestor = repo.commit();
  repo.git('checkout', '-b', 'feature');
  repo.write('docs/usage.md', 'feature docs');
  const head = repo.commit();
  repo.git('checkout', 'main');
  repo.write('README.md', 'base docs');
  const base = repo.commit();
  repo.git('merge', '--no-ff', '--no-edit', 'feature');
  const shallow = join(repo.cwd, 'shallow');
  repo.git('clone', '--depth=2', '--no-local', '--', repo.cwd, shallow);
  const inspection = { cwd: shallow, run: repo.run };
  assert.equal(scope.determineScope(base, head, inspection).fullProof, false);
  assert.equal(scope.determineScope(ancestor, head, inspection).fullProof, true);
});

test('real Git diffs preserve unusual filenames and inspect both sides of renames', t => {
  const repo = repository(t);
  repo.write('docs/a file\nwith tabs\t.md', 'original');
  let base = repo.commit();
  repo.write('docs/a file\nwith tabs\t.md', 'edited');
  let head = repo.commit();
  assert.equal(scope.determineScope(base, head, repo).fullProof, false);

  // A rename inside documentation qualifies; crossing into or out of code does not.
  base = head;
  renameSync(join(repo.cwd, 'docs/a file\nwith tabs\t.md'), join(repo.cwd, 'docs/renamed.md'));
  head = repo.commit();
  assert.equal(scope.determineScope(base, head, repo).fullProof, false);
  base = head;
  mkdirSync(join(repo.cwd, 'src'));
  renameSync(join(repo.cwd, 'docs/renamed.md'), join(repo.cwd, 'src/renamed.cs'));
  head = repo.commit();
  assert.equal(scope.determineScope(base, head, repo).fullProof, true);
  assert.equal(scope.determineScope(head, base, repo).fullProof, true);
  assert.equal(scope.determineScope(head, head, repo).fullProof, true);
  assert.equal(scope.determineScope('f'.repeat(40), head, repo).fullProof, true);
});

test('CLI writes a conservative GitHub output for unavailable commits', t => {
  const repo = repository(t);
  const output = join(repo.cwd, 'output');
  const result = spawnSync(process.execPath, [join(__dirname, 'ci-change-scope.js'), '--base', SHA, '--head', OTHER_SHA],
    { cwd: repo.cwd, env: { ...process.env, GITHUB_OUTPUT: output }, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(readFileSync(output, 'utf8'), 'full_proof=true\n');
  assert.match(result.stdout, /commit unavailable/);
});
