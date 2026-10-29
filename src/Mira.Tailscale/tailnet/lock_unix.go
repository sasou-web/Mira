//go:build unix

package tailnet

import (
	"errors"
	"os"

	"golang.org/x/sys/unix"
)

// dirLock keeps a second node off the same state: two would fight over its keys.
type dirLock struct{ f *os.File }

func lockDir(path string) (*dirLock, error) {
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE, 0o600)
	if err != nil {
		return nil, err
	}
	if err := unix.Flock(int(f.Fd()), unix.LOCK_EX|unix.LOCK_NB); err != nil {
		f.Close()
		if errors.Is(err, unix.EWOULDBLOCK) {
			return nil, newError(CodeLocked, err)
		}
		return nil, err
	}
	return &dirLock{f}, nil
}

func (l *dirLock) release() {
	unix.Flock(int(l.f.Fd()), unix.LOCK_UN)
	l.f.Close()
}
