package tailnet

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"tailscale.com/ipn"
)

const (
	stateFile    = "state.bin"
	lockFile     = "state.lock"
	stateKeySize = 32
	// stateMagic starts state.bin, and is authenticated with it: a file of another format or version is never read as one.
	stateMagic = "MIRA-TAILNET-1\n"
)

// store is the node's state (its private keys, its profile, its preferences), kept in one file encrypted with AES-256-GCM
// under the app's key: what Tailscale's own file store keeps in clear.
type store struct {
	path string
	aead cipher.AEAD
	logf func(string, ...any)

	mu    sync.Mutex
	state map[string][]byte
}

// openStore reads the state, or starts an empty one when there is none or it can't be read with this key any more (the
// keychain was reset): the unreadable file is kept aside, and the node simply logs in again.
func openStore(path string, key []byte, logf func(string, ...any)) (*store, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	s := &store{path: path, aead: aead, logf: logf, state: map[string][]byte{}}
	data, err := os.ReadFile(path)
	switch {
	case errors.Is(err, os.ErrNotExist):
		return s, nil
	case err != nil:
		return nil, err
	}
	if s.state, err = s.decrypt(data); err != nil {
		aside := fmt.Sprintf("%s.bad-%s", path, time.Now().UTC().Format("20060102-150405"))
		if err := os.Rename(path, aside); err != nil {
			return nil, err
		}
		logf("tailnet: the saved state can't be read (%v): kept as %s, starting over", err, filepath.Base(aside))
		s.state = map[string][]byte{}
	}
	return s, nil
}

func (s *store) String() string { return "mira-tailnet-store" }

func (s *store) ReadState(id ipn.StateKey) ([]byte, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	bs, ok := s.state[string(id)]
	if !ok {
		return nil, ipn.ErrStateNotExist
	}
	return bytes.Clone(bs), nil
}

func (s *store) WriteState(id ipn.StateKey, bs []byte) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	next := make(map[string][]byte, len(s.state)+1)
	for k, v := range s.state {
		next[k] = v
	}
	if bs == nil {
		delete(next, string(id))
	} else {
		next[string(id)] = bytes.Clone(bs)
	}
	data, err := s.encrypt(next)
	if err != nil {
		return err
	}
	if err := writeFileAtomic(s.path, data); err != nil {
		return err
	}
	s.state = next
	return nil
}

func (s *store) encrypt(state map[string][]byte) ([]byte, error) {
	plain, err := json.Marshal(state)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, s.aead.NonceSize())
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	out := append([]byte(stateMagic), nonce...)
	return s.aead.Seal(out, nonce, plain, []byte(stateMagic)), nil
}

func (s *store) decrypt(data []byte) (map[string][]byte, error) {
	if !bytes.HasPrefix(data, []byte(stateMagic)) || len(data) < len(stateMagic)+s.aead.NonceSize()+s.aead.Overhead() {
		return nil, errors.New("not a state file of this version")
	}
	rest := data[len(stateMagic):]
	nonce, sealed := rest[:s.aead.NonceSize()], rest[s.aead.NonceSize():]
	plain, err := s.aead.Open(nil, nonce, sealed, []byte(stateMagic))
	if err != nil {
		return nil, errors.New("another key, or a damaged file")
	}
	var state map[string][]byte
	if err := json.Unmarshal(plain, &state); err != nil {
		return nil, err
	}
	if state == nil {
		state = map[string][]byte{}
	}
	return state, nil
}

// writeFileAtomic replaces path with data in one rename, after the data reached the disk: a crash leaves the old state or
// the new one, never half of one.
func writeFileAtomic(path string, data []byte) error {
	tmp, err := os.CreateTemp(filepath.Dir(path), filepath.Base(path)+".*.tmp")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if err := tmp.Chmod(0o600); err != nil && !errors.Is(err, errors.ErrUnsupported) {
		tmp.Close()
		return err
	}
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(tmp.Name(), path)
}
