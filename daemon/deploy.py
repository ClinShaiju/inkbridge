#!/usr/bin/env python3
"""Upload the cross-compiled daemon binary to the rMPP and restart the service.

The service (inkbridge-daemon) runs /home/root/inkbridge/inkbridge-daemon and is already installed
via install-service.sh. This just refreshes the binary + restarts. Requires paramiko.
"""
import os
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
BIN = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "target", "aarch64-unknown-linux-musl", "release", "inkbridge-daemon",
)
REMOTE = "/home/root/inkbridge/inkbridge-daemon"


def main():
    if not os.path.exists(BIN):
        print("binary not found — build first:", BIN, file=sys.stderr)
        sys.exit(1)
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    ssh.connect(HOST, username=USER, password=PW)

    def run(cmd):
        _, out, err = ssh.exec_command(cmd)
        code = out.channel.recv_exit_status()
        o, e = out.read().decode(errors="replace"), err.read().decode(errors="replace")
        if o.strip():
            print(o.strip())
        if e.strip():
            print("ERR:", e.strip())
        return code

    sftp = ssh.open_sftp()
    try:
        sftp.mkdir("/home/root/inkbridge")
    except IOError:
        pass
    # Upload to a temp name then move into place (can't overwrite a running binary in place).
    tmp = REMOTE + ".new"
    sftp.put(BIN, tmp)
    print("uploaded:", tmp, f"({os.path.getsize(BIN)} bytes)")
    sftp.close()

    run(f"chmod +x {tmp} && mv {tmp} {REMOTE}")
    run("systemctl restart inkbridge-daemon")
    print("restarted inkbridge-daemon")
    run("sleep 1; systemctl --no-pager status inkbridge-daemon | head -n 4")
    print("--- listening ports (expect 9292 pen + 9293 control) ---")
    run("netstat -ltn 2>/dev/null | grep -E ':929[23]' || ss -ltn 2>/dev/null | grep -E ':929[23]'")
    ssh.close()


if __name__ == "__main__":
    main()
