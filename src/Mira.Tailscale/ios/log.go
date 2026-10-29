package main

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"miracinema.app/tailscale/tailnet"
)

const (
	logName = "mira-tailscale.log"
	// logLimit is the size the log grows to before it starts over (the previous one kept as .1).
	logLimit = 512 << 10
)

// fileLog keeps the node's diagnostics in a file of its folder, on this iPhone only, login pages hidden: nothing of it is
// ever sent anywhere. It never blocks the node: when the disk lags, lines are dropped and counted.
type fileLog struct {
	dir     string
	lines   chan string
	dropped atomic.Int64
	done    chan struct{}

	mu     sync.RWMutex
	closed bool
}

func openLog(dir string) *fileLog {
	l := &fileLog{dir: dir, lines: make(chan string, 2048), done: make(chan struct{})}
	go l.run()
	return l
}

func (l *fileLog) logf(format string, args ...any) {
	if l == nil {
		return
	}
	line := time.Now().UTC().Format("2006-01-02T15:04:05.000Z ") + strings.TrimRight(tailnet.Redact(fmt.Sprintf(format, args...)), "\n") + "\n"
	l.mu.RLock()
	defer l.mu.RUnlock()
	if l.closed {
		return
	}
	select {
	case l.lines <- line:
	default:
		l.dropped.Add(1)
	}
}

func (l *fileLog) close() {
	if l == nil {
		return
	}
	l.mu.Lock()
	if !l.closed {
		l.closed = true
		close(l.lines)
	}
	l.mu.Unlock()
	<-l.done
}

func (l *fileLog) run() {
	defer close(l.done)
	path := filepath.Join(l.dir, logName)
	var f *os.File
	var w *bufio.Writer
	var size int64
	var retry time.Time
	open := func() {
		// The folder may not be there yet: the node makes it when it starts, after its log.
		os.MkdirAll(l.dir, 0o700)
		var err error
		if f, err = os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0o600); err != nil {
			f, w = nil, nil
			retry = time.Now().Add(5 * time.Second)
			return
		}
		w = bufio.NewWriter(f)
		if info, err := f.Stat(); err == nil {
			size = info.Size()
		}
	}
	open()
	for line := range l.lines {
		if f == nil && time.Now().After(retry) {
			open()
		}
		if f == nil {
			l.dropped.Add(1)
			continue
		}
		if n := l.dropped.Swap(0); n > 0 {
			dropped := fmt.Sprintf("%s mira-tailscale: %d lines dropped\n", time.Now().UTC().Format("2006-01-02T15:04:05.000Z"), n)
			w.WriteString(dropped)
			size += int64(len(dropped))
		}
		w.WriteString(line)
		size += int64(len(line))
		if len(l.lines) == 0 {
			w.Flush()
		}
		if size > logLimit {
			w.Flush()
			f.Close()
			os.Rename(path, path+".1")
			open()
		}
	}
	if f != nil {
		w.Flush()
		f.Close()
	}
}
