namespace GitKeyRouter.App;

public static class TrayResidencePolicy
{
    public static bool ShouldHideOnClose(
        bool keepRunningInTray,
        bool explicitExitRequested,
        CloseReason closeReason)
        => keepRunningInTray
           && !explicitExitRequested
           && closeReason == CloseReason.UserClosing;

    public static bool ShouldHideOnMinimize(bool keepRunningInTray, bool closing)
        => keepRunningInTray && !closing;
}
