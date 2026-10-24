using System;
using UnityEngine;

namespace GitToolbar
{
    /// <summary>
    /// Git state the player was built from (branch, commit, uncommitted changes), stamped at build time
    /// by the editor's GitBuildStamp. Use it in bug reports and debug overlays, e.g. "develop@9ba2400*".
    /// </summary>
    [Serializable]
    public class BuildGitInfo
    {
        public const string ResourceName = "BuildGitInfo";

        public string Branch;      // Branch name, or empty when built from a detached HEAD.
        public string Commit;      // Abbreviated commit hash.
        public bool IsDirty;       // Built with uncommitted changes.
        public string BuildTime;   // UTC, ISO 8601.

        static BuildGitInfo s_Current;
        static bool s_Loaded;

        /// <summary>
        /// The stamped info, or null in the editor and in builds made without stamping.
        /// </summary>
        public static BuildGitInfo Current
        {
            get
            {
                // The stamp file only exists while a build runs - never pick it up (or cache it) in the editor.
                if (Application.isEditor) return null;

                if (!s_Loaded)
                {
                    s_Loaded = true;
                    var asset = Resources.Load<TextAsset>(ResourceName);
                    if (asset != null)
                    {
                        s_Current = JsonUtility.FromJson<BuildGitInfo>(asset.text);
                        Resources.UnloadAsset(asset);
                    }
                }
                return s_Current;
            }
        }

        public override string ToString()
        {
            var name = string.IsNullOrEmpty(Branch) ? "detached" : Branch;
            return $"{name}@{Commit}{(IsDirty ? "*" : string.Empty)}";
        }
    }
}
