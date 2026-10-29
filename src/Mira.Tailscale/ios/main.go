// Command ios is Built-in Tailscale for Mira's iPhone app: the node of package tailnet and its relay, built by
// tools/ios/tailscale.sh as a static library (c-archive) in MiraTailscale.xcframework. Each function answers in JSON, a
// string the app frees with MiraTailscaleFree; include/MiraTailscale/MiraTailscale.h declares them for Swift.
package main

/*
#include <stdlib.h>
*/
import "C"

import "unsafe"

// MiraTailscaleStart starts the node and its relay from the app's configuration (JSON); its status.
//
//export MiraTailscaleStart
func MiraTailscaleStart(config *C.char) *C.char {
	return C.CString(encode(shared.start(C.GoString(config))))
}

// MiraTailscaleStatus is the node's status: its state, its tailnet, its devices, the relay's base.
//
//export MiraTailscaleStatus
func MiraTailscaleStatus() *C.char {
	return C.CString(encode(shared.status()))
}

// MiraTailscaleCheck takes a fresh look at the network and listens again if iOS took the relay's socket; its status.
//
//export MiraTailscaleCheck
func MiraTailscaleCheck() *C.char {
	return C.CString(encode(shared.check()))
}

// MiraTailscaleNetwork is told the interface iOS reaches the Internet with, at each change.
//
//export MiraTailscaleNetwork
func MiraTailscaleNetwork(name *C.char) {
	shared.network(C.GoString(name))
}

// MiraTailscaleLogin gives the page that signs the node in ({"url"} or {"error"}).
//
//export MiraTailscaleLogin
func MiraTailscaleLogin() *C.char {
	return C.CString(encode(shared.login()))
}

// MiraTailscaleLogout signs the node out of its tailnet ({} or {"error"}).
//
//export MiraTailscaleLogout
func MiraTailscaleLogout() *C.char {
	return C.CString(encode(shared.logout()))
}

// MiraTailscaleStop stops the relay and the node, keeping its state; its status (off).
//
//export MiraTailscaleStop
func MiraTailscaleStop() *C.char {
	return C.CString(encode(shared.stop()))
}

// MiraTailscaleFree frees a string one of these functions returned.
//
//export MiraTailscaleFree
func MiraTailscaleFree(text *C.char) {
	C.free(unsafe.Pointer(text))
}
