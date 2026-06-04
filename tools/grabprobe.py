#!/opt/bin/python3
# Definitively test whether something (xochitl) holds an exclusive EVIOCGRAB on
# the pen. If we can grab it, nobody else holds it; if we get EBUSY, xochitl does.
import fcntl, os, errno

EVIOCGRAB = 0x40044590  # _IOW('E', 0x90, int)
path = "/dev/input/event2"
fd = os.open(path, os.O_RDONLY | os.O_NONBLOCK)
try:
    fcntl.ioctl(fd, EVIOCGRAB, 1)
    print("GRAB OK -> no other process holds the grab")
    fcntl.ioctl(fd, EVIOCGRAB, 0)
except OSError as e:
    if e.errno == errno.EBUSY:
        print("GRAB EBUSY -> another process (xochitl) holds an exclusive grab")
    else:
        print("GRAB FAILED errno=%d (%s)" % (e.errno, e.strerror))
finally:
    os.close(fd)
