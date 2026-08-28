using System.Text;
using SkillView.Cli;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class Utf8TextWriterStreamTests
{
    [Fact]
    public void Write_DecodesUnicodeAcrossChunkBoundariesWithoutClosingWriter()
    {
        const string expected = "Skill ✓ — 日本語 🚀";
        var bytes = Encoding.UTF8.GetBytes(expected);
        var output = new StringWriter();

        using (var stream = new Utf8TextWriterStream(output))
        {
            foreach (var value in bytes)
            {
                stream.Write([value]);
            }
        }
        output.Write("!");

        Assert.Equal(expected + "!", output.ToString());
    }
}
