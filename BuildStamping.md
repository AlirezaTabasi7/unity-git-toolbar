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
- With **Warn On Dirty Build** on, building with uncommitted changes asks first. Untracked files count. The dialog never appears in batch mode, so CI isn't blocked.
- If git can't be read, the build continues unstamped and logs a warning. Stamping never fails a build.

## Limitations

- Only the player build is stamped, not Addressables content built separately.
- The commit is your local HEAD, which may not be pushed yet.
- `IsDirty` says that there were changes, not which ones.
