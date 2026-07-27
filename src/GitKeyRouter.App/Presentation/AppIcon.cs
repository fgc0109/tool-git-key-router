using System.Drawing;

namespace GitKeyRouter.App.Presentation;

internal static class AppIcon
{
    private const string ResourceName = "GitKeyRouter.App.Assets.GitKeyRouter.ico";

    public static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded application icon '{ResourceName}' was not found.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
