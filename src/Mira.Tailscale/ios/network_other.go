//go:build !darwin

package main

// setDefaultInterface does nothing off Apple's systems: the library's tests run there.
func setDefaultInterface(string) {}
