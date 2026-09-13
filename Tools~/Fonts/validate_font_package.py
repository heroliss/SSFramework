"""Inspect a generated Unity font archive without extracting or executing its contents."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import tarfile


def validate(archive: Path) -> dict:
    source = json.loads(Path(__file__).with_name("source.json").read_text(encoding="utf-8"))
    prefix = "Assets/ThirdParty/SSFrameworkFonts/zh-CN/"
    expected = {
        source["fontFile"], "OFL.txt", "NOTICE.txt", "README.txt",
        "NotoSansSC-Regular TMP.asset", "NotoSansSC-Regular TextCore.asset",
    }
    assets = {}
    with tarfile.open(archive, "r:gz") as package:
        members = package.getmembers()
        if len(members) > 100 or sum(m.size for m in members) > 32 * 1024 * 1024:
            raise ValueError("Unexpected archive size or member count")
        by_name = {m.name: m for m in members}
        if len(by_name) != len(members):
            raise ValueError("Duplicate archive entries")
        for member in members:
            if not member.name.endswith("/pathname"):
                continue
            base = member.name.rsplit("/", 1)[0]
            if not re.fullmatch(r"[a-f0-9]{32}", base):
                raise ValueError("Unexpected asset GUID folder")
            path = package.extractfile(member).read().decode("utf-8")
            if not path.startswith(prefix) or path[len(prefix):] not in expected or path in assets:
                raise ValueError(f"Unexpected or duplicate asset: {path}")
            data = package.extractfile(by_name[base + "/asset"]).read()
            meta = package.extractfile(by_name[base + "/asset.meta"]).read().decode("utf-8")
            if f"guid: {base}" not in meta:
                raise ValueError(f"GUID mismatch: {path}")
            assets[path] = {"guid": base, "data": data, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    if set(assets) != {prefix + name for name in expected}:
        raise ValueError("The archive must contain exactly the six intended assets")
    font = assets[prefix + source["fontFile"]]
    if font["sha256"] != source["fontSha256"] or font["bytes"] != source["fontBytes"]:
        raise ValueError("Original OTF does not match the pinned source")
    if assets[prefix + "OFL.txt"]["sha256"] != source["licenseSha256"]:
        raise ValueError("License does not match the pinned source")
    for name in ("NotoSansSC-Regular TMP.asset", "NotoSansSC-Regular TextCore.asset"):
        text = assets[prefix + name]["data"].decode("utf-8")
        for marker in ("m_AtlasPopulationMode: 1", "m_GlyphTable: []", "m_CharacterTable: []", "m_ClearDynamicDataOnBuild: 1"):
            if marker not in text:
                raise ValueError(f"Unexpected font setting in {name}: {marker}")
        if not re.search(r"m_SourceFontFile: \{[^\r\n}]*guid: " + font["guid"], text):
            raise ValueError(f"Missing bundled source reference: {name}")
    return {
        "passed": True, "packageSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        "packageBytes": archive.stat().st_size,
        "entries": [{"path": path, **{k: v for k, v in value.items() if k != "data"}} for path, value in sorted(assets.items())],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = json.dumps(validate(args.archive), ensure_ascii=False, indent=2)
    if args.report:
        args.report.write_text(result + "\n", encoding="utf-8")
    print(result)
