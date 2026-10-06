#!/usr/bin/env python3
"""Compares two read-battery.sh outputs command by command, e.g. a CMS 12 site and its CMS 13 upgrade.

    compare-battery.py <before directory> <after directory> [--all]

Prints, per command both ran, the JSON paths whose values differ (lists compared item by item), so the differences
can be checked against the expected ones. Only the commands both directories have are compared; the doctor and
env outputs (paths, versions and the machine's state) are left out unless --all is given.
"""
import json
import os
import sys

SKIPPED = {"doctor", "env", "db-list"}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def diff(a, b, path, out):
    if isinstance(a, dict) and isinstance(b, dict):
        for key in sorted(set(a) | set(b)):
            if key not in a:
                out.append(f"{path}.{key}: only after = {short(b[key])}")
            elif key not in b:
                out.append(f"{path}.{key}: only before = {short(a[key])}")
            else:
                diff(a[key], b[key], f"{path}.{key}", out)
    elif isinstance(a, list) and isinstance(b, list):
        key = identity(a, b)
        if key:
            # Items matched by their ref, name or id, so one added item doesn't make every later one differ.
            before, after = {x[key]: x for x in a}, {y[key]: y for y in b}
            for k in [x[key] for x in a] + [y[key] for y in b if y[key] not in before]:
                if k not in after:
                    out.append(f"{path}[{key}={k}]: only before")
                elif k not in before:
                    out.append(f"{path}[{key}={k}]: only after")
                else:
                    diff(before[k], after[k], f"{path}[{key}={k}]", out)
            return
        if len(a) != len(b):
            out.append(f"{path}: {len(a)} items before, {len(b)} after")
        for i, (x, y) in enumerate(zip(a, b)):
            diff(x, y, f"{path}[{i}]", out)
    elif a != b:
        out.append(f"{path}: {short(a)} -> {short(b)}")


def identity(a, b):
    """The key that tells the items of two lists of objects apart, if one does: unique in both lists."""
    items = a + b
    if not items or not all(isinstance(x, dict) for x in items):
        return None
    for key in ("ref", "id", "name", "property", "code"):
        if all(key in x and isinstance(x[key], (str, int)) for x in items) \
                and len({x[key] for x in a}) == len(a) and len({y[key] for y in b}) == len(b):
            return key
    return None


def short(value):
    text = json.dumps(value, ensure_ascii=False)
    return text if len(text) <= 160 else text[:157] + "..."


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 2:
        print(__doc__)
        sys.exit(1)
    before, after = args
    every = "--all" in sys.argv
    labels = sorted(
        f[:-5] for f in os.listdir(before)
        if f.endswith(".json") and os.path.exists(os.path.join(after, f)) and (every or f[:-5] not in SKIPPED))
    same = 0
    for label in labels:
        out = []
        a, b = load(os.path.join(before, label + ".json")), load(os.path.join(after, label + ".json"))
        for envelope in (a, b):
            envelope.get("meta", {}).pop("version", None)
        diff(a, b, "", out)
        if out:
            print(f"== {label} ({len(out)})")
            for line in out:
                print("  " + line)
        else:
            same += 1
    only = sorted({f[:-5] for f in os.listdir(after) if f.endswith(".json")} - {f[:-5] for f in os.listdir(before) if f.endswith(".json")})
    print(f"{same} of {len(labels)} commands identical" + (f"; only after: {', '.join(only)}" if only else ""))


if __name__ == "__main__":
    main()
