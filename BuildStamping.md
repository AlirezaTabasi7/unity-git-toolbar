# Build Stamping

Part of the [Git Toolbar](README.md).

Every player build records the git branch and commit it was built from, and whether there were uncommitted changes. A bug report can then say `develop@9ba2400*` instead of "the build from Tuesday".

## Setup

It's on by default. Add these lines to your `.gitignore`, so a stamp left behind by a failed build is never committed:

```gitignore
/Assets/Resources/BuildGitInfo.json
/Assets/Resources/BuildGitInfo.json.meta
```

## Usage

```csharp
using GitToolbar;

var git = BuildGitInfo.Current;   // null in the editor and in unstamped builds
versionText.text = git != null ? $"{Application.version} ({git})" : Application.version;
// 1.4.0 (develop@9ba2400*)
```

`*` marks a build with uncommitted changes. The individual fields are `Branch`, `Commit`, `IsDirty` and `BuildTime`. Scripts in your own assembly definitions need a reference to `GitToolbar.Runtime`.

## How it works

- Before each player build, from the editor, a build script or CI, the stamp is written to `Assets/Resources/BuildGitInfo.json`. It's deleted after the build, or on the next editor load if the build failed.
- With **Warn On Dirty Build** on, building with uncommitted changes asks first. Untracked files count. The check runs before other build hooks, and untracked files created after the build started are ignored, so files other hooks create during the build don't count. The dialog never appears in batch mode, so CI isn't blocked.
- If git can't be read, the build continues unstamped and logs a warning. Stamping never fails a build.

## Limitations

- Only the player build is stamped, not Addressables content built separately.
- The commit is your local HEAD, which may not be pushed yet.
- `IsDirty` says that there were changes, not which ones.
- A tracked file that another build hook modifies before the check counts as a change. That can only happen if the hook also uses `int.MinValue` as its callback order.
- If the warning lists Unity files you didn't change and `git diff` is empty, the cause is usually line endings: git checks the files out with CRLF and Unity saves them with LF. [This gist](https://gist.github.com/AlirezaTabasi7/7b8a36c9e5a0e2691f5dce8440d79656) explains the `.gitattributes` fix.
