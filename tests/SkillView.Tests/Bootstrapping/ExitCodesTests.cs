using SkillView.Bootstrapping;
using Xunit;

namespace SkillView.Tests.Bootstrapping;

/// Lock the exit-code contract. Scripts and agent session hooks rely on these
/// numbers, so they must not drift. If any value here changes, downstream
/// consumers break silently. Update the contract intentionally and document it
/// in the release notes.
public class ExitCodesTests
{
    [Fact]
    public void SuccessIsZero() => Assert.Equal(0, ExitCodes.Success);

    [Fact]
    public void UserErrorIsOne() => Assert.Equal(1, ExitCodes.UserError);

    [Fact]
    public void InvalidUsageIsTwo() => Assert.Equal(2, ExitCodes.InvalidUsage);

    [Fact]
    public void EnvironmentErrorIsTen() => Assert.Equal(10, ExitCodes.EnvironmentError);

    [Fact]
    public void NoMatchesIsTwenty() => Assert.Equal(20, ExitCodes.NoMatches);
}
