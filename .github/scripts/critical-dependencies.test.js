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
          const issue = { title, body, labels, assignees, html_url: `https://example.invalid/${issues.length + 1}` };
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
          body: 'Improve gh skill JSON output.', draft: false, prerelease: false,
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
    core: { info: () => {} }, project, locator, fetchImpl,
  };

  await monitor(args);
  assert.equal(issues.length, 2);
  assert.deepEqual(issues.map(issue => issue.title), [
    'Terminal.Gui 2.5.1 compatibility review',
    'GitHub CLI v2.102.0 compatibility review',
  ]);
  assert.ok(issues.every(issue => issue.assignees[0] === 'harder' && issue.labels[0] === 'critical-dependency'));
  assert.match(issues[0].body, /keyboard shortcuts/);
  assert.match(issues[1].body, /JSON inventory\/search output/);
  await monitor(args);
  assert.equal(issues.length, 2);
});

test('fails visibly when a critical version cannot be determined', async () => {
  await assert.rejects(() => monitor({
    github: { rest: { issues: { getLabel: async () => {} } } },
    context: { repo: { owner: 'harder', repo: 'gh-skillview' } },
    core: { info: () => {} }, project: '<Project/>', locator,
  }), /TerminalGuiVersion/);
});
