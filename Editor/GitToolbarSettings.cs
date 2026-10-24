using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace GitToolbar
{
    /// <summary>
    /// Project-wide settings for the git toolbar and build stamping.
    /// Saved in ProjectSettings so the whole team shares them. Edit them in Project Settings > Git Toolbar.
    /// </summary>
    [FilePath("ProjectSettings/GitToolbarSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class GitToolbarSettings : ScriptableSingleton<GitToolbarSettings>
    {
        public const string SettingsPath = "Project/Git Toolbar";

        [Tooltip("Branches you should not commit to directly. Supports * wildcards, e.g. release/*")]
        public List<string> ProtectedBranches = new List<string> { "main", "master", "release/*" };

        [Tooltip("Write the branch, commit and dirty flag into player builds (readable at runtime via BuildGitInfo).")]
        public bool StampBuilds = true;

        [Tooltip("Ask before building with uncommitted changes. Never asks in batch mode (CI).")]
        public bool WarnOnDirtyBuild = true;

        public bool IsProtected(string branch)
        {
            if (string.IsNullOrEmpty(branch)) return false;

            foreach (var pattern in ProtectedBranches)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(branch, regex)) return true;
            }
            return false;
        }

        public void Save() => Save(true);

        class Provider : SettingsProvider
        {
            SerializedObject m_SerializedSettings;

            Provider() : base(SettingsPath, SettingsScope.Project, new[] { "git", "branch", "build", "stamp", "protected" }) { }

            [SettingsProvider]
            static SettingsProvider Create() => new Provider();

            public override void OnGUI(string searchContext)
            {
                var settings = instance;
                settings.hideFlags &= ~HideFlags.NotEditable;

                m_SerializedSettings ??= new SerializedObject(settings);
                m_SerializedSettings.Update();

                EditorGUI.BeginChangeCheck();
                using (new EditorGUIUtility.IconSizeScope(Vector2.zero))
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Toolbar", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(m_SerializedSettings.FindProperty(nameof(ProtectedBranches)), true);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Builds", EditorStyles.boldLabel);
                    EditorGUILayout.PropertyField(m_SerializedSettings.FindProperty(nameof(StampBuilds)));
                    EditorGUILayout.PropertyField(m_SerializedSettings.FindProperty(nameof(WarnOnDirtyBuild)));
                }

                if (EditorGUI.EndChangeCheck())
                {
                    m_SerializedSettings.ApplyModifiedProperties();
                    settings.Save();
                    GitBranchStatus.RepaintToolbar();
                }
            }
        }
    }
}
