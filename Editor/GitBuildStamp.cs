using System;
using System.IO;
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

        public int callbackOrder => 0;

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

            if (settings.WarnOnDirtyBuild && status.HasChanges && !Application.isBatchMode)
            {
                var message = $"You are building from \"{status.DisplayName}\" with uncommitted changes " +
                              $"({status.Staged} staged, {status.Modified} modified, {status.Untracked} untracked).\n\n" +
                              "The build will not match any commit.";

                if (!EditorUtility.DisplayDialog("Uncommitted changes", message, "Build Anyway", "Cancel Build"))
                    throw new BuildFailedException("Build cancelled: uncommitted changes.");
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

        public void OnPostprocessBuild(BuildReport report) => DeleteStamp();

        static void DeleteStamp()
        {
            if (File.Exists(k_AssetPath)) AssetDatabase.DeleteAsset(k_AssetPath);
        }
    }
}
