#!/bin/sh
# Install the inkbridge daemon as a persistent systemd service on the reMarkable Paper Pro.
#
# The rMPP rootfs is read-only and /etc is a tmpfs overlay, so a unit written to /etc the
# normal way is lost on reboot. We use the same approach as xovi-tripletap: remount the
# rootfs rw and drop the /etc overlay so the unit lands on the PERSISTENT rootfs /etc.
# This survives reboots but NOT reMarkable software updates — re-run this after an update.
set -e
DIR=/home/root/inkbridge
UNIT=inkbridge-daemon.service

# Stop any manually-launched daemon so the service can bind :9292.
for p in $(pidof inkbridge-daemon 2>/dev/null); do kill "$p" 2>/dev/null || true; done
sleep 1

# rMPP (Ferrari) / rM-family: expose the persistent rootfs /etc.
if grep -qE "reMarkable (Ferrari|Chiappa)" /proc/device-tree/model 2>/dev/null; then
    echo "rMPP detected: remounting / rw and dropping the /etc overlay..."
    mount -o remount,rw /
    umount -R /etc || true
fi

echo "Installing $UNIT ..."
cp "$DIR/$UNIT" /etc/systemd/system/
systemctl daemon-reload
systemctl enable inkbridge-daemon --now

# Report whether /etc is now the persistent rootfs (overlay dropped) or still tmpfs.
# (busybox has no findmnt — read /proc/mounts directly.)
if grep -q " /etc overlay " /proc/mounts; then
    echo "WARNING: /etc is still an overlay (umount failed) — unit may NOT persist across reboot."
else
    echo "OK: /etc overlay dropped — unit is on the persistent rootfs and will survive reboot."
fi
echo "Status:"; systemctl --no-pager status inkbridge-daemon | head -n 6
echo "NOTE: re-run this script after a reMarkable software update."
