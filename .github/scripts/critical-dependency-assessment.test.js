const test = require('node:test');
const assert = require('node:assert/strict');
const { extractAssessment, isOwnedAssessmentComment } = require('./critical-dependency-assessment.js');

const valid = `### What changed
The upstream release fixes a reported security flaw in interactive skill search.

### SkillView impact
The install adapter passes selectors to gh skill install, which needs review.

### Compatibility assessment
**Likely compatible** based on the help contract; install behavior remains untested.

### Focused follow-up
Run a read-only search and check install argument construction before merging.`;

test('accepts a complete assessment after Copilot planning text', () => {
  assert.equal(extractAssessment('Reading files first.\nview path: dependency-issue.md\n\n' + valid), valid);
  const punctuated = valid.replace('**Likely compatible**', '**Likely compatible.**');
  assert.equal(extractAssessment(punctuated), punctuated);
});

test('rejects a tool trace and incomplete sections', () => {
  assert.equal(extractAssessment('Reading files first.\nview path: dependency-issue.md'), null);
  assert.equal(extractAssessment(valid.replace('### Focused follow-up', '### Follow-up')), null);
  assert.equal(extractAssessment(valid.replace('**Likely compatible**', 'Probably fine')), null);
  assert.equal(extractAssessment(valid + ' extra'.repeat(500)), null);
});

test('only the Actions bot owns a marked assessment', () => {
  const body = '<!-- skillview-copilot-assessment-v1 -->';
  assert.equal(isOwnedAssessmentComment({ user: { login: 'github-actions[bot]' }, body }), true);
  assert.equal(isOwnedAssessmentComment({ user: { login: 'another-user' }, body }), false);
  assert.equal(isOwnedAssessmentComment({ user: { login: 'github-actions[bot]' }, body: 'other comment' }), false);
});
