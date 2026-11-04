#!/usr/bin/env bash
# Fails (exit 1) and lists files with CRLF line endings: they break `sh` scripts in containers
# ("set: illegal option -"). Checks every sh/yml/yaml/sql/json file under infra/ and all *.sh under services/.
# Fix with: sed -i 's/\r$//' <file>
root="$(cd "$(dirname "$0")/../.." && pwd)"
py=; for c in python python3; do "$c" -c "" >/dev/null 2>&1 && { py=$c; break; }; done; [ -n "$py" ] || { echo "python not found"; exit 2; }
"$py" -I - "$root" <<'PY'
import os, sys
root = sys.argv[1]
skip = {"node_modules", "bin", "obj", ".venv", ".git", "__pycache__"}
bad = []
for top, exts in (("infra", (".sh", ".yml", ".yaml", ".sql", ".json")), ("services", (".sh",))):
    for d, dirs, files in os.walk(os.path.join(root, top)):
        dirs[:] = [x for x in dirs if x not in skip]
        for f in files:
            if f.endswith(exts):
                p = os.path.join(d, f)
                if b"\r\n" in open(p, "rb").read():
                    bad.append(os.path.relpath(p, root))
if bad:
    print("CRLF line endings found:")
    print("\n".join(sorted(bad)))
    sys.exit(1)
print("line endings OK")
PY
