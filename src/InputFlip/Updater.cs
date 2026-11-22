using System.Reflection;
using InputFlip.Core;
using Velopack;

namespace InputFlip;

public enum UpdateState
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>This copy wasn't installed by the installer (run from source or the portable zip), so it can't update itself.</summary>
    NotInstalled,

    /// <summary>No update source is set.</summary>
    NoSource,

    /// <summary>The update folder doesn't exist (moved, or the app was built on another PC).</summary>
    SourceMissing,

    UpToDate,

    Available,

    Failed,
}

public sealed record UpdateCheck(UpdateState State, string? NewVersion, string Message);

/// <summary>
/// Checks for and installs new versions with Velopack. The source, first that's set: settings.json's updateSource (a
/// folder or web address), a folder baked in by scripts/build-installer.ps1 -LocalUpdates (for testing an update on
/// the PC that builds it), else the project's GitHub releases. Updates are full packages; no deltas.
/// </summary>
internal sealed class Updater
{
    private UpdateManager? _manager;
    private string? _managerSource;
    private UpdateInfo? _pending;

    /// <summary>The releases folder of the build, or null for builds made without the installer script.</summary>
    public static string? BuiltInSource { get; } = typeof(Updater).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateSource")?.Value is { Length: > 0 } value ? value : null;

    /// <summary>Where published versions are: the GitHub repo's releases.</summary>
    public const string DefaultSource = "https://github.com/undah/inputflip";

    public UpdateCheck Last { get; private set; } = new(UpdateState.Unknown, null, "");

    public async Task<UpdateCheck> CheckAsync(string? configuredSource)
    {
        Last = await Task.Run(() => Check(configuredSource));
        return Last;
    }

    private UpdateCheck Check(string? configuredSource)
    {
        string source = string.IsNullOrWhiteSpace(configuredSource) ? BuiltInSource ?? DefaultSource : configuredSource.Trim();

        bool isWeb = source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isWeb)
        {
            source = Environment.ExpandEnvironmentVariables(source);
            if (!Directory.Exists(source))
                return new(UpdateState.SourceMissing, null, $"Can't find the update folder: {source}");
        }

        try
        {
            if (_manager is null || _managerSource != source)
            {
                // A GitHub repo: Velopack reads its releases through GitHub's API. Anything else: a folder or a plain web folder.
                _manager = IsGitHub(source)
                    ? new UpdateManager(new Velopack.Sources.GithubSource(source.TrimEnd('/'), accessToken: null, prerelease: false))
                    : new UpdateManager(source);
                _managerSource = source;
            }
            if (!_manager.IsInstalled)
                return new(UpdateState.NotInstalled, null, "Updates work in the installed app. This copy wasn't installed with the installer.");

            _pending = _manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            if (_pending is null)
                return new(UpdateState.UpToDate, null, $"You have the latest version ({AppInfo.Version}).");

            string version = _pending.TargetFullRelease.Version.ToString();
            Log.Info($"Update available: {version} in {source}");
            return new(UpdateState.Available, version, $"Version {version} is ready to install.");
        }
        catch (Exception ex)
        {
            Log.Error($"Checking for updates in {source} failed", ex);
            return new(UpdateState.Failed, null, IsGitHub(source)
                ? "Couldn't reach GitHub to check for updates. Check the internet connection, then try again."
                : "Couldn't check for updates. The log has the details.");
        }
    }

    private static bool IsGitHub(string source) =>
        source.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the pending update, then closes the app, installs it and starts the new version.</summary>
    public async Task<string?> InstallAsync(Action<int>? progress)
    {
        if (_manager is null || _pending is null) return "There's no update to install. Check again.";
        try
        {
            Log.Info($"Installing update {_pending.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(_pending, progress);
            _manager.ApplyUpdatesAndRestart(_pending);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Installing the update failed", ex);
            return "The update didn't install. The log has the details. Try again, or run the new installer.";
        }
    }
}
