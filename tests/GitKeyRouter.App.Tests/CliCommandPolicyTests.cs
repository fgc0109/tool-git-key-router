using GitKeyRouter.App.Cli;

namespace GitKeyRouter.App.Tests;

public sealed class CliCommandPolicyTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-V")]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-H")]
    public void BootstrapCommandsRunWithoutApplicationServices(string command)
    {
        Assert.True(CliApplication.CanRunWithoutServices([command]));
        Assert.False(CliApplication.RequiresExclusiveInstance([command]));
    }

    [Theory]
    [InlineData("diagnose")]
    [InlineData("list-services")]
    [InlineData("list-identities")]
    [InlineData("list-profiles")]
    [InlineData("list-routes")]
    [InlineData("parse-url")]
    [InlineData("resolve-profile")]
    [InlineData("test-service")]
    [InlineData("test-route")]
    [InlineData("test-ssh")]
    [InlineData("gh-login")]
    [InlineData("gh-status")]
    [InlineData("gh-resolve")]
    [InlineData("gh")]
    [InlineData("apply")]
    [InlineData("apply-profiles")]
    public void ReadOnlyAndPreviewCommandsDoNotTakeExclusiveInstanceLock(string command)
    {
        Assert.False(CliApplication.CanRunWithoutServices([command]));
        Assert.False(CliApplication.RequiresExclusiveInstance([command]));
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("APPLY-PROFILES")]
    public void ConfirmedMutatingCommandsTakeExclusiveInstanceLock(string command)
    {
        Assert.True(CliApplication.RequiresExclusiveInstance([command, "--YES"]));
    }

    [Fact]
    public void GuiStartupTakesExclusiveInstanceLock()
    {
        Assert.False(CliApplication.CanRunWithoutServices([]));
        Assert.True(CliApplication.RequiresExclusiveInstance([]));
    }
}
