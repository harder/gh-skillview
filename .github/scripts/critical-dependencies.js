const fs = require('node:fs');

const LABEL = 'critical-dependency';
const TERMINAL_GUI_REPO = 'Terminal.Gui';

function versionParts(value) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)$/.exec(value);
  return match ? match.slice(1).map(Number) : null;
}

function compareVersions(left, right) {
  const a = versionParts(left);
  const b = versionParts(right);
  if (!a || !b) throw new Error(`Expected stable versions, got ${left} and ${right}`);
  for (let index = 0; index < a.length; index++) {
    if (a[index] !== b[index]) return a[index] - b[index];
  }
  return 0;
}

function propertyVersion(project, property) {
  const expression = new RegExp(`<${property}(?:\\s[^>]*)?>(\\d+\\.\\d+\\.\\d+)<\\/${property}>`);
  const match = expression.exec(project);
  if (!match) throw new Error(`Could not read ${property} from SkillView.Core.csproj`);
  return match[1];
}

function minimumGhVersion(source) {
  const match = /MinimumVersion\s*=\s*new\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)/.exec(source);
  if (!match) throw new Error('Could not read GhBinaryLocator.MinimumVersion');
  return match.slice(1).join('.');
}

function suggestedChecks(notes, kind) {
  const text = (notes || '').toLowerCase();
  const checks = [];
  if (kind === 'terminal-gui') {
    if (/key|input|paste|mouse/.test(text)) checks.push('keyboard shortcuts, text fields, paste, and focus behavior');
    if (/layout|scroll|table|render/.test(text)) checks.push('table layout, scrolling, terminal resize, and theme rendering');
    if (/async|thread|dispatch|cancel|shutdown/.test(text)) checks.push('UI dispatch, cancellation ownership, modal completion, and shutdown');
    if (/trim|aot|reflection/.test(text)) checks.push('standalone and extension Native AOT warnings and startup');
  } else {
    if (/skill|agent/.test(text)) checks.push('`gh skill` subcommands, agent selectors, and install defaults');
    if (/json|output|format/.test(text)) checks.push('JSON inventory/search output and parser fixtures');
    if (/extension|path|auth/.test(text)) checks.push('extension launch, `GH_PATH`, authentication, and subprocess selection');
  }
  return checks.length ? checks.map(check => `- ${check}`).join('\n')
    : '- Compare upstream changes with SkillView adapters and add focused tests for any affected behavior.';
}

function releaseHighlights(notes, kind) {
  const paragraphs = (notes || '').split(/\n\s*\n/).map(item => item.trim()).filter(Boolean);
  const pattern = kind === 'gh'
    ? /\bgh skills?\b|\bskill (?:search|install|update|list|preview)\b/i
    : /terminal\.gui|keyboard|input|layout|scroll|render|thread|cancel|aot|trim/i;
  const matches = [];
  for (let index = 0; index < paragraphs.length && matches.length < 5; index++) {
    if (!pattern.test(paragraphs[index])) continue;
    let detail = paragraphs[index];
    if (/^See https:\/\/github\.com\//.test(paragraphs[index + 1] || '')) {
      detail += `\n${paragraphs[++index]}`;
    }
    matches.push(detail.slice(0, 1000));
  }
  return matches.length
    ? matches.map(item => `> ${item.replace(/\n/g, '\n> ')}`).join('\n\n')
    : 'No directly relevant entry was found in the published release notes; inspect the upstream diff.';
}

function releaseOverview(notes) {
  const security = /## Security\b/i.test(notes || '')
    ? '- Security section present; review all advisories in the linked upstream notes.'
    : '- No security section identified in the published notes.';
  const changed = (notes || '').split('\n')
    .filter(line => /^\s*[-*]\s+/.test(line) && !/chore\(deps\)|@dependabot/i.test(line))
    .slice(0, 8)
    .map(line => line.trim().replace(/@(?=[A-Za-z0-9-]+)/g, ''));
  return [security, ...changed].join('\n');
}

async function stableNugetVersion(packageName, fetchImpl) {
  const response = await fetchImpl(`https://api.nuget.org/v3-flatcontainer/${packageName.toLowerCase()}/index.json`, {
    signal: AbortSignal.timeout(15000),
  });
  if (!response.ok) throw new Error(`NuGet returned HTTP ${response.status} for ${packageName}`);
  const data = await response.json();
  const stable = (data.versions || []).filter(version => versionParts(version));
  if (!stable.length) throw new Error(`No stable NuGet versions found for ${packageName}`);
  stable.sort((left, right) => compareVersions(right, left));
  return stable[0];
}

async function ensureLabel(github, owner, repo) {
  try {
    await github.rest.issues.getLabel({ owner, repo, name: LABEL });
  } catch (error) {
    if (error.status !== 404) throw error;
    await github.rest.issues.createLabel({
      owner, repo, name: LABEL, color: 'B60205',
      description: 'Upstream release requiring SkillView compatibility review',
    });
  }
}

async function createOnce(github, core, owner, repo, title, body) {
  const issues = await github.paginate(github.rest.issues.listForRepo, {
    owner, repo, labels: LABEL, state: 'all', per_page: 100,
  });
  if (issues.some(issue => !issue.pull_request && issue.title === title)) {
    core.info(`Already tracked: ${title}`);
    return null;
  }
  const { data: issue } = await github.rest.issues.create({
    owner, repo, title, body, labels: [LABEL], assignees: [owner],
  });
  core.info(`Created ${issue.html_url}`);
  return issue.number;
}

async function checkTerminalGui(github, core, owner, repo, project, fetchImpl, newIssues) {
  const packages = [
    ['Terminal.Gui', propertyVersion(project, 'TerminalGuiVersion')],
    ['Terminal.Gui.Editor', propertyVersion(project, 'TerminalGuiEditorVersion')],
  ];
  const { data: releases } = await github.rest.repos.listReleases({
    owner: 'tui-cs', repo: TERMINAL_GUI_REPO, per_page: 30,
  });
  for (const [packageName, current] of packages) {
    const latest = await stableNugetVersion(packageName, fetchImpl);
    if (compareVersions(latest, current) <= 0) {
      core.info(`${packageName} is current at ${current}`);
      continue;
    }
    const release = releases.find(item => !item.draft && item.tag_name.toLowerCase() === `v${latest}`);
    const notes = release?.body || '';
    const number = await createOnce(github, core, owner, repo,
      `${packageName} ${latest} compatibility review`, `
SkillView pins **${packageName} ${current}**; NuGet now has **${latest}**.

- [NuGet package](https://www.nuget.org/packages/${packageName}/${latest})
- [Upstream release](${release?.html_url || 'https://github.com/tui-cs/Terminal.Gui/releases'})
- Find the Dependabot PR and review both Terminal.Gui packages together when appropriate.
- Run locked tests and all four Native AOT publishes; check the extension's remaining trim suppressions.
- Exercise keyboard selection, scrolling, resizing, install dialogs, and shutdown in a real terminal.

### Release overview
${releaseOverview(notes)}

### Potentially relevant release notes
${releaseHighlights(notes, 'terminal-gui')}

### Compatibility status
**Needs verification.** This alert does not claim the new version is compatible or breaking. Copilot will add a separate evidence-based assessment; validate it with tests and human review.

Suggested checks from release notes:
${suggestedChecks(notes, 'terminal-gui')}
`.trim());
    if (number) newIssues.push({ number, kind: 'terminal-gui', version: latest });
  }
}

async function checkGitHubCli(github, core, owner, repo, locator, newIssues) {
  const minimum = minimumGhVersion(locator);
  const { data: release } = await github.rest.repos.getLatestRelease({ owner: 'cli', repo: 'cli' });
  if (release.draft || release.prerelease || !versionParts(release.tag_name)) {
    throw new Error(`Unexpected GitHub CLI latest release: ${release.tag_name}`);
  }
  const number = await createOnce(github, core, owner, repo,
    `GitHub CLI ${release.tag_name} compatibility review`, `
[GitHub CLI ${release.tag_name}](${release.html_url}) is available. SkillView currently requires **gh ${minimum}+**.

- [Upstream release notes](${release.html_url})
- Run the required contract tests against gh ${minimum} and ${release.tag_name.slice(1)}.
- Compare \`gh skill --help\`, search/preview/install/update/list flags, and JSON output with SkillView's adapters.
- Diff \`gh skill install --help\` agent selectors against \`InstallAgentCatalog\` and its tests.
- Check extension launch, \`GH_PATH\`, authentication, install defaults, and any release-note changes to \`gh skill\`.
- Update the minimum only when a needed behavior requires it.

### Release overview
${releaseOverview(release.body)}

### Skill-related release notes
${releaseHighlights(release.body, 'gh')}

### Compatibility status
**Needs verification.** The scheduled contract tests cover command shape and key flags, not every interactive search/install path. Copilot will add a separate evidence-based assessment; validate it with tests and human review.

Suggested checks from release notes:
${suggestedChecks(release.body, 'gh')}
`.trim());
  if (number) newIssues.push({ number, kind: 'gh', version: release.tag_name });
}

async function requestedReassessment(github, context, owner, repo) {
  const raw = context.eventName === 'workflow_dispatch' && context.payload?.inputs?.issue_number;
  if (!raw) return null;
  if (!/^[1-9]\d*$/.test(raw)) throw new Error('issue_number must be a positive issue number');
  const number = Number(raw);
  if (!Number.isSafeInteger(number)) throw new Error('issue_number is too large');
  const { data: issue } = await github.rest.issues.get({ owner, repo, issue_number: number });
  if (issue.pull_request || !issue.labels?.some(label => label.name === LABEL)) {
    throw new Error(`Issue #${number} is not a critical-dependency issue`);
  }
  const gh = /^GitHub CLI (v\d+\.\d+\.\d+) compatibility review$/.exec(issue.title);
  if (gh) return { number, kind: 'gh', version: gh[1] };
  const gui = /^Terminal\.Gui(?:\.Editor)? (\d+\.\d+\.\d+) compatibility review$/.exec(issue.title);
  if (gui) return { number, kind: 'terminal-gui', version: gui[1] };
  throw new Error(`Issue #${number} has an unexpected critical-dependency title`);
}

module.exports = async ({
  github, context, core, fetchImpl = fetch,
  project = fs.readFileSync('src/SkillView.Core/SkillView.Core.csproj', 'utf8'),
  locator = fs.readFileSync('src/SkillView.Core/Gh/GhBinaryLocator.cs', 'utf8'),
}) => {
  const { owner, repo } = context.repo;
  const newIssues = [];
  await ensureLabel(github, owner, repo);
  await checkTerminalGui(github, core, owner, repo, project, fetchImpl, newIssues);
  await checkGitHubCli(github, core, owner, repo, locator, newIssues);
  const reassessment = await requestedReassessment(github, context, owner, repo);
  if (reassessment && !newIssues.some(issue => issue.number === reassessment.number)) {
    newIssues.push(reassessment);
  }
  core.setOutput?.('new-issues', JSON.stringify(newIssues));
};

module.exports.compareVersions = compareVersions;
module.exports.propertyVersion = propertyVersion;
module.exports.minimumGhVersion = minimumGhVersion;
module.exports.suggestedChecks = suggestedChecks;
module.exports.releaseHighlights = releaseHighlights;
module.exports.releaseOverview = releaseOverview;
module.exports.requestedReassessment = requestedReassessment;
