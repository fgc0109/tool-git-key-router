namespace GitKeyRouter.App.Tests;

public sealed class TrayResidencePolicyTests
{
    [Fact]
    public void ClosePolicy_HidesOnlyNormalUserCloseWhenEnabled()
    {
        Assert.True(TrayResidencePolicy.ShouldHideOnClose(true, false, CloseReason.UserClosing));
        Assert.False(TrayResidencePolicy.ShouldHideOnClose(false, false, CloseReason.UserClosing));
        Assert.False(TrayResidencePolicy.ShouldHideOnClose(true, true, CloseReason.UserClosing));
        Assert.False(TrayResidencePolicy.ShouldHideOnClose(true, false, CloseReason.WindowsShutDown));
        Assert.False(TrayResidencePolicy.ShouldHideOnClose(true, false, CloseReason.ApplicationExitCall));
    }

    [Fact]
    public void MinimizePolicy_HidesOnlyWhenEnabledAndNotClosing()
    {
        Assert.True(TrayResidencePolicy.ShouldHideOnMinimize(true, false));
        Assert.False(TrayResidencePolicy.ShouldHideOnMinimize(false, false));
        Assert.False(TrayResidencePolicy.ShouldHideOnMinimize(true, true));
    }
}
