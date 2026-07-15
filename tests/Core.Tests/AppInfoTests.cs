using FluentAssertions;
using VideoSplitJoiner.Core;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Name_ShouldBeVideoSplitJoiner()
    {
        AppInfo.Name.Should().Be("VideoSplitJoiner");
    }
}
