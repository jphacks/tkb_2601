#!/usr/bin/env python3
"""Import the locally downloaded BOOTH Nekobot.zip into the Unity project.

Supports both Nekobot.zip (containing Nekobot.unitypackage) and the package itself.
Preserves GUID metadata, never downloads models, extracts only known asset paths.
"""
import argparse
import io
import sys
import tarfile
import zipfile
from pathlib import Path

ROOT = "Assets/Nekobot"
ALLOWED = {ROOT + "/" + filename for filename in
           ("nekobot.fbx", "nekobot.prefab", "tex.png", "base.mat", "blue.mat")}


def read_package(path):
    if not path.is_file():
        raise ValueError("Archive not found: " + str(path))
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as z:
            names = [n for n in z.namelist() if n.lower().endswith(".unitypackage")]
            if len(names) != 1:
                raise ValueError("Expected one Nekobot.unitypackage in ZIP")
            return z.read(names[0])
    if path.suffix.lower() != ".unitypackage":
        raise ValueError("Expected Nekobot.zip or Nekobot.unitypackage")
    return path.read_bytes()


def unpack(package):
    groups = {}
    with tarfile.open(fileobj=io.BytesIO(package), mode="r:*") as tar:
        for entry in tar.getmembers():
            if not entry.isfile():
                continue
            parts = entry.name.split("/")
            if len(parts) != 2 or parts[1] not in ("pathname", "asset", "asset.meta"):
                continue
            groups.setdefault(parts[0], {})[parts[1]] = tar.extractfile(entry).read()

    files = {}
    for group in groups.values():
        raw_name = group.get("pathname")
        if not raw_name:
            continue
        name = raw_name.decode("utf-8").strip().replace("\\", "/")
        if name == ROOT:
            if "asset.meta" in group:
                files[ROOT + ".meta"] = group["asset.meta"]
        elif name in ALLOWED:
            if "asset" not in group or "asset.meta" not in group:
                raise ValueError("Missing asset/meta for " + name)
            files[name] = group["asset"]
            files[name + ".meta"] = group["asset.meta"]

    for filename in ("nekobot.fbx", "tex.png"):
        if ROOT + "/" + filename not in files:
            raise ValueError("Expected model file missing: " + filename)
    return files


def install(project, files, overwrite=False):
    version = project / "ProjectSettings/ProjectVersion.txt"
    if not version.exists() or "m_EditorVersion: 6000.3.12f1" not in version.read_text():
        raise ValueError("Unity project MUST use version 6000.3.12f1: " + str(project))
    updates = []
    for name, data in sorted(files.items()):
        path = project / name
        if path.is_file() and path.read_bytes() == data:
            continue
        if name == ROOT + ".meta" and path.exists():
            # Unity may already have created a folder GUID for the tracked README.
            continue
        if path.exists() and not overwrite:
            raise ValueError("Refusing to overwrite edited local asset: " + str(path) +
                             " (pass --overwrite to replace)")
        updates.append((path, data))
    for path, data in updates:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        print("[Nekobot] Imported", path.relative_to(project))
    print("[Nekobot] Local files ready:", len(files), "changed:", len(updates))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()
    try:
        install(args.project.expanduser().resolve(),
                unpack(read_package(args.archive.expanduser().resolve())),
                overwrite=args.overwrite)
    except (OSError, ValueError, tarfile.TarError, zipfile.BadZipFile) as exc:
        print("[Nekobot] ERROR:", exc, file=sys.stderr)
        sys.exit(1)
