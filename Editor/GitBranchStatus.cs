using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DevLocker.VersionControl.WiseGit;
using DevLocker.VersionControl.WiseGit.ContextMenus;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace GitToolbar
{
    /// <summary>
    /// Main toolbar element showing the current git branch with a zsh (powerlevel10k) style status,
    /// e.g. "⚠ main ⇣1⇡2 *1 +2 !5 ?4 ◌1 🔒2". The icon is tinted by the overall repository state.
    /// Click it to show the log, right-click for git actions (via WiseGit). Hold Left Alt while hovering for the color key.
    /// </summary>
    [InitializeOnLoad]
    public static class GitBranchStatus
    {
        const string k_Path = "Version Control/Git Status";
        const string k_PreferencesMenu = "Assets/Git/Git Preferences";
        const int k_MaxLocksPerGroup = 5;
        const int k_MaxStashLabelLength = 50;
        const double k_PollInterval = 0.1;

        // Dark skin / light skin pairs, readable on the respective toolbar backgrounds.
        static readonly Color32 k_Red = Pick("#F85149", "#CF222E");
        static readonly Color32 k_Orange = Pick("#F0883E", "#BC4C00");
        static readonly Color32 k_Magenta = Pick("#DB61A2", "#BF3989");
        static readonly Color32 k_Blue = Pick("#58A6FF", "#0969DA");
        static readonly Color32 k_Yellow = Pick("#E3B341", "#9A6700");
        static readonly Color32 k_Gray = Pick("#8B949E", "#6E7781");
        static readonly Color32 k_Green = Pick("#3FB950", "#1A7F37");
        static readonly Color32 k_Cyan = Pick("#39C5CF", "#1B7C83");
        static readonly Color32 k_Violet = Pick("#A371F7", "#8250DF");

        static readonly Dictionary<Color32, Texture2D> s_Icons = new Dictionary<Color32, Texture2D>();
        static Texture2D s_BaseIcon;

        static readonly bool s_IsClone;
        static GitRepoStatus s_Status;
        static bool s_HasQueried;
        static bool s_IsFetching;
        static bool s_FetchQueued;

        static bool s_AltHeld;
        static double s_NextPollTime;
        static TextElement s_Label;
        static string s_LastTooltip;

        static GitBranchStatus()
        {
            // ParrelSync clones share these scripts but aren't git repositories - stay hidden and never run git there.
            s_IsClone = ParrelSyncSupport.IsClone();
            if (s_IsClone) return;

            // Branch switches and commits usually happen outside Unity, so re-check whenever the editor regains focus.
            EditorApplication.focusChanged += hasFocus => { if (hasFocus) RequestRefresh(); };
            EditorApplication.projectChanged += RequestRefresh;
            EditorSceneManager.sceneSaved += _ => RequestRefresh();
            AssemblyReloadEvents.beforeAssemblyReload += DestroyIcons;

            // Unsaved scenes / prefabs only need a repaint, not a git query.
            EditorSceneManager.sceneDirtied += _ => RepaintToolbar();
            EditorSceneManager.sceneOpened += (_, _) => RepaintToolbar();
            EditorSceneManager.sceneClosed += _ => RepaintToolbar();
            EditorSceneManager.newSceneCreated += (_, _, _) => RepaintToolbar();
            PrefabStage.prefabStageDirtied += _ => RepaintToolbar();
            PrefabStage.prefabSaved += _ => RepaintToolbar();
            PrefabStage.prefabStageOpened += _ => RepaintToolbar();
            PrefabStage.prefabStageClosing += _ => EditorApplication.delayCall += RepaintToolbar;
            Undo.undoRedoPerformed += RepaintToolbar;

            EditorApplication.update += OnUpdate;

            // WiseGit re-scans after asset changes and background fetches (which update the behind count and locks).
            EditorApplication.delayCall += () =>
            {
                var database = GitStatusesDatabase.Instance;
                if (database != null) database.DatabaseChanged += RequestRefresh;
            };

            RequestRefresh();
        }

        [MainToolbarElement(k_Path, defaultDockPosition = MainToolbarDockPosition.Middle, defaultDockIndex = 2)]
        static MainToolbarElement CreateElement()
        {
            var status = s_Status;
            var unsaved = GetUnsavedDocuments();
            var locks = GetLocks();

            var text = status != null ? BuildText(status, unsaved, locks) : "git…";
            var color = status != null ? StateColor(status.State) : k_Gray;
            s_LastTooltip = BuildTooltip(status, unsaved, locks);

            return new MainToolbarButton(new MainToolbarContent(text, GetIcon(color), s_LastTooltip), () => RunAction(GitContextMenusManager.ShowLogAll))
            {
                // Hide entirely in clones, and when git is unavailable or the project is not a repository.
                displayed = !s_IsClone && (status != null || !s_HasQueried),
                populateContextMenu = PopulateActionsMenu
            };
        }

        /// <summary>
        /// Rebuilds the toolbar element from the cached git status (no git query).
        /// </summary>
        public static void RepaintToolbar()
        {
            if (s_IsClone) return;

            MainToolbar.Refresh(k_Path);
            EditorApplication.delayCall += () => ToolbarLabelWidth.Unclamp(k_Path, ref s_Label);
        }

        static void OnUpdate()
        {
            if (EditorApplication.timeSinceStartup < s_NextPollTime) return;
            s_NextPollTime = EditorApplication.timeSinceStartup + k_PollInterval;

            // Re-apply after the toolbar rebuilds its layout (docking changes, window resizes, etc.).
            ToolbarLabelWidth.Unclamp(k_Path, ref s_Label);

            // Tooltips only show while Unity is focused, so only watch the key then.
            if (!LeftAltKey.IsSupported || !InternalEditorUtility.isApplicationActive) return;

            var altHeld = LeftAltKey.IsHeld;
            if (altHeld != s_AltHeld)
            {
                s_AltHeld = altHeld;

                // If our tooltip is already open, swap its text in place so the color key appears without re-hovering.
                var previousTooltip = s_LastTooltip;
                s_LastTooltip = BuildTooltip(s_Status, GetUnsavedDocuments(), GetLocks());
                ToolbarTooltip.ReplaceIfShowing(previousTooltip, s_LastTooltip);

                RepaintToolbar();
            }
        }

        #region Status

        static void RequestRefresh()
        {
            if (s_IsClone) return;

            if (s_IsFetching)
            {
                s_FetchQueued = true;
                return;
            }

            s_IsFetching = true;

            // Runs git on a worker thread so the editor never stalls; Completed is invoked on the main thread.
            var operation = GitAsyncOperation<GitRepoStatus>.Start(_ => GitRepoStatus.Query());
            operation.Completed += op =>
            {
                s_IsFetching = false;
                s_HasQueried = true;
                s_Status = op.Result;
                RepaintToolbar();

                if (s_FetchQueued)
                {
                    s_FetchQueued = false;
                    RequestRefresh();
                }
            };
        }

        static bool IsProtected(GitRepoStatus status) => status != null && GitToolbarSettings.instance.IsProtected(status.Branch);

        // Unsaved changes live only in memory - git can't see them until they are saved.
        static List<string> GetUnsavedDocuments()
        {
            var unsaved = new List<string>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty) unsaved.Add((string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name) + " (scene)");
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.scene.isDirty)
                unsaved.Add(Path.GetFileNameWithoutExtension(prefabStage.assetPath) + " (prefab)");

            return unsaved;
        }

        // Lock info comes from WiseGit's status database (filled when WiseGit fetches remote changes).
        static List<GitStatusData> GetLocks()
        {
            var database = GitStatusesDatabase.Instance;
            if (database == null || !database.IsActive) return new List<GitStatusData>();

            return database.GetAllKnownStatusData(true, false, false)
                .Where(data => data.LockStatus != VCLockStatus.NoLock)
                .OrderBy(data => data.Path)
                .ToList();
        }

        static Color32 StateColor(GitRepoState state)
        {
            switch (state)
            {
                case GitRepoState.Conflicted: return k_Red;
                case GitRepoState.Dirty: return k_Orange;
                case GitRepoState.Diverged: return k_Magenta;
                case GitRepoState.Ahead: return k_Blue;
                case GitRepoState.Behind: return k_Yellow;
                case GitRepoState.NoUpstream: return k_Gray;
                default: return k_Green;
            }
        }

        static string StateDescription(GitRepoState state)
        {
            switch (state)
            {
                case GitRepoState.Conflicted: return "Conflicts or an operation in progress";
                case GitRepoState.Dirty: return "Uncommitted changes";
                case GitRepoState.Diverged: return "Diverged from upstream";
                case GitRepoState.Ahead: return "Commits to push";
                case GitRepoState.Behind: return "Commits to pull";
                case GitRepoState.NoUpstream: return "No upstream branch";
                default: return "Clean and in sync";
            }
        }

        #endregion

        #region Text & Tooltip

        // powerlevel10k order: branch ⇣behind⇡ahead *stashes action ~conflicts +staged !modified ?untracked, then ◌unsaved 🔒locks.
        static string BuildText(GitRepoStatus status, List<string> unsaved, List<GitStatusData> locks)
        {
            var sb = new StringBuilder();
            if (IsProtected(status)) sb.Append(Colorize("⚠", k_Red)).Append(' ');
            sb.Append(status.DisplayName);

            if (status.Behind > 0 || status.Ahead > 0)
            {
                sb.Append(' ');
                if (status.Behind > 0) sb.Append(Colorize($"⇣{status.Behind}", k_Yellow));
                if (status.Ahead > 0) sb.Append(Colorize($"⇡{status.Ahead}", k_Blue));
            }

            AppendCount(sb, "*", status.Stashes, k_Cyan);
            if (status.Action != null) sb.Append(' ').Append(Colorize(status.Action, k_Red));
            AppendCount(sb, "~", status.Conflicts, k_Red);
            AppendCount(sb, "+", status.Staged, k_Green);
            AppendCount(sb, "!", status.Modified, k_Orange);
            AppendCount(sb, "?", status.Untracked, k_Violet);
            AppendCount(sb, "◌", unsaved.Count, k_Magenta);
            if (locks.Count > 0) sb.Append(" 🔒").Append(locks.Count);

            return sb.ToString();
        }

        static void AppendCount(StringBuilder sb, string symbol, int count, Color32 color)
        {
            if (count > 0) sb.Append(' ').Append(Colorize(symbol + count, color));
        }

        static string BuildTooltip(GitRepoStatus status, List<string> unsaved, List<GitStatusData> locks)
        {
            var sb = new StringBuilder();

            if (status == null)
            {
                sb.AppendLine("Reading git status…");
            }
            else
            {
                var target = status.Upstream != null ? $" → {status.Upstream}" : "  (no upstream)";
                var head = status.IsDetached ? $"Detached HEAD at {status.ShortCommit}" : status.Branch;
                sb.AppendLine($"<b>{head}</b>{target}");
                sb.AppendLine(Colorize("●", StateColor(status.State)) + " " + StateDescription(status.State));
                if (IsProtected(status)) sb.AppendLine(Colorize("⚠", k_Red) + " Protected branch: avoid committing here directly");
                sb.AppendLine();

                var details = new StringBuilder();
                AppendDetail(details, $"⇡{status.Ahead}", status.Ahead, k_Blue, "commit(s) to push");
                AppendDetail(details, $"⇣{status.Behind}", status.Behind, k_Yellow, "commit(s) to pull");
                AppendDetail(details, $"*{status.Stashes}", status.Stashes, k_Cyan, "stash(es)");
                if (status.Action != null) details.AppendLine(Colorize(status.Action, k_Red) + "  in progress");
                AppendDetail(details, $"~{status.Conflicts}", status.Conflicts, k_Red, "conflicted file(s)");
                AppendDetail(details, $"+{status.Staged}", status.Staged, k_Green, "staged file(s)");
                AppendDetail(details, $"!{status.Modified}", status.Modified, k_Orange, "modified file(s), not staged");
                AppendDetail(details, $"?{status.Untracked}", status.Untracked, k_Violet, "untracked file(s)");
                sb.Append(details.Length > 0 ? details.ToString() : "Nothing to commit\n");

                if (unsaved.Count > 0)
                {
                    sb.AppendLine(Colorize($"◌{unsaved.Count}", k_Magenta) + "  unsaved, git can't see these yet: " + string.Join(", ", unsaved));
                }

                sb.AppendLine();
                if (status.Upstream == null) sb.AppendLine("<b>Remote</b>  none tracked");
                else if (status.TrackedRemoteUrl == null) sb.AppendLine($"<b>Remote</b>  {status.Upstream}");
                else sb.AppendLine($"<b>Remote</b>  {status.TrackedRemote} · {status.TrackedRemoteUrl}");

                if (status.LastCommitTime.HasValue)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<b>Last commit</b>  {status.LastCommitAuthor} · {FormatAgo(status.LastCommitTime.Value)}");
                }

                AppendLocks(sb, locks);
            }

            sb.AppendLine();
            if (s_AltHeld || !LeftAltKey.IsSupported)
            {
                AppendLegend(sb);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("Hold <b>Left Alt</b> while hovering to show the color key");
            }
            sb.Append("Click to show log · Right-click for git actions");

            return sb.ToString();
        }

        static void AppendLocks(StringBuilder sb, List<GitStatusData> locks)
        {
            if (locks.Count == 0) return;

            sb.AppendLine();
            sb.AppendLine($"<b>Locks</b>  🔒{locks.Count}");
            AppendLockGroup(sb, locks, VCLockStatus.LockedHere, "Locked by you - others can't change these until you push or release them", false);
            AppendLockGroup(sb, locks, VCLockStatus.LockedOther, "Locked by others - don't edit these, your changes can't be pushed", true);
            AppendLockGroup(sb, locks, VCLockStatus.LockedButStolen, "Stolen - you locked these, but someone else holds the lock now", true);
            AppendLockGroup(sb, locks, VCLockStatus.BrokenLock, "Broken - your lock is no longer valid (someone stole and released it)", false);
        }

        static void AppendLockGroup(StringBuilder sb, List<GitStatusData> locks, VCLockStatus lockStatus, string title, bool showOwner)
        {
            var group = locks.Where(data => data.LockStatus == lockStatus).ToList();
            if (group.Count == 0) return;

            var color = lockStatus == VCLockStatus.LockedHere ? k_Green : k_Red;
            sb.AppendLine(Colorize($"{group.Count}  {title}", color));

            foreach (var data in group.Take(k_MaxLocksPerGroup))
            {
                var details = data.LockDetails;
                var owner = showOwner && !string.IsNullOrEmpty(details.Owner) ? $" · {details.Owner}" : string.Empty;
                var when = DateTime.TryParse(details.Date, out var date) ? $" · {FormatAgo(date)}" : string.Empty;
                sb.AppendLine($"    {data.Path}{owner}{when}");
            }

            if (group.Count > k_MaxLocksPerGroup) sb.AppendLine($"    … and {group.Count - k_MaxLocksPerGroup} more");
        }

        static void AppendLegend(StringBuilder sb)
        {
            sb.AppendLine("<b>Icon color</b>");
            sb.AppendLine(Colorize("●", k_Red) + " Red: conflicts, or merge / rebase / cherry-pick in progress");
            sb.AppendLine(Colorize("●", k_Orange) + " Orange: uncommitted changes");
            sb.AppendLine(Colorize("●", k_Magenta) + " Magenta: diverged, both push and pull needed");
            sb.AppendLine(Colorize("●", k_Blue) + " Blue: ahead, commits to push");
            sb.AppendLine(Colorize("●", k_Yellow) + " Yellow: behind, commits to pull (as of last fetch)");
            sb.AppendLine(Colorize("●", k_Gray) + " Gray: no upstream (local branch or detached HEAD)");
            sb.AppendLine(Colorize("●", k_Green) + " Green: clean and in sync");
            sb.AppendLine();
            sb.AppendLine("<b>Text</b>");
            sb.AppendLine(Colorize("⚠", k_Red) + " protected branch (Project Settings > Git Toolbar)");
            sb.AppendLine(Colorize("⇣N", k_Yellow) + " commits behind upstream (pull)");
            sb.AppendLine(Colorize("⇡N", k_Blue) + " commits ahead of upstream (push)");
            sb.AppendLine(Colorize("*N", k_Cyan) + " stashes");
            sb.AppendLine(Colorize("merge", k_Red) + " / " + Colorize("rebase 2/5", k_Red) + " operation in progress");
            sb.AppendLine(Colorize("~N", k_Red) + " conflicted files");
            sb.AppendLine(Colorize("+N", k_Green) + " staged files");
            sb.AppendLine(Colorize("!N", k_Orange) + " modified files, not staged");
            sb.AppendLine(Colorize("?N", k_Violet) + " untracked files");
            sb.AppendLine(Colorize("◌N", k_Magenta) + " unsaved scenes / prefabs, not on disk yet");
            sb.AppendLine("🔒N files locked with Git LFS (reported by WiseGit)");
            sb.AppendLine("@abc1234 detached HEAD at that commit");
        }

        static void AppendDetail(StringBuilder sb, string label, int count, Color32 color, string description)
        {
            if (count > 0) sb.AppendLine(Colorize(label, color) + "  " + description);
        }

        static string FormatAgo(DateTime time)
        {
            var ago = DateTime.Now - time;
            if (ago.TotalMinutes < 1) return "just now";
            if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes} min ago";
            if (ago.TotalDays < 1) return $"{(int)ago.TotalHours} h ago";
            if (ago.TotalDays < 30) return $"{(int)ago.TotalDays} day(s) ago";
            return time.ToString("yyyy-MM-dd");
        }

        static string Colorize(string text, Color32 color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

        static Color32 Pick(string dark, string light)
        {
            ColorUtility.TryParseHtmlString(EditorGUIUtility.isProSkin ? dark : light, out var color);
            return color;
        }

        #endregion

        #region Icon

        // The base icon is the orange git diamond with a white glyph; recolor the diamond and keep the glyph white.
        static Texture2D GetIcon(Color32 tint)
        {
            if (s_Icons.TryGetValue(tint, out var icon) && icon != null) return icon;

            if (s_BaseIcon == null) s_BaseIcon = Resources.Load<Texture2D>("GitToolbarIcon");
            if (s_BaseIcon == null || !s_BaseIcon.isReadable) return s_BaseIcon;

            var pixels = s_BaseIcon.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                // Green channel goes from 80 (orange diamond) to 255 (white glyph) - use it as the blend factor.
                var t = Mathf.InverseLerp(80, 255, p.g);
                var c = Color32.Lerp(tint, new Color32(255, 255, 255, 255), t);
                pixels[i] = new Color32(c.r, c.g, c.b, p.a);
            }

            icon = new Texture2D(s_BaseIcon.width, s_BaseIcon.height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            icon.SetPixels32(pixels);
            icon.Apply();

            s_Icons[tint] = icon;
            return icon;
        }

        static void DestroyIcons()
        {
            foreach (var icon in s_Icons.Values)
            {
                if (icon != null) UnityEngine.Object.DestroyImmediate(icon);
            }
            s_Icons.Clear();
        }

        #endregion

        #region Menus

        static void PopulateActionsMenu(DropdownMenu menu)
        {
            var status = s_Status;
            var hasChanges = status != null && status.HasChanges;
            var hasConflicts = status != null && (status.Conflicts > 0 || status.Action != null);

            AddAction(menu, hasChanges ? "Commit…" : "Commit… (nothing to commit)", hasChanges, Commit);
            AddAction(menu, "Show Log", true, GitContextMenusManager.ShowLogAll);
            AddAction(menu, "Check Changes…", true, GitContextMenusManager.CheckChangesAll);
            menu.AppendSeparator();

            AddAction(menu, WithCount("Pull", "⇣", status?.Behind ?? 0), true, GitContextMenusManager.PullAll);
            AddAction(menu, WithCount("Push", "⇡", status?.Ahead ?? 0), true, GitContextMenusManager.PushAll);
            AddAction(menu, "Fetch", true, GitContextMenusManager.FetchAll);
            AddAction(menu, "Merge…", true, GitContextMenusManager.MergeAll);
            menu.AppendSeparator();

            AddAction(menu, "Resolve Conflicts…", hasConflicts, () => GitContextMenusManager.ResolveAll());
            AddAction(menu, "Revert…", hasChanges, GitContextMenusManager.RevertAll);
            menu.AppendSeparator();

            AppendSwitchToMenu(menu, status);
            AddAction(menu, "Switch Branch…", true, () => GitContextMenusManager.Switch());
            AppendStashMenu(menu, status);
            AddAction(menu, "Repo Browser", status?.Upstream != null, () => GitContextMenusManager.RepoBrowser(".", status.Upstream));
            menu.AppendSeparator();

            AddAction(menu, "Copy Branch Name", status != null, CopyBranchName);
            AddAction(menu, "Git Preferences…", true, () => EditorApplication.ExecuteMenuItem(k_PreferencesMenu));
            AddAction(menu, "Toolbar Settings…", true, () => SettingsService.OpenProjectSettings(GitToolbarSettings.SettingsPath));
            menu.AppendSeparator();

            menu.AppendAction("Refresh Status", _ => RequestRefresh());
        }

        static void AppendSwitchToMenu(DropdownMenu menu, GitRepoStatus status)
        {
            if (status == null || status.RecentBranches.Count == 0)
            {
                AddAction(menu, "Switch To/(no other local branches)", false, null);
                return;
            }

            var canSwitch = !EditorApplication.isPlayingOrWillChangePlaymode;
            foreach (var branch in status.RecentBranches)
            {
                AddAction(menu, "Switch To/" + MenuSafe(branch), canSwitch, () => SwitchTo(branch));
            }
        }

        static void AppendStashMenu(DropdownMenu menu, GitRepoStatus status)
        {
            var canRun = status != null && !EditorApplication.isPlayingOrWillChangePlaymode;
            var hasTrackedChanges = status != null && status.Staged + status.Modified > 0;
            var stashes = status?.StashList ?? new List<GitRepoStatus.StashEntry>();

            AddAction(menu, "Stash/Stash Changes", canRun && hasTrackedChanges,
                () => RunFileOperation("Stash changes", "stash push"));
            AddAction(menu, "Stash/Stash Changes (Include Untracked)", canRun && status.HasChanges,
                () => RunFileOperation("Stash changes", "stash push --include-untracked"));
            AddAction(menu, "Stash/Pop Latest", canRun && stashes.Count > 0,
                () => RunFileOperation("Pop stash", "stash pop"));

            if (stashes.Count == 0) return;

            menu.AppendSeparator("Stash/");
            foreach (var stash in stashes)
            {
                var entry = stash;
                var subject = entry.Subject.Length > k_MaxStashLabelLength ? entry.Subject.Substring(0, k_MaxStashLabelLength) + "…" : entry.Subject;
                var submenu = $"Stash/{MenuSafe($"{entry.Ref}  {subject}  ({entry.Age})")}/";

                AddAction(menu, submenu + "Apply", canRun, () => RunFileOperation($"Apply {entry.Ref}", $"stash apply {entry.Ref}"));
                AddAction(menu, submenu + "Pop", canRun, () => RunFileOperation($"Pop {entry.Ref}", $"stash pop {entry.Ref}"));
                AddAction(menu, submenu + "Drop…", canRun, () => DropStash(entry));
            }
        }

        // '/' would create nested submenus - show a look-alike instead.
        static string MenuSafe(string label) => label.Replace('/', '∕');

        static void AddAction(DropdownMenu menu, string label, bool enabled, Action action)
        {
            menu.AppendAction(label, _ => RunAction(action),
                _ => enabled ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        static void RunAction(Action action)
        {
            // Defer so menus close and the click finishes before WiseGit possibly blocks the editor (pull / push / fetch wait for the tool).
            EditorApplication.delayCall += () =>
            {
                action();
                RequestRefresh();
            };
        }

        static void Commit()
        {
            if (IsProtected(s_Status) && !EditorUtility.DisplayDialog("Protected branch",
                    $"\"{s_Status.Branch}\" is a protected branch. Commits usually go to a feature branch and get merged.\n\nCommit here anyway?",
                    "Commit Anyway", "Cancel"))
                return;

            GitContextMenusManager.CommitAll();
        }

        static void SwitchTo(string branch)
        {
            var status = s_Status;
            if (status != null && status.Staged + status.Modified + status.Conflicts > 0 &&
                !EditorUtility.DisplayDialog("Uncommitted changes",
                    $"You have uncommitted changes. Git carries them over to \"{branch}\" when it can, " +
                    "and refuses to switch if they would be overwritten.\n\nSwitch anyway?", "Switch", "Cancel"))
                return;

            RunFileOperation($"Switch to \"{branch}\"", $"switch \"{branch}\"");
        }

        static void DropStash(GitRepoStatus.StashEntry entry)
        {
            if (!EditorUtility.DisplayDialog("Drop stash", $"Permanently delete {entry.Ref}?\n\n{entry.Subject}", "Drop", "Cancel"))
                return;

            // Dropping doesn't touch working copy files, so no save prompt or asset refresh is needed.
            var error = GitRepoStatus.RunFileOperation($"stash drop {entry.Ref}");
            if (error != null) EditorUtility.DisplayDialog("Git", $"Drop {entry.Ref} failed:\n\n{error}", "OK");
        }

        /// <summary>
        /// Runs a git command that changes files on disk: saves first, blocks Unity while git runs, then refreshes assets.
        /// </summary>
        static void RunFileOperation(string description, string args)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            AssetDatabase.SaveAssets();

            // git runs on the main thread on purpose: Unity must not import assets while files are being swapped.
            string error;
            EditorUtility.DisplayProgressBar("Git", $"{description}…", 0.5f);
            try
            {
                error = GitRepoStatus.RunFileOperation(args);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // Refresh even on failure - e.g. a stash pop with conflicts still changes files.
            AssetDatabase.Refresh();

            if (error != null) EditorUtility.DisplayDialog("Git", $"{description} failed:\n\n{error}", "OK");
        }

        static string WithCount(string label, string symbol, int count) => count > 0 ? $"{label}  {symbol}{count}" : label;

        static void CopyBranchName()
        {
            if (s_Status == null) return;
            EditorGUIUtility.systemCopyBuffer = s_Status.IsDetached ? s_Status.ShortCommit : s_Status.Branch;
        }

        #endregion
    }
}
