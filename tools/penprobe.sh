#!/bin/sh
# Ground-truth probe: read the pen digitizer alongside a running xochitl while
# holding a wakelock (so the rMPP's autosleep=mem doesn't suspend the digitizer).
# Does NOT pause xochitl and does NOT grab the device.
# Run it, then draw continuously on the rMPP for the whole window.
N="${1:-10}"
echo "xochitl pid before: $(pidof xochitl)"
echo "inkbridge_probe" > /sys/power/wake_lock   # keep the SoC/digitizer awake
echo "wake_lock now: $(cat /sys/power/wake_lock)"
evtest /dev/input/event2 >/tmp/evt.log 2>&1 &
EP=$!
sleep "$N"
kill "$EP" 2>/dev/null
echo "inkbridge_probe" > /sys/power/wake_unlock # release
syn=$(grep -c SYN_REPORT /tmp/evt.log)
absx=$(grep -c 'ABS_X' /tmp/evt.log)
echo "captured over ${N}s: SYN_REPORT=$syn  ABS_X_lines=$absx"
echo "xochitl pid after:  $(pidof xochitl)"
