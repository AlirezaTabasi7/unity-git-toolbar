using System;
using System.IO;
using DevLocker.VersionControl.WiseGit;
using DevLocker.VersionControl.WiseGit.Shell;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GitToolbar
{
    /// <summary>
    /// Stamps the git branch / commit / dirty flag into player builds as Resources/BuildGitInfo.json
    /// (read at runtime through BuildGitInfo.Current), and optionally warns before building with uncommitted changes.
    /// The file only exists for the duration of the build and is git-ignored in case a build fails midway.
    /// </summary>
    [InitializeOnLoad]
    public class GitBuildStamp : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string k_AssetPath = "Assets/Resources/" + BuildGitInfo.ResourceName + ".json";

        // Read git status before other build hooks write their own temporary files (e.g. the Performance
        // Testing package's Resources JSON), so a clean tree isn't reported as dirty. Another hook can use
        // int.MinValue too, and the order among equal values isn't defined, so untracked files created after
        // the build started are also ignored (see IgnoreFilesCreatedDuringBuild).
        public int callbackOrder => int.MinValue;

        // A build start time further than this from "now" is treated as unreliable (e.g. a time zone mix-up;
        // real offsets are at least 30 minutes), and nothing is ignored.
        static readonly TimeSpan k_MaxBuildStartAge = TimeSpan.FromMinutes(25);

        static GitBuildStamp()
        {
            // Remove a stamp left behind by a build that failed after preprocessing.
            EditorApplication.delayCall += () =>
            {
                if (!BuildPipeline.isBuildingPlayer) DeleteStamp();
            };
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = GitToolbarSettings.instance;
            if (!settings.StampBuilds && !settings.WarnOnDirtyBuild) return;

            var status = GitRepoStatus.Query();
            if (status == null)
            {
                Debug.LogWarning("[GitBuildStamp] Could not read git status - the build is not stamped.");
                return;
            }

            IgnoreFilesCreatedDuringBuild(status, report.summary.buildStartedAt);

            if (settings.WarnOnDirtyBuild && status.HasChanges && !Application.isBatchMode)
            {
                var message = $"You are building from \"{status.DisplayName}\" with uncommitted changes " +
                              $"({status.Staged} staged, {status.Modified} modified, {status.Untracked} untracked).\n\n" +
                              "The build will not match any commit.";

                if (!EditorUtility.DisplayDialog("Uncommitted changes", message, "Build Anyway", "Cancel Build"))
                    throw new BuildFailedException("Build canceled: uncommitted changes.");
            }

            if (!settings.StampBuilds) return;

            var info = new BuildGitInfo
            {
                Branch = status.Branch ?? string.Empty,
                Commit = status.ShortCommit,
                IsDirty = status.HasChanges,
                BuildTime = DateTime.UtcNow.ToString("o"),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(k_AssetPath));
            File.WriteAllText(k_AssetPath, JsonUtility.ToJson(info, true));
            AssetDatabase.ImportAsset(k_AssetPath, ImportAssetOptions.ForceSynchronousImport);

            Debug.Log($"[GitBuildStamp] Stamped build with {info}.");
        }

        /// <summary>
        /// Recounts untracked files, leaving out those created after the build started: they were written by
        /// other build hooks that ran before this one, not left over from before the build. Tracked files that
        /// other hooks modify can't be told apart from earlier edits, so for those only the callback order helps.
        /// </summary>
        static void IgnoreFilesCreatedDuringBuild(GitRepoStatus status, DateTime buildStarted)
        {
            if (status.Untracked == 0) return;

            // Unity reports the build start in UTC but with DateTimeKind.Unspecified, which ToUniversalTime()
            // would treat as local time.
            var startUtc = buildStarted.Kind == DateTimeKind.Local
                ? buildStarted.ToUniversalTime()
                : DateTime.SpecifyKind(buildStarted, DateTimeKind.Utc);
            var age = DateTime.UtcNow - startUtc;
            if (age < TimeSpan.Zero || age > k_MaxBuildStartAge)
            {
                Debug.Log($"[GitBuildStamp] Build start time looks wrong ({buildStarted:o}, {buildStarted.Kind}), " +
                          "so files created during the build aren't ignored.");
                return;
            }

            var root = WiseGitIntegration.ProjectRootNative;
            // One path per untracked file (not per new folder like git status), unquoted, NUL-separated.
            // ":/" covers the whole repository like git status, even when the project is in a subfolder.
            var result = ShellUtils.ExecuteCommand("git", "-c core.quotepath=off ls-files --others --exclude-standard -z -- :/", root, 10000);
            if (result.ErrorCode != 0 || result.Output == null) return;

            var untracked = 0;
            var ignored = 0;
            foreach (var path in result.Output.Split('\0'))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var fullPath = Path.Combine(root, path.Trim('\r', '\n'));
                if (File.Exists(fullPath) && File.GetCreationTimeUtc(fullPath) >= startUtc) ignored++;
                else untracked++;
            }

            if (ignored == 0) return;

            status.Untracked = untracked;
            Debug.Log($"[GitBuildStamp] Ignored {ignored} untracked file(s) created during the build by other build hooks.");
        }

        public void OnPostprocessBuild(BuildReport report) => DeleteStamp();

        static void DeleteStamp()
        {
            if (File.Exists(k_AssetPath)) AssetDatabase.DeleteAsset(k_AssetPath);
        }
    }
}
