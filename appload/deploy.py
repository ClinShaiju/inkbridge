#!/usr/bin/env python3
"""
Deploy the inkbridge AppLoad visualizer to the rMPP.

Uploads this appload/ tree to /home/root/xovi/exthome/appload/inkbridge/, makes backend/entry
executable, and restarts xochitl so AppLoad re-scans. Raw QML (no resources.rcc) — fine for a
small app. Requires paramiko (pip install paramiko).
"""

import os
import subprocess
import sys

import paramiko


def _load_env():
    """Load KEY=VALUE pairs from a .env file at the repo root (never committed)."""
    here = os.path.dirname(os.path.abspath(__file__))
    for d in (here, os.path.dirname(here)):
        path = os.path.join(d, ".env")
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line or line.startswith("#") or "=" not in line:
                    continue
                key, val = line.split("=", 1)
                os.environ.setdefault(key.strip(), val.strip().strip('"').strip("'"))
        break


_load_env()
HOST = os.environ.get("INKBRIDGE_HOST", "10.11.99.1")
USER = os.environ.get("INKBRIDGE_USER", "root")
PW = os.environ.get("INKBRIDGE_PW")
if not PW:
    sys.exit("INKBRIDGE_PW not set — copy .env.example to .env and set the rMPP root password "
             "(reMarkable: Settings > Help > Copyrights and licenses, bottom of the page).")
LOCAL_DIR = os.path.dirname(os.path.abspath(__file__))
REMOTE_DIR = "/home/root/xovi/exthome/appload/inkbridge"

# AppLoad loads the frontend from a compiled resources.rcc (qrc:/<hash>/frontend/Main.qml), NOT
# from raw .qml files. Ship the rcc + manifest + the (executable) backend + the seed area.json.
SKIP = {"deploy.py", "__pycache__", ".gitignore", "application.qrc"}
UPLOAD = ["manifest.json", "resources.rcc", "area.json", "icon.png", "backend"]


def compile_rcc():
    """Compile application.qrc -> resources.rcc (Qt binary format) with PySide6's rcc."""
    import PySide6
    rcc = os.path.join(os.path.dirname(PySide6.__file__), "rcc.exe")
    qrc = os.path.join(LOCAL_DIR, "application.qrc")
    out = os.path.join(LOCAL_DIR, "resources.rcc")
    r = subprocess.run([rcc, "-binary", qrc, "-o", out], capture_output=True, text=True)
    if r.returncode != 0:
        print("rcc FAILED:", r.stderr, file=sys.stderr)
        sys.exit(1)
    with open(out, "rb") as f:
        if f.read(4) != b"qres":
            print("ERROR: resources.rcc is not Qt binary format", file=sys.stderr)
            sys.exit(1)
    print(f"compiled resources.rcc ({os.path.getsize(out)} bytes)")


def main():
    compile_rcc()
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(HOST, username=USER, password=PW)
    sftp = ssh.open_sftp()

    def mkdir(path):
        try:
            sftp.mkdir(path)
        except IOError:
            pass

    def upload_tree(local, remote):
        mkdir(remote)
        for name in sorted(os.listdir(local)):
            if name in SKIP:
                continue
            lpath = os.path.join(local, name)
            rpath = remote + "/" + name
            if os.path.isdir(lpath):
                upload_tree(lpath, rpath)
            else:
                sftp.put(lpath, rpath)
                print("uploaded:", rpath)

    mkdir(REMOTE_DIR)
    for item in UPLOAD:
        lpath = os.path.join(LOCAL_DIR, item)
        if not os.path.exists(lpath):
            continue
        if os.path.isdir(lpath):
            upload_tree(lpath, REMOTE_DIR + "/" + item)
        else:
            sftp.put(lpath, REMOTE_DIR + "/" + item)
            print("uploaded:", REMOTE_DIR + "/" + item)

    def run(cmd):
        _, out, err = ssh.exec_command(cmd)
        o, e = out.read().decode(errors="replace"), err.read().decode(errors="replace")
        if o.strip():
            print(o.strip())
        if e.strip():
            print("ERR:", e.strip())

    run(f"chmod +x {REMOTE_DIR}/backend/entry")
    print("restarting xochitl to reload AppLoad ...")
    ssh.exec_command("nohup systemctl restart xochitl > /dev/null 2>&1 &")
    print("done — open AppLoad on the rMPP to launch 'inkbridge'.")

    sftp.close()
    ssh.close()


if __name__ == "__main__":
    main()
