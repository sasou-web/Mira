package tailnet

import (
	"errors"
	"os"

	"golang.org/x/sys/windows"
)

// dirLock keeps a second node off the same state: two would fight over its keys.
type dirLock struct{ f *os.File }

func lockDir(path string) (*dirLock, error) {
	f, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE, 0o600)
	if err != nil {
		return nil, err
	}
	err = windows.LockFileEx(windows.Handle(f.Fd()), windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, new(windows.Overlapped))
	if err != nil {
		f.Close()
		if errors.Is(err, windows.ERROR_LOCK_VIOLATION) {
			return nil, newError(CodeLocked, err)
		}
		return nil, err
	}
	return &dirLock{f}, nil
}

func (l *dirLock) release() {
	windows.UnlockFileEx(windows.Handle(l.f.Fd()), 0, 1, 0, new(windows.Overlapped))
	l.f.Close()
}
