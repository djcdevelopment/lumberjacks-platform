#!/usr/bin/env python3
"""Strictly inspect Comfy.Transport.Contracts before and after publication."""

from __future__ import annotations

import argparse
import re
import sys
import zipfile
from pathlib import Path, PurePosixPath
from xml.etree import ElementTree


PACKAGE_ID = "Comfy.Transport.Contracts"
REPOSITORY = "https://github.com/djcdevelopment/lumberjacks-platform"
SOURCE_ENTRIES = {
    "contentFiles/cs/any/Valheim/ValheimRoutedRpcAdmissions.cs": Path(
        "Valheim/ValheimRoutedRpcAdmissions.cs"
    ),
    "contentFiles/cs/any/Policies/VehicleSnapshotRelevanceSet.cs": Path(
        "Policies/VehicleSnapshotRelevanceSet.cs"
    ),
    "contentFiles/cs/any/Policies/ZdoBandPolicy.cs": Path(
        "Policies/ZdoBandPolicy.cs"
    ),
    "contentFiles/cs/any/Policies/ZdoFanoutPolicy.cs": Path(
        "Policies/ZdoFanoutPolicy.cs"
    ),
    "contentFiles/cs/any/Policies/ZdoIntegrationContract.cs": Path(
        "Policies/ZdoIntegrationContract.cs"
    ),
}


class PackageError(RuntimeError):
    pass


def local_name(element: ElementTree.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def descendants(element: ElementTree.Element, name: str) -> list[ElementTree.Element]:
    return [item for item in element.iter() if local_name(item) == name]


def descendant(element: ElementTree.Element, name: str) -> ElementTree.Element:
    found = descendants(element, name)
    if len(found) != 1:
        raise PackageError(f"expected exactly one {name} element, found {len(found)}")
    return found[0]


def text_of(element: ElementTree.Element, name: str) -> str:
    return (descendant(element, name).text or "").strip()


def safe_entries(archive: zipfile.ZipFile) -> list[str]:
    names = [entry.filename for entry in archive.infolist()]
    if len(names) != len(set(names)):
        raise PackageError("package contains duplicate ZIP entries")
    for name in names:
        path = PurePosixPath(name)
        if (
            not name
            or name.startswith("/")
            or "\\" in name
            or path.is_absolute()
            or ".." in path.parts
        ):
            raise PackageError(f"unsafe package entry: {name!r}")
    return names


def validate_payload(names: set[str], allow_signature: bool) -> None:
    required = {
        "[Content_Types].xml",
        "_rels/.rels",
        f"{PACKAGE_ID}.nuspec",
        "PACKAGE-README.md",
        "lib/netstandard2.0/Comfy.Transport.Contracts.dll",
        *SOURCE_ENTRIES,
    }
    missing = required - names
    if missing:
        raise PackageError("package payload is missing: " + ", ".join(sorted(missing)))

    allowed = set(required)
    core_properties = {
        name
        for name in names
        if re.fullmatch(
            r"package/services/metadata/core-properties/[0-9a-f]{32}\.psmdcp",
            name,
        )
    }
    if len(core_properties) != 1:
        raise PackageError(
            "package must contain exactly one generated core-properties entry"
        )
    allowed.update(core_properties)
    if allow_signature and ".signature.p7s" in names:
        allowed.add(".signature.p7s")
    unexpected = names - allowed
    if unexpected:
        raise PackageError(
            "package payload contains unexpected entries: "
            + ", ".join(sorted(unexpected))
        )


def validate_content_files(metadata: ElementTree.Element) -> None:
    content_files = descendant(metadata, "contentFiles")
    declarations = {
        item.attrib.get("include", ""): item.attrib.get("buildAction", "")
        for item in content_files
        if local_name(item) == "files"
    }
    expected = {
        name.removeprefix("contentFiles/"): "Compile" for name in SOURCE_ENTRIES
    }
    if declarations != expected:
        raise PackageError(
            "contentFiles declarations drifted: " + repr(sorted(declarations.items()))
        )


def validate_package(
    package: Path,
    version: str,
    expected_commit: str,
    source_root: Path,
    allow_signature: bool = False,
) -> dict[str, str | int]:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", version):
        raise PackageError(f"invalid expected package version: {version!r}")
    if not re.fullmatch(r"[0-9a-fA-F]{40}", expected_commit):
        raise PackageError("expected repository commit must be a full 40-hex SHA")
    expected_filename = f"{PACKAGE_ID}.{version}.nupkg"
    if package.name.lower() != expected_filename.lower():
        raise PackageError(
            f"package filename is {package.name!r}, expected {expected_filename!r}"
        )

    with zipfile.ZipFile(package) as archive:
        names = set(safe_entries(archive))
        validate_payload(names, allow_signature)
        root = ElementTree.fromstring(archive.read(f"{PACKAGE_ID}.nuspec"))

        dll = archive.read("lib/netstandard2.0/Comfy.Transport.Contracts.dll")
        if len(dll) < 512 or not dll.startswith(b"MZ"):
            raise PackageError("compiled payload is not a plausible PE assembly")
        for entry, relative_source in SOURCE_ENTRIES.items():
            source_path = source_root / relative_source
            if not source_path.is_file():
                raise PackageError(f"source payload input is missing: {source_path}")
            if archive.read(entry) != source_path.read_bytes():
                raise PackageError(f"package source bytes differ from {relative_source}")
        if archive.read("PACKAGE-README.md") != (source_root / "PACKAGE-README.md").read_bytes():
            raise PackageError("package readme bytes differ from project source")

    metadata = descendant(root, "metadata")
    actual_id = text_of(metadata, "id")
    actual_version = text_of(metadata, "version")
    if actual_id != PACKAGE_ID:
        raise PackageError(f"package id is {actual_id!r}, expected {PACKAGE_ID!r}")
    if actual_version != version:
        raise PackageError(
            f"package version is {actual_version!r}, expected {version!r}"
        )
    if text_of(metadata, "license") != "BUSL-1.1":
        raise PackageError("package license expression must be BUSL-1.1")
    if text_of(metadata, "readme") != "PACKAGE-README.md":
        raise PackageError("package readme must be PACKAGE-README.md")
    if text_of(metadata, "title") != "Comfy Transport Contracts":
        raise PackageError("package title drifted")
    if not text_of(metadata, "description"):
        raise PackageError("package description is empty")

    repository = descendant(metadata, "repository")
    repository_url = repository.attrib.get("url", "").rstrip("/")
    repository_commit = repository.attrib.get("commit", "")
    if repository.attrib.get("type") != "git":
        raise PackageError("package repository type must be git")
    if repository_url != REPOSITORY:
        raise PackageError(
            f"package repository URL is {repository_url!r}, expected {REPOSITORY!r}"
        )
    if repository_commit.lower() != expected_commit.lower():
        raise PackageError(
            f"package repository commit is {repository_commit!r}, "
            f"expected {expected_commit!r}"
        )

    dependencies = descendant(metadata, "dependencies")
    dependency_rows = descendants(dependencies, "dependency")
    if dependency_rows:
        raise PackageError("Transport contracts package must not carry dependencies")
    validate_content_files(metadata)

    return {
        "id": actual_id,
        "version": actual_version,
        "commit": repository_commit.lower(),
        "entries": len(names),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--allow-repository-signature", action="store_true")
    args = parser.parse_args()
    try:
        result = validate_package(
            args.package,
            args.version,
            args.commit,
            args.source_root,
            args.allow_repository_signature,
        )
    except (PackageError, OSError, zipfile.BadZipFile, ElementTree.ParseError) as exc:
        print(f"INVALID: {exc}", file=sys.stderr)
        return 1
    print("VERIFIED " + " ".join(f"{key}={value}" for key, value in result.items()))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
