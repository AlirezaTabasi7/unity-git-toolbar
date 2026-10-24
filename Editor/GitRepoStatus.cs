using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DevLocker.VersionControl.WiseGit;
using DevLocker.VersionControl.WiseGit.Shell;

namespace GitToolbar
{
    /// <summary>
    /// Overall repository state, ordered from most to least urgent. Drives the toolbar icon color.
    /// </summary>
    public enum GitRepoState
    {
        Conflicted,   // Conflicts, or a merge / rebase / cherry-pick / revert / bisect in progress.
        Dirty,        // Uncommitted changes (staged, modified or untracked).
        Diverged,     // Both ahead of and behind the upstream.
        Ahead,        // Committed but not pushed.
        Behind,       // Upstream has commits to pull (as of the last fetch).
        NoUpstream,   // Local-only branch or detached HEAD.
        Clean,        // Nothing to commit, in sync with upstream.
    }

    public class GitRepoStatus
    {
        const int k_CommandTimeout = 10000;
        const int k_FileOperationTimeout = 60000;
        const int k_RecentBranchCount = 10;

        public string Branch;        // Branch name, or null when HEAD is detached.
        public string ShortCommit;   // Abbreviated HEAD commit.
        public string Upstream;      // E.g. origin/develop, or null when there is none.
        public int Ahead;
        public int Behind;
        public int Stashes;
        public int Conflicts;
        public int Staged;
        public int Modified;
        public int Untracked;
        public string Action;        // E.g. "merge", "rebase 2/5", or null.

        public string TrackedRemote;     // Remote the upstream belongs to, e.g. origin.
        public string TrackedRemoteUrl;  // Its fetch URL, shortened, e.g. github.com/org/repo.

        public string LastCommitAuthor;
        public DateTime? LastCommitTime;

        public readonly List<string> RecentBranches = new List<string>();  // Other local branches, most recently committed first.
        public readonly List<StashEntry> StashList = new List<StashEntry>(); // Newest first.

        public struct StashEntry
        {
            public string Ref;       // E.g. stash@{0}
            public string Subject;   // E.g. "On develop: before refactor"
            public string Age;       // E.g. "2 hours ago"
        }

        public bool IsDetached => Branch == null;
        public string DisplayName => IsDetached ? $"@{ShortCommit}" : Branch;
        public bool HasChanges => Staged + Modified + Untracked + Conflicts > 0;

        public GitRepoState State
        {
            get
            {
                if (Conflicts > 0 || Action != null) return GitRepoState.Conflicted;
                if (HasChanges) return GitRepoState.Dirty;
                if (Upstream == null) return GitRepoState.NoUpstream;
                if (Ahead > 0 && Behind > 0) return GitRepoState.Diverged;
                if (Ahead > 0) return GitRepoState.Ahead;
                if (Behind > 0) return GitRepoState.Behind;
                return GitRepoState.Clean;
            }
        }

        /// <summary>
        /// Runs git synchronously - call it from a worker thread (see GitAsyncOperation).
        /// Returns null when git is unavailable or the project is not inside a repository.
        /// </summary>
        public static GitRepoStatus Query()
        {
            var root = WiseGitIntegration.ProjectRootNative;
            var result = ShellUtils.ExecuteCommand("git", "status --porcelain=v2 --branch --show-stash", root, k_CommandTimeout);
            // Check the exit code rather than HasErrors - git prints harmless warnings (e.g. line endings) to stderr.
            if (result.ErrorCode != 0 || string.IsNullOrEmpty(result.Output)) return null;

            var status = new GitRepoStatus();
            foreach (var line in result.Output.Split('\n'))
            {
                status.ParseLine(line);
            }

            var gitDir = ShellUtils.ExecuteCommand("git", "rev-parse --absolute-git-dir", root, k_CommandTimeout).Output;
            if (!string.IsNullOrEmpty(gitDir))
            {
                status.Action = DetectAction(gitDir.Trim());
            }

            if (status.Upstream != null)
            {
                status.ParseTrackedRemote(ShellUtils.ExecuteCommand("git", "remote -v", root, k_CommandTimeout).Output);
            }

            // Author name and unix commit time, separated by a unit separator.
            var lastCommit = ShellUtils.ExecuteCommand("git", "log -1 --format=%an%x1f%ct", root, k_CommandTimeout).Output;
            var commitParts = lastCommit?.Split('\x1f');
            if (commitParts?.Length == 2 && long.TryParse(commitParts[1].Trim(), out var unixTime))
            {
                status.LastCommitAuthor = commitParts[0];
                status.LastCommitTime = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
            }

            var branches = ShellUtils.ExecuteCommand("git", $"for-each-ref --sort=-committerdate --count={k_RecentBranchCount + 1} --format=%(refname:short) refs/heads/", root, k_CommandTimeout).Output;
            if (!string.IsNullOrEmpty(branches))
            {
                foreach (var branch in branches.Split('\n'))
                {
                    var name = branch.Trim();
                    if (name.Length > 0 && name != status.Branch && status.RecentBranches.Count < k_RecentBranchCount)
                        status.RecentBranches.Add(name);
                }
            }

            if (status.Stashes > 0)
            {
                // Ref, subject and relative age, separated by unit separators.
                var stashes = ShellUtils.ExecuteCommand("git", "stash list --format=%gd%x1f%gs%x1f%cr", root, k_CommandTimeout).Output;
                if (!string.IsNullOrEmpty(stashes))
                {
                    foreach (var line in stashes.Split('\n'))
                    {
                        var parts = line.Split('\x1f');
                        if (parts.Length == 3)
                            status.StashList.Add(new StashEntry { Ref = parts[0], Subject = parts[1], Age = parts[2].Trim() });
                    }
                }
            }

            return status;
        }

        /// <summary>
        /// Runs a git command that changes working copy files (switch, stash, ...) synchronously on the calling (main) thread,
        /// so Unity cannot import while files change. Returns the git error, or null on success.
        /// </summary>
        public static string RunFileOperation(string args)
        {
            var result = ShellUtils.ExecuteCommand("git", args, WiseGitIntegration.ProjectRootNative, k_FileOperationTimeout);
            if (result.ErrorCode == 0) return null;

            // git reports some failures (e.g. stash conflicts) on stdout.
            var message = string.Join("\n", new[] { result.Error, result.Output }).Trim();
            return string.IsNullOrEmpty(message) ? $"git exited with code {result.ErrorCode}" : message;
        }

        // "git remote -v" lines look like: "origin\thttps://host/repo.git (fetch)"
        void ParseTrackedRemote(string output)
        {
            if (string.IsNullOrEmpty(output)) return;

            // Remote names may contain '/', so pick the longest remote name that prefixes the upstream.
            foreach (var line in output.Split('\n'))
            {
                if (!line.EndsWith(" (fetch)")) continue;

                var tab = line.IndexOf('\t');
                if (tab < 0) continue;

                var name = line.Substring(0, tab);
                if (!Upstream.StartsWith(name + "/") || name.Length <= (TrackedRemote?.Length ?? 0)) continue;

                TrackedRemote = name;
                TrackedRemoteUrl = ShortenUrl(line.Substring(tab + 1, line.Length - tab - 1 - " (fetch)".Length));
            }
        }

        // https://user:token@host/org/repo.git -> host/org/repo, git@host:org/repo.git -> host/org/repo
        static string ShortenUrl(string url)
        {
            url = Regex.Replace(url, @"^\w+://", string.Empty);
            url = Regex.Replace(url, @"^[^/@]+@", string.Empty);       // Credentials or ssh user - never show tokens.
            url = Regex.Replace(url, @"^([^/:]{2,}):(?!\d)", "$1/");   // scp-like ssh syntax (leaves ports and drive letters alone).
            if (url.EndsWith(".git")) url = url.Substring(0, url.Length - 4);
            return url.TrimEnd('/');
        }

        void ParseLine(string line)
        {
            if (line.Length < 2) return;

            if (line.StartsWith("# "))
            {
                var space = line.IndexOf(' ', 2);
                if (space < 0) return;
                var key = line.Substring(2, space - 2);
                var value = line.Substring(space + 1);

                switch (key)
                {
                    case "branch.oid":
                        ShortCommit = value.Length > 7 ? value.Substring(0, 7) : value;
                        break;
                    case "branch.head":
                        Branch = value == "(detached)" ? null : value;
                        break;
                    case "branch.upstream":
                        Upstream = value;
                        break;
                    case "branch.ab":
                        // Format: "+<ahead> -<behind>"
                        var parts = value.Split(' ');
                        if (parts.Length == 2)
                        {
                            int.TryParse(parts[0].TrimStart('+'), out Ahead);
                            int.TryParse(parts[1].TrimStart('-'), out Behind);
                        }
                        break;
                    case "stash":
                        int.TryParse(value, out Stashes);
                        break;
                }
                return;
            }

            switch (line[0])
            {
                // Ordinary ("1") and renamed/copied ("2") entries: "<type> <XY> ..." where X is the index, Y the work tree.
                case '1':
                case '2':
                    if (line.Length < 4) return;
                    if (line[2] != '.') Staged++;
                    if (line[3] != '.') Modified++;
                    break;
                case 'u':
                    Conflicts++;
                    break;
                case '?':
                    Untracked++;
                    break;
            }
        }

        static string DetectAction(string gitDir)
        {
            var rebaseMerge = Path.Combine(gitDir, "rebase-merge");
            if (Directory.Exists(rebaseMerge))
                return "rebase" + ReadProgress(rebaseMerge, "msgnum", "end");

            var rebaseApply = Path.Combine(gitDir, "rebase-apply");
            if (Directory.Exists(rebaseApply))
            {
                var name = File.Exists(Path.Combine(rebaseApply, "applying")) ? "am" : "rebase";
                return name + ReadProgress(rebaseApply, "next", "last");
            }

            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
            if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return "bisect";
            return null;
        }

        static string ReadProgress(string dir, string currentFile, string totalFile)
        {
            try
            {
                var current = File.ReadAllText(Path.Combine(dir, currentFile)).Trim();
                var total = File.ReadAllText(Path.Combine(dir, totalFile)).Trim();
                return $" {current}/{total}";
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }
}
