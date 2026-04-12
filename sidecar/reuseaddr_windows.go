package main

import (
	"syscall"

	"golang.org/x/sys/windows"
)

func setReuseAddr(network, address string, c syscall.RawConn) error {
	return c.Control(func(fd uintptr) {
		windows.SetsockoptInt(windows.Handle(fd), windows.SOL_SOCKET, windows.SO_REUSEADDR, 1)
	})
}
