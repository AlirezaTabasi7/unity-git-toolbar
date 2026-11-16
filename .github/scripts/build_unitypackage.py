"""Builds a .unitypackage from a folder of Unity assets, without Unity.

A .unitypackage is a gzipped tar with one directory per asset GUID, holding
`asset` (the file's bytes, omitted for folders), `asset.meta` and `pathname`
(the asset's path in the importing project).

The install folder itself gets an entry too, with the given GUID, since Unity
skips assets whose parent folder is neither in the package nor in the project.
Keep that GUID the same across releases so updates land in the same folder.

Usage: build_unitypackage.py <source folder> <install path> <folder guid> <output file>
  e.g. build_unitypackage.py . "Assets/Git Toolbar" 111ff15a44017d041b2819456acf4c39 GitToolbar-1.1.0.unitypackage
"""

import gzip
import io
import os
import re
import sys
import tarfile
import time

NOW = int(time.time())  # Entries need a real modification time, like Unity's own exports.
GUID = re.compile(r"^guid:\s*([0-9a-f]{32})\s*$", re.MULTILINE)


def ignored(name):
    # Unity skips hidden names and names ending in "~" (e.g. Documentation~); keep them out of the package too.
    return name.startswith(".") or name.endswith("~") or name.endswith(".meta")


def add_bytes(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = 0o777
    info.mtime = NOW
    tar.addfile(info, io.BytesIO(data))


def add_directory(tar, name):
    # Unity's importer skips assets whose GUID directory has no entry of its own.
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    info.mode = 0o777
    info.mtime = NOW
    tar.addfile(info)


FOLDER_META = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
"""


def main(source, install_path, folder_guid, output):
    source = os.path.abspath(source)
    install_path = install_path.strip("/")
    entries = []

    for root, dirs, files in os.walk(source):
        dirs[:] = sorted(d for d in dirs if not ignored(d))
        for name in sorted(dirs) + sorted(f for f in files if not ignored(f)):
            path = os.path.join(root, name)
            meta = path + ".meta"
            relative = os.path.relpath(path, source).replace(os.sep, "/")
            if not os.path.isfile(meta):
                sys.exit(f"error: {relative} has no .meta file")
            with open(meta, "rb") as f:
                meta_bytes = f.read()
            match = GUID.search(meta_bytes.decode("utf-8"))
            if not match:
                sys.exit(f"error: no guid in {relative}.meta")
            entries.append((match.group(1), path, relative, meta_bytes))

    guids = [e[0] for e in entries]
    if len(set(guids)) != len(guids):
        sys.exit("error: duplicate guids")

    if not re.fullmatch(r"[0-9a-f]{32}", folder_guid) or folder_guid in guids:
        sys.exit("error: the folder guid must be 32 hex digits and unused")

    # Match Unity's own exports: GNU tar headers, and "archtemp.tar" as the name inside the gzip header.
    # Python would store the output's own name there, and Unity then silently imports nothing.
    with open(output, "wb") as file,             gzip.GzipFile(filename="archtemp.tar", mode="wb", fileobj=file, mtime=NOW) as gz,             tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as tar:
        add_directory(tar, folder_guid)
        add_bytes(tar, f"{folder_guid}/asset.meta", FOLDER_META.format(guid=folder_guid).encode("utf-8"))
        add_bytes(tar, f"{folder_guid}/pathname", install_path.encode("utf-8"))
        for guid, path, relative, meta_bytes in entries:
            add_directory(tar, guid)
            if os.path.isfile(path):
                with open(path, "rb") as f:
                    add_bytes(tar, f"{guid}/asset", f.read())
            add_bytes(tar, f"{guid}/asset.meta", meta_bytes)
            add_bytes(tar, f"{guid}/pathname", f"{install_path}/{relative}".encode("utf-8"))

    print(f"{output}: {install_path}/ and {len(entries)} assets in it")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        sys.exit(__doc__)
    main(*sys.argv[1:])
