const test = require('node:test');
const assert = require('node:assert/strict');
const monitor = require('./critical-dependencies.js');

const project = `
  <TerminalGuiVersion Condition="'$(TerminalGuiVersion)' == ''">2.5.0</TerminalGuiVersion>
  <TerminalGuiEditorVersion Condition="'$(TerminalGuiEditorVersion)' == ''">2.5.7</TerminalGuiEditorVersion>
`;
const locator = 'public static readonly SemVer MinimumVersion = new(2, 97, 0);';

test('reads package properties and the enforced GitHub CLI minimum', () => {
  assert.equal(monitor.propertyVersion(project, 'TerminalGuiVersion'), '2.5.0');
  assert.equal(monitor.propertyVersion(project, 'TerminalGuiEditorVersion'), '2.5.7');
  assert.equal(monitor.minimumGhVersion(locator), '2.97.0');
  assert.ok(monitor.compareVersions('2.5.10', '2.5.9') > 0);
  assert.ok(monitor.compareVersions('v2.102.0', '2.97.0') > 0);
});

test('creates assigned, deduplicated issues with useful checks for new releases', async () => {
  const issues = [];
  const outputs = [];
  let labelExists = false;
  const github = {
    paginate: async () => issues,
    rest: {
      issues: {
        getLabel: async () => {
          if (!labelExists) throw Object.assign(new Error('missing label'), { status: 404 });
        },
        createLabel: async () => { labelExists = true; },
        listForRepo: async () => ({ data: issues }),
        create: async ({ title, body, labels, assignees }) => {
          const number = issues.length + 1;
          const issue = { number, title, body, labels, assignees, html_url: `https://example.invalid/${number}` };
          issues.push(issue);
          return { data: issue };
        },
      },
      repos: {
        listReleases: async () => ({ data: [{
          tag_name: 'v2.5.1', html_url: 'https://example.invalid/tg',
          body: 'Fix keyboard input and layout.', draft: false,
        }] }),
        getLatestRelease: async () => ({ data: {
          tag_name: 'v2.102.0', html_url: 'https://example.invalid/gh',
          body: '## Security\n\nInteractive `gh skill search` fixed option injection.\n\nSee https://github.com/cli/cli/security/advisories/GHSA-qcwj-mr2r-2cx7\n\n## What\'s Changed\n\n* Fix auth handling', draft: false, prerelease: false,
        } }),
      },
    },
  };
  const fetchImpl = async url => ({
    ok: true,
    json: async () => ({ versions: url.includes('terminal.gui.editor')
      ? ['2.5.7', '2.5.8-develop.1']
      : ['2.5.0', '2.5.1', '2.5.2-develop.1'] }),
  });
  const args = {
    github, context: { repo: { owner: 'harder', repo: 'gh-skillview' } },
    core: { info: () => {}, setOutput: (key, value) => outputs.push([key, JSON.parse(value)]) }, project, locator, fetchImpl,
  };

  await monitor(args);
  assert.equal(issues.length, 2);
  assert.deepEqual(issues.map(issue => issue.title), [
    'Terminal.Gui 2.5.1 compatibility review',
    'GitHub CLI v2.102.0 compatibility review',
  ]);
  assert.ok(issues.every(issue => issue.assignees[0] === 'harder' && issue.labels[0] === 'critical-dependency'));
  assert.match(issues[0].body, /keyboard shortcuts/);
  assert.match(issues[1].body, /Interactive `gh skill search` fixed option injection/);
  assert.match(issues[1].body, /GHSA-qcwj-mr2r-2cx7/);
  assert.match(issues[1].body, /Needs verification/);
  assert.deepEqual(outputs[0], ['new-issues', [
    { number: 1, kind: 'terminal-gui', version: '2.5.1' },
    { number: 2, kind: 'gh', version: 'v2.102.0' },
  ]]);
  await monitor(args);
  assert.equal(issues.length, 2);
  assert.deepEqual(outputs[1], ['new-issues', []]);
});

test('release summaries distinguish direct gh skill notes from unrelated skill content', () => {
  const notes = '## Security\n\nInteractive `gh skill search` changed.\n\nSee https://example.invalid/advisory\n\n* Add a skill for recordings';
  assert.match(monitor.releaseHighlights(notes, 'gh'), /gh skill search/);
  assert.doesNotMatch(monitor.releaseHighlights(notes, 'gh'), /recordings/);
  assert.match(monitor.releaseOverview(notes), /Security section present/);
  assert.match(monitor.releaseHighlights('No CLI changes.', 'gh'), /No directly relevant entry/);
});

test('release highlights keep adjacent unrelated bullets out of skill excerpts', () => {
  const notes = '## Changes\n\n* New repository skill content\n* Fix `gh skill search` option injection\n\nSee https://github.com/cli/cli/security/advisories/GHSA-qcwj-mr2r-2cx7\n\n* Update auth flow';
  const highlight = monitor.releaseHighlights(notes, 'gh');
  assert.match(highlight, /gh skill search/);
  assert.match(highlight, /GHSA-qcwj-mr2r-2cx7/);
  assert.doesNotMatch(highlight, /repository skill content|Update auth flow/);
});

test('manual reassessment accepts only labeled dependency issues with known titles', async () => {
  const github = { rest: { issues: { get: async () => ({ data: {
    title: 'GitHub CLI v2.102.0 compatibility review',
    labels: [{ name: 'critical-dependency' }],
  } }) } } };
  const context = { eventName: 'workflow_dispatch', payload: { inputs: { issue_number: '31' } } };
  assert.deepEqual(await monitor.requestedReassessment(github, context, 'harder', 'gh-skillview'),
    { number: 31, kind: 'gh', version: 'v2.102.0' });
  context.payload.inputs.issue_number = '31; echo unsafe';
  await assert.rejects(() => monitor.requestedReassessment(github, context, 'harder', 'gh-skillview'), /positive issue number/);
  context.payload.inputs.issue_number = '31';
  github.rest.issues.get = async () => ({ data: { title: 'Other issue', labels: [] } });
  await assert.rejects(() => monitor.requestedReassessment(github, context, 'harder', 'gh-skillview'), /not a critical-dependency issue/);
});

test('fails visibly when a critical version cannot be determined', async () => {
  await assert.rejects(() => monitor({
    github: { rest: { issues: { getLabel: async () => {} } } },
    context: { repo: { owner: 'harder', repo: 'gh-skillview' } },
    core: { info: () => {} }, project: '<Project/>', locator,
  }), /TerminalGuiVersion/);
});
