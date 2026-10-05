const fs = require('node:fs');

const headings = [
  '### What changed',
  '### SkillView impact',
  '### Compatibility assessment',
  '### Focused follow-up',
];

function extractAssessment(raw) {
  if (typeof raw !== 'string') return null;
  // Copilot's silent text mode can include a planning message before its
  // final answer. Publish only the requested, complete assessment.
  const start = raw.lastIndexOf(headings[0]);
  if (start < 0) return null;
  const assessment = raw.slice(start).trim();
  if (assessment.length < 200 || assessment.length > 10000) return null;
  if (assessment.split(/\s+/).length > 500) return null;
  let previous = -1;
  for (const heading of headings) {
    const index = assessment.indexOf(heading);
    if (index <= previous) return null;
    previous = index;
  }
  if (!/\*\*(Likely compatible|Potential break|Unknown)\.?\*\*/.test(assessment)) return null;
  const sections = headings.map((heading, index) => {
    const from = assessment.indexOf(heading) + heading.length;
    const to = index + 1 < headings.length
      ? assessment.indexOf(headings[index + 1]) : assessment.length;
    return assessment.slice(from, to).trim();
  });
  if (sections.some(section => section.length < 15)) return null;
  return assessment;
}

if (require.main === module) {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) throw new Error('Expected input and output file paths');
  const assessment = extractAssessment(fs.readFileSync(input, 'utf8'));
  if (!assessment) throw new Error('Copilot did not produce a complete assessment');
  fs.writeFileSync(output, `${assessment}\n`);
}

module.exports = { extractAssessment };
