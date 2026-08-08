namespace GitKeyRouter.App.Updates;

public static class UpdateProjectLinks
{
    public static readonly Uri Manifest =
        new("https://project-base-mirror.github.io/tool-git-key-router/update.json");

    public static readonly Uri LatestReleaseApi =
        new("https://api.github.com/repos/project-base-mirror/tool-git-key-router/releases/latest");

    public static readonly Uri LatestReleasePage =
        new("https://github.com/project-base-mirror/tool-git-key-router/releases/latest");

    public const string GitHubOwner = "project-base-mirror";
    public const string GitHubRepository = "tool-git-key-router";
}
