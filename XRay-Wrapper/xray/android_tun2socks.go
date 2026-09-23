//go:build android

package xray

/*
#include <stdbool.h>
#include <stdlib.h>

typedef int (*android_protect_fn)(int);
static android_protect_fn android_protect_cb;
static void android_set_protect(android_protect_fn f) { android_protect_cb = f; }
static int android_call_protect(int fd) {
	if (android_protect_cb == 0) return 0;
	return android_protect_cb(fd);
}
*/
import "C"

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	tcore "github.com/eycorsican/go-tun2socks/core"
)

const androidTunReadBufferSize = 64 * 1024

var androidTunMutex sync.Mutex
var androidTunState *androidTunBridge
var androidTunLastError string
var androidTunRunning atomic.Bool

type androidTunBridge struct {
	tunFd int
	lwip  tcore.LWIPStack
	done  chan struct{}
	stop  chan struct{}
	hasPi bool
}

//export StartAndroidTun2Socks
func StartAndroidTun2Socks(fd C.int, proxyPort C.int, isUdpEnabled C.bool, username *C.char, password *C.char, limitMux C.bool) (errPtr *C.char) {
	defer func() {
		if recovered := recover(); recovered != nil {
			// The mutex is already held once this function has locked it.
			// stopAndroidTunLocked expects that and unlocks internally.
			message := fmt.Sprintf("android tun2socks panic: %v", recovered)
			stopAndroidTunLocked()
			androidTunLastError = message
			errPtr = C.CString(message)
		}
	}()

	androidTunMutex.Lock()
	defer androidTunMutex.Unlock()

	androidTunLastError = ""
	stopAndroidTunLocked()

	if fd <= 0 {
		message := "invalid Android TUN file descriptor"
		androidTunLastError = message
		return C.CString(message)
	}

	if proxyPort <= 0 {
		message := "invalid Android SOCKS proxy port"
		androidTunLastError = message
		return C.CString(message)
	}

	auth := newLocalSocksAuth(C.GoString(username), C.GoString(password))
	androidMuxLimited.Store(bool(limitMux))

	// os.File registers the fd with the Go poller, which keeps a dup.
	// On this phone that dup survives Close and leaves a DOWN tun with
	// 10.0.236.10 still installed. Raw syscalls close the only fd.
	tunFd := int(fd)
	_ = syscall.SetNonblock(tunFd, false)

	lwip := tcore.NewLWIPStack()
	bridge := &androidTunBridge{
		tunFd: tunFd,
		lwip:    lwip,
		done:    make(chan struct{}),
		stop:    make(chan struct{}),
	}

	tcore.RegisterTCPConnHandler(newAndroidTCPHandler("127.0.0.1", uint16(proxyPort), auth))
	if bool(isUdpEnabled) {
		tcore.RegisterUDPConnHandler(newAndroidUDPHandler("127.0.0.1", uint16(proxyPort), auth, 30*time.Second))
	} else {
		tcore.RegisterUDPConnHandler(newAndroidDnsOverTcpHandler("127.0.0.1", uint16(proxyPort), auth))
	}

	tcore.RegisterOutputFn(func(data []byte) (int, error) {
		return writeAndroidTunPacket(bridge, data)
	})

	androidTunState = bridge
	androidTunRunning.Store(true)
	go runAndroidTunLoop(bridge)
	return nil
}

//export StopAndroidTun2Socks
func StopAndroidTun2Socks() {
	androidTunMutex.Lock()
	defer androidTunMutex.Unlock()
	stopAndroidTunLocked()
}

//export IsAndroidTun2SocksRunning
func IsAndroidTun2SocksRunning() C.bool {
	return C.bool(androidTunRunning.Load())
}

//export GetAndroidTun2SocksLastError
func GetAndroidTun2SocksLastError() *C.char {
	androidTunMutex.Lock()
	defer androidTunMutex.Unlock()

	if androidTunLastError == "" {
		return nil
	}

	return C.CString(androidTunLastError)
}

func runAndroidTunLoop(bridge *androidTunBridge) {
	defer close(bridge.done)
	defer func() {
		androidTunMutex.Lock()
		if androidTunState == bridge {
			androidTunState = nil
		}
		androidTunMutex.Unlock()
	}()

	buffer := make([]byte, androidTunReadBufferSize)
	for {
		tunFd := bridge.tunFd
		if tunFd < 0 {
			return
		}

		packetLength, err := syscall.Read(tunFd, buffer)
		if err == syscall.EINTR {
			continue
		}
		if packetLength > 0 {
			if isBridgeStopping(bridge) {
				return
			}

			packet := stripTunPi(bridge, buffer[:packetLength])
			if tryAnswerTunDns(bridge, packet) {
				continue
			}
			if tryRejectTunUdp(bridge, packet) {
				continue
			}

			lwip := bridge.lwip
			if lwip == nil {
				return
			}

			if _, writeErr := lwip.Write(packet); writeErr != nil && !isIgnorableTunInputError(writeErr) {
				if isBridgeStopping(bridge) {
					return
				}

				setAndroidTunLastError(fmt.Sprintf("tun2socks packet handling failed: %v", writeErr))
				return
			}
		}

		if err != nil {
			if !isExpectedTunClose(err) {
				setAndroidTunLastError(fmt.Sprintf("tun2socks read failed: %v", err))
			}
			return
		}

		select {
		case <-bridge.stop:
			return
		default:
		}
	}
}

func writeAndroidTunPacket(bridge *androidTunBridge, data []byte) (int, error) {
	if !androidTunRunning.Load() {
		return 0, io.ErrClosedPipe
	}
	tunFd := bridge.tunFd
	if tunFd < 0 {
		return 0, io.ErrClosedPipe
	}

	if bridge.hasPi {
		framed := make([]byte, len(data)+4)
		framed[2] = 0x08
		copy(framed[4:], data)
		return syscall.Write(tunFd, framed)
	}
	return syscall.Write(tunFd, data)
}

func stripTunPi(bridge *androidTunBridge, packet []byte) []byte {
	if len(packet) >= 24 && packet[0] == 0 && packet[1] == 0 && packet[2] == 8 && packet[3] == 0 && packet[4]>>4 == 4 {
		bridge.hasPi = true
		return packet[4:]
	}
	if len(packet) >= 4 && packet[0]>>4 != 4 && packet[0]>>4 != 6 && packet[2] == 8 && packet[3] == 0 {
		bridge.hasPi = true
		return packet[4:]
	}
	return packet
}

func stopAndroidTunLocked() {
	bridge := androidTunState
	androidTunState = nil
	androidTunRunning.Store(false)
	androidMuxLimited.Store(false)

	if bridge == nil {
		return
	}

	select {
	case <-bridge.stop:
	default:
		close(bridge.stop)
	}

	tunFd := bridge.tunFd
	bridge.tunFd = -1
	lwip := bridge.lwip

	// Release the global bridge lock before closing the TUN fd and LWIP stack.
	// Close() may synchronously trigger callbacks that also need androidTunMutex.
	androidTunMutex.Unlock()
	defer androidTunMutex.Lock()

	// Close the TUN FD first so the packet reader unblocks before lwip teardown.
	if tunFd >= 0 {
		_ = syscall.Close(tunFd)
	}

	// Give the read loop a chance to exit cleanly before tearing down lwip.
	select {
	case <-bridge.done:
	case <-time.After(2 * time.Second):
	}

	if lwip != nil {
		_ = lwip.Close()
	}
}

func isExpectedTunClose(err error) bool {
	if err == nil {
		return true
	}

	if errors.Is(err, os.ErrClosed) || errors.Is(err, io.EOF) || errors.Is(err, io.ErrClosedPipe) {
		return true
	}

	message := err.Error()
	return strings.Contains(message, "file already closed") ||
		strings.Contains(message, "bad file descriptor") ||
		strings.Contains(message, "use of closed file") ||
		strings.Contains(message, "closed pipe")
}

func isIgnorableTunInputError(err error) bool {
	if err == nil {
		return false
	}

	return isExpectedTunClose(err) || strings.Contains(err.Error(), "packet not handled")
}

func setAndroidTunLastError(message string) {
	androidTunMutex.Lock()
	defer androidTunMutex.Unlock()
	androidTunLastError = message
}

func isBridgeStopping(bridge *androidTunBridge) bool {
	select {
	case <-bridge.stop:
		return true
	default:
		return false
	}
}

//export SetAndroidSocketProtect
func SetAndroidSocketProtect(cb C.android_protect_fn) {
	C.android_set_protect(cb)
}

func protectConn(conn net.Conn) {
	type syscallConner interface {
		SyscallConn() (syscall.RawConn, error)
	}
	sc, ok := conn.(syscallConner)
	if !ok {
		return
	}
	raw, err := sc.SyscallConn()
	if err != nil {
		return
	}
	_ = raw.Control(func(fd uintptr) {
		C.android_call_protect(C.int(fd))
	})
}

func tryAnswerTunDns(bridge *androidTunBridge, packet []byte) bool {
	query, src, dst, srcPort, ok := parseIPv4UdpDns(packet)
	if !ok {
		return false
	}

	pkt := append([]byte(nil), query...)
	srcCopy := append(net.IP(nil), src...)
	dstCopy := append(net.IP(nil), dst...)
	go func() {
		resp := lookupTunDnsCached(pkt)
		if len(resp) == 0 {
			var err error
			resp, err = queryUpstreamDns(pkt)
			if err != nil || len(resp) == 0 {
				return
			}
			storeTunDnsCache(pkt, resp)
		}
		reply := buildIPv4UdpPacket(dstCopy, srcCopy, 53, srcPort, resp)
		if len(reply) == 0 {
			return
		}
		_, _ = writeAndroidTunPacket(bridge, reply)
	}()
	return true
}

func tryRejectTunUdp(bridge *androidTunBridge, packet []byte) bool {
	if len(packet) < 28 || packet[0]>>4 != 4 {
		return false
	}
	ihl := int(packet[0]&0x0f) * 4
	if ihl < 20 || len(packet) < ihl+8 {
		return false
	}
	if packet[9] != 17 {
		return false
	}
	if packet[6]&0x1f != 0 || packet[7] != 0 {
		return false
	}
	dstPort := binary.BigEndian.Uint16(packet[ihl+2 : ihl+4])
	if dstPort == 53 {
		return false
	}
	reply := buildIcmpPortUnreachable(packet)
	if len(reply) > 0 {
		go func() { _, _ = writeAndroidTunPacket(bridge, reply) }()
	}
	return true
}

func buildIcmpPortUnreachable(orig []byte) []byte {
	ihl := int(orig[0]&0x0f) * 4
	if ihl < 20 || len(orig) < ihl+8 {
		return nil
	}
	payload := ihl + 8
	if payload > len(orig) {
		payload = len(orig)
	}
	if payload > 28+ihl {
		payload = 28 + ihl
	}
	total := 20 + 8 + payload
	pkt := make([]byte, total)
	pkt[0] = 0x45
	binary.BigEndian.PutUint16(pkt[2:4], uint16(total))
	pkt[8] = 64
	pkt[9] = 1
	copy(pkt[12:16], orig[16:20])
	copy(pkt[16:20], orig[12:16])
	binary.BigEndian.PutUint16(pkt[10:12], ipv4Checksum(pkt[:20]))
	pkt[20] = 3
	pkt[21] = 3
	copy(pkt[28:], orig[:payload])
	binary.BigEndian.PutUint16(pkt[22:24], icmpChecksum(pkt[20:]))
	return pkt
}

func icmpChecksum(b []byte) uint16 {
	var sum uint32
	for i := 0; i+1 < len(b); i += 2 {
		sum += uint32(binary.BigEndian.Uint16(b[i : i+2]))
	}
	if len(b)%2 == 1 {
		sum += uint32(b[len(b)-1]) << 8
	}
	for sum > 0xffff {
		sum = (sum >> 16) + (sum & 0xffff)
	}
	return ^uint16(sum)
}

func parseIPv4UdpDns(packet []byte) (query []byte, src, dst net.IP, srcPort uint16, ok bool) {
	if len(packet) < 28 || packet[0]>>4 != 4 {
		return nil, nil, nil, 0, false
	}
	ihl := int(packet[0]&0x0f) * 4
	if ihl < 20 || len(packet) < ihl+8 {
		return nil, nil, nil, 0, false
	}
	if packet[9] != 17 {
		return nil, nil, nil, 0, false
	}
	if packet[6]&0x1f != 0 || packet[7] != 0 {
		return nil, nil, nil, 0, false
	}
	udp := packet[ihl:]
	dstPort := binary.BigEndian.Uint16(udp[2:4])
	if dstPort != 53 {
		return nil, nil, nil, 0, false
	}
	srcPort = binary.BigEndian.Uint16(udp[0:2])
	src = net.IP(append([]byte(nil), packet[12:16]...))
	dst = net.IP(append([]byte(nil), packet[16:20]...))
	query = append([]byte(nil), udp[8:]...)
	if len(query) < 12 {
		return nil, nil, nil, 0, false
	}
	return query, src, dst, srcPort, true
}

type tunDnsCacheEntry struct {
	resp []byte
	exp  time.Time
}

var tunDnsCacheMu sync.Mutex
var tunDnsCache = map[string]tunDnsCacheEntry{}

func tunDnsCacheKey(query []byte) string {
	if len(query) < 12 {
		return ""
	}
	return string(query[2:])
}

func lookupTunDnsCached(query []byte) []byte {
	key := tunDnsCacheKey(query)
	if key == "" {
		return nil
	}
	tunDnsCacheMu.Lock()
	defer tunDnsCacheMu.Unlock()
	entry, ok := tunDnsCache[key]
	if !ok || time.Now().After(entry.exp) {
		return nil
	}
	out := append([]byte(nil), entry.resp...)
	if len(out) >= 2 && len(query) >= 2 {
		out[0] = query[0]
		out[1] = query[1]
	}
	return out
}

func storeTunDnsCache(query, resp []byte) {
	key := tunDnsCacheKey(query)
	if key == "" || len(resp) < 12 {
		return
	}
	tunDnsCacheMu.Lock()
	if len(tunDnsCache) > 256 {
		tunDnsCache = map[string]tunDnsCacheEntry{}
	}
	tunDnsCache[key] = tunDnsCacheEntry{resp: append([]byte(nil), resp...), exp: time.Now().Add(30 * time.Second)}
	tunDnsCacheMu.Unlock()
}

func queryUpstreamDns(query []byte) ([]byte, error) {
	var last error
	for _, server := range []string{"1.1.1.1:53", "8.8.8.8:53"} {
		raddr, err := net.ResolveUDPAddr("udp4", server)
		if err != nil {
			last = err
			continue
		}
		conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4zero, Port: 0})
		if err != nil {
			last = err
			continue
		}
		protectConn(conn)
		_ = conn.SetDeadline(time.Now().Add(3 * time.Second))
		if _, err = conn.WriteTo(query, raddr); err != nil {
			last = err
			conn.Close()
			continue
		}
		buf := make([]byte, 4096)
		n, _, err := conn.ReadFrom(buf)
		conn.Close()
		if err != nil {
			last = err
			continue
		}
		if n >= 12 {
			return buf[:n], nil
		}
	}
	if last == nil {
		last = errors.New("empty dns reply")
	}
	return nil, last
}

func buildIPv4UdpPacket(src, dst net.IP, srcPort, dstPort uint16, payload []byte) []byte {
	src4 := src.To4()
	dst4 := dst.To4()
	if src4 == nil || dst4 == nil {
		return nil
	}

	udpLen := 8 + len(payload)
	total := 20 + udpLen
	pkt := make([]byte, total)
	pkt[0] = 0x45
	pkt[1] = 0
	binary.BigEndian.PutUint16(pkt[2:4], uint16(total))
	binary.BigEndian.PutUint16(pkt[4:6], 0)
	pkt[6] = 0x40
	pkt[7] = 0
	pkt[8] = 64
	pkt[9] = 17
	copy(pkt[12:16], src4)
	copy(pkt[16:20], dst4)
	cs := ipv4Checksum(pkt[:20])
	binary.BigEndian.PutUint16(pkt[10:12], cs)

	binary.BigEndian.PutUint16(pkt[20:22], srcPort)
	binary.BigEndian.PutUint16(pkt[22:24], dstPort)
	binary.BigEndian.PutUint16(pkt[24:26], uint16(udpLen))
	binary.BigEndian.PutUint16(pkt[26:28], 0)
	copy(pkt[28:], payload)
	return pkt
}

func ipv4Checksum(hdr []byte) uint16 {
	var sum uint32
	for i := 0; i+1 < len(hdr); i += 2 {
		sum += uint32(binary.BigEndian.Uint16(hdr[i : i+2]))
	}
	for sum > 0xffff {
		sum = (sum >> 16) + (sum & 0xffff)
	}
	return ^uint16(sum)
}
