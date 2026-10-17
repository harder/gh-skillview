using System.Text;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class LocalTrackingWriterTests
{
    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void InsertRetainsEveryAuthoredByteIncludingBomCommentsUnknownFieldsAndBody(string newline, bool bom)
    {
        var source = (bom ? "\uFEFF" : "") + string.Join(newline,
            "---", "# author comment", "name: local", "description: test", "custom: {nested: [a, b]}", "---", "# body", "", "raw \u001b body", "");
        var path = Path.Combine(Path.GetTempPath(), "durable \U0001F600 local");
        var output = Encoding.UTF8.GetString(LocalTrackingWriter.Bind(Encoding.UTF8.GetBytes(source), path));
        var (_, metadata, valid) = FrontMatterParser.Parse(output);
        Assert.True(valid); Assert.Equal(path, metadata.LocalPath);
        var start = output.IndexOf("metadata:", StringComparison.Ordinal);
        var end = output.IndexOf(newline, output.IndexOf("local-path:", start, StringComparison.Ordinal), StringComparison.Ordinal) + newline.Length;
        Assert.Equal(source, output.Remove(start, end - start));
    }

    [Fact]
    public void ReplaceChangesOnlyScalarBytesAndKeepsOriginalCommentMappingAndLineEndings()
    {
        var old = Path.Combine(Path.GetTempPath(), "old").Replace('\\', '/');
        var path = Path.Combine(Path.GetTempPath(), "new source");
        var source = "---\r\nname: local\r\ndescription: test\r\nmetadata: # mapping comment\r\n  local-path: '" + old + "' # exact trailing comment\r\n  arbitrary: keep-this\r\n---\r\nbody\r\n";
        var output = Encoding.UTF8.GetString(LocalTrackingWriter.Bind(Encoding.UTF8.GetBytes(source), path));
        var (_, parsed, _) = FrontMatterParser.Parse(output);
        Assert.Equal(path, parsed.LocalPath);
        var first = output.IndexOf("  local-path: ", StringComparison.Ordinal) + "  local-path: ".Length;
        var last = output.IndexOf(" # exact", first, StringComparison.Ordinal);
        Assert.Equal(source.Replace("'" + old + "'", "VALUE", StringComparison.Ordinal), output[..first] + "VALUE" + output[last..]);
    }

    [Fact]
    public void ExistingUnknownScalarMetadataRemainsByteExactWhenLocalMemberInserted()
    {
        var source = "---\nname: local\ndescription: test\nmetadata:\n  author-value: \"17\" # authored\n  owner-note: private\n---\nbody\n";
        var output = Encoding.UTF8.GetString(LocalTrackingWriter.Bind(Encoding.UTF8.GetBytes(source), Path.GetTempPath()));
        var start = output.IndexOf("  local-path: ", StringComparison.Ordinal);
        var end = output.IndexOf('\n', start) + 1;
        Assert.Equal(source, output.Remove(start, end - start));
    }

    [Theory]
    [InlineData("metadata: {local-path: old}\n")]
    [InlineData("metadata: scalar\n")]
    [InlineData("metadata:\n  local-path: |\n    old\n")]
    [InlineData("metadata:\n  local-path: old\n  local-path: other\n")]
    [InlineData("metadata: &source\n  local-path: old\n")]
    [InlineData("metadata:\n  author-map:\n    value: authored\n")]
    [InlineData("metadata:\n  author-list: [keep, this]\n")]
    public void UnsupportedTrackingRefusesInsteadOfReserializingAuthoredYaml(string metadata)
    {
        var source = Encoding.UTF8.GetBytes("---\nname: local\ndescription: test\n" + metadata + "---\nbody\n");
        Assert.Throws<IOException>(() => LocalTrackingWriter.Bind(source, Path.GetTempPath()));
    }

    [Fact]
    public void RelativeSourceRefuses()
    {
        Assert.Throws<IOException>(() => LocalTrackingWriter.Bind(Encoding.UTF8.GetBytes("---\nname: local\ndescription: test\n---\nbody\n"), "relative"));
    }
}
