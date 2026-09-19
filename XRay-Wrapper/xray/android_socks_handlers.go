//go:build android

package xray

import (
	"errors"
	"fmt"
	"io"
	"net"
	"strings"
	"sync"
	"time"

	"golang.org/x/net/proxy"

	"github.com/eycorsican/go-tun2socks/common/log"
	tcore "github.com/eycorsican/go-tun2socks/core"
	tsocks "github.com/eycorsican/go-tun2socks/proxy/socks"
)

const (
	androidSocks5MethodNoAuth           = 0x00
	androidSocks5MethodUsernamePassword = 0x02
	androidSocks5MethodNoAcceptable     = 0xff
	androidSocks5Version                = 0x05
	androidSocks5UDPAssociate           = 0x03

	// max IP packet size - min IP header size - min UDP header size - min SOCKS5 header size
	androidMaxUdpPayloadSize = 65535 - 20 - 8 - 7

	// Yandex OpenFlux mux dies when a browser opens dozens of parallel TCP
	// streams. Cap concurrent SOCKS relays so pages can finish.
	androidMaxConcurrentSocks = 12
)

var androidSocksGate = make(chan struct{}, androidMaxConcurrentSocks)

type androidSocksTCPHandler struct {
	proxyHost string
	proxyPort uint16
	auth      *localSocksAuth
}

type androidSocksUDPHandler struct {
	sync.Mutex

	proxyHost   string
	proxyPort   uint16
	auth        *localSocksAuth
	udpConns    map[tcore.UDPConn]net.PacketConn
	tcpConns    map[tcore.UDPConn]net.Conn
	remoteAddrs map[tcore.UDPConn]*net.UDPAddr
	timeout     time.Duration
}

type tcpRelayDirection byte

const (
	relayUplink tcpRelayDirection = iota
	relayDownlink
)

type duplexConn interface {
	net.Conn
	CloseRead() error
	CloseWrite() error
}

func newAndroidTCPHandler(proxyHost string, proxyPort uint16, auth *localSocksAuth) tcore.TCPConnHandler {
	return &androidSocksTCPHandler{
		proxyHost: proxyHost,
		proxyPort: proxyPort,
		auth:      auth,
	}
}

func newAndroidUDPHandler(proxyHost string, proxyPort uint16, auth *localSocksAuth, timeout time.Duration) tcore.UDPConnHandler {
	return &androidSocksUDPHandler{
		proxyHost:   proxyHost,
		proxyPort:   proxyPort,
		auth:        auth,
		udpConns:    make(map[tcore.UDPConn]net.PacketConn, 8),
		tcpConns:    make(map[tcore.UDPConn]net.Conn, 8),
		remoteAddrs: make(map[tcore.UDPConn]*net.UDPAddr, 8),
		timeout:     timeout,
	}
}

func acquireAndroidSocksGate(timeout time.Duration) bool {
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case androidSocksGate <- struct{}{}:
		return true
	case <-timer.C:
		return false
	}
}

type androidTimedDialer struct {
	timeout time.Duration
}

func (d androidTimedDialer) Dial(network, address string) (net.Conn, error) {
	conn, err := net.DialTimeout(network, address, d.timeout)
	if err != nil {
		return nil, err
	}
	if err := conn.SetDeadline(time.Now().Add(d.timeout)); err != nil {
		conn.Close()
		return nil, err
	}
	return conn, nil
}

func (h *androidSocksTCPHandler) Handle(conn net.Conn, target *net.TCPAddr) error {
	go func() {
		if !acquireAndroidSocksGate(20 * time.Second) {
			log.Errorf("socks gate timeout %v", target)
			conn.Close()
			return
		}
		defer func() { <-androidSocksGate }()

		var proxyAuth *proxy.Auth
		if h.auth != nil && h.auth.enabled() {
			proxyAuth = &proxy.Auth{
				User:     h.auth.Username,
				Password: h.auth.Password,
			}
		}

		socksAddr := tcore.ParseTCPAddr(h.proxyHost, h.proxyPort).String()
		dialer, err := proxy.SOCKS5("tcp", socksAddr, proxyAuth, androidTimedDialer{timeout: 8 * time.Second})
		if err != nil {
			log.Errorf("socks dialer: %v", err)
			conn.Close()
			return
		}

		dialTarget := target
		if ip4 := target.IP.To4(); ip4 != nil {
			dialTarget = &net.TCPAddr{IP: ip4, Port: target.Port}
		}

		upstreamConn, err := dialer.Dial("tcp", dialTarget.String())
		if err != nil {
			log.Errorf("socks connect %v: %v", dialTarget, err)
			conn.Close()
			return
		}
		_ = upstreamConn.SetDeadline(time.Time{})

		log.Infof("new proxy connection to %v", target)
		relayTCP(conn, upstreamConn)
	}()
	return nil
}

func relayTCP(lhs net.Conn, rhs net.Conn) {
	upCh := make(chan struct{})

	closeConn := func(dir tcpRelayDirection, interrupt bool) {
		lhsDConn, lhsOk := lhs.(duplexConn)
		rhsDConn, rhsOk := rhs.(duplexConn)
		if !interrupt && lhsOk && rhsOk {
			switch dir {
			case relayUplink:
				lhsDConn.CloseRead()
				rhsDConn.CloseWrite()
			case relayDownlink:
				lhsDConn.CloseWrite()
				rhsDConn.CloseRead()
			default:
				panic("unexpected TCP relay direction")
			}
		} else {
			lhs.Close()
			rhs.Close()
		}
	}

	go func() {
		if _, err := io.Copy(rhs, lhs); err != nil {
			closeConn(relayUplink, true)
		} else {
			closeConn(relayUplink, false)
		}
		upCh <- struct{}{}
	}()

	if _, err := io.Copy(lhs, rhs); err != nil {
		closeConn(relayDownlink, true)
	} else {
		closeConn(relayDownlink, false)
	}

	<-upCh
}

func (h *androidSocksUDPHandler) Connect(conn tcore.UDPConn, target *net.UDPAddr) error {
	if target == nil {
		return h.connectInternal(conn, "")
	}
	return h.connectInternal(conn, target.String())
}

func (h *androidSocksUDPHandler) connectInternal(conn tcore.UDPConn, dest string) error {
	tcpConn, err := net.DialTimeout("tcp", tcore.ParseTCPAddr(h.proxyHost, h.proxyPort).String(), 4*time.Second)
	if err != nil {
		return err
	}

	if err := authenticateSocks5Connection(tcpConn, h.auth); err != nil {
		tcpConn.Close()
		return err
	}

	if _, err := tcpConn.Write(append([]byte{androidSocks5Version, androidSocks5UDPAssociate, 0}, []byte{1, 0, 0, 0, 0, 0, 0}...)); err != nil {
		tcpConn.Close()
		return err
	}

	buf := make([]byte, tsocks.MaxAddrLen)
	if _, err := io.ReadFull(tcpConn, buf[:3]); err != nil {
		tcpConn.Close()
		return err
	}

	rep := buf[1]
	if rep != 0 {
		tcpConn.Close()
		return fmt.Errorf("SOCKS UDP associate failed with code %d", rep)
	}

	remoteAddr, err := readAndroidSocksAddr(tcpConn, buf)
	if err != nil {
		tcpConn.Close()
		return err
	}

	resolvedRemoteAddr, err := net.ResolveUDPAddr("udp", remoteAddr.String())
	if err != nil {
		tcpConn.Close()
		return errors.New("failed to resolve SOCKS UDP relay address")
	}

	packetConn, err := net.ListenPacket("udp", "")
	if err != nil {
		tcpConn.Close()
		return err
	}

	h.Lock()
	h.tcpConns[conn] = tcpConn
	h.udpConns[conn] = packetConn
	h.remoteAddrs[conn] = resolvedRemoteAddr
	h.Unlock()

	go h.handleTCP(conn, tcpConn)
	go h.fetchUDPInput(conn, packetConn)

	log.Infof("new proxy connection to %v", dest)
	return nil
}

func (h *androidSocksUDPHandler) ReceiveTo(conn tcore.UDPConn, data []byte, addr *net.UDPAddr) error {
	h.Lock()
	packetConn, ok1 := h.udpConns[conn]
	remoteAddr, ok2 := h.remoteAddrs[conn]
	h.Unlock()

	if ok1 && ok2 {
		buf := append([]byte{0, 0, 0}, tsocks.ParseAddr(addr.String())...)
		buf = append(buf, data[:]...)
		if _, err := packetConn.WriteTo(buf, remoteAddr); err != nil {
			h.Close(conn)
			return fmt.Errorf("write remote failed: %w", err)
		}
		return nil
	}

	h.Close(conn)
	return fmt.Errorf("proxy connection %v->%v does not exist", conn.LocalAddr(), addr)
}

func (h *androidSocksUDPHandler) Close(conn tcore.UDPConn) {
	conn.Close()

	h.Lock()
	defer h.Unlock()

	if tcpConn, ok := h.tcpConns[conn]; ok {
		tcpConn.Close()
		delete(h.tcpConns, conn)
	}

	if packetConn, ok := h.udpConns[conn]; ok {
		packetConn.Close()
		delete(h.udpConns, conn)
	}

	delete(h.remoteAddrs, conn)
}

func (h *androidSocksUDPHandler) handleTCP(conn tcore.UDPConn, tcpConn net.Conn) {
	buf := tcore.NewBytes(tcore.BufSize)
	defer func() {
		h.Close(conn)
		tcore.FreeBytes(buf)
	}()

	for {
		tcpConn.SetDeadline(time.Time{})
		if _, err := tcpConn.Read(buf); err != nil {
			return
		}
	}
}

func (h *androidSocksUDPHandler) fetchUDPInput(conn tcore.UDPConn, input net.PacketConn) {
	buf := tcore.NewBytes(androidMaxUdpPayloadSize)
	defer func() {
		h.Close(conn)
		tcore.FreeBytes(buf)
	}()

	for {
		input.SetDeadline(time.Now().Add(h.timeout))
		n, _, err := input.ReadFrom(buf)
		if err != nil {
			return
		}
		if n < 3 {
			continue
		}

		addr := tsocks.SplitAddr(buf[3:n])
		if addr == nil {
			continue
		}

		resolvedAddr, err := net.ResolveUDPAddr("udp", addr.String())
		if err != nil {
			continue
		}

		if _, err = conn.WriteFrom(buf[int(3+len(addr)):n], resolvedAddr); err != nil {
			log.Warnf("write local failed: %v", err)
			return
		}
	}
}

func authenticateSocks5Connection(conn net.Conn, auth *localSocksAuth) error {
	if auth != nil && auth.enabled() {
		if _, err := conn.Write([]byte{androidSocks5Version, 1, androidSocks5MethodUsernamePassword}); err != nil {
			return err
		}

		reply := make([]byte, 2)
		if _, err := io.ReadFull(conn, reply); err != nil {
			return err
		}
		if reply[0] != androidSocks5Version {
			return fmt.Errorf("unexpected SOCKS version in method reply: %d", reply[0])
		}
		if reply[1] == androidSocks5MethodNoAcceptable {
			return errors.New("SOCKS server rejected all authentication methods")
		}
		if reply[1] != androidSocks5MethodUsernamePassword {
			return fmt.Errorf("SOCKS server selected unexpected auth method: %d", reply[1])
		}

		username := []byte(auth.Username)
		password := []byte(auth.Password)
		if len(username) == 0 || len(username) > 255 || len(password) > 255 {
			return errors.New("invalid SOCKS credential lengths")
		}

		authRequest := make([]byte, 0, 3+len(username)+len(password))
		authRequest = append(authRequest, 1, byte(len(username)))
		authRequest = append(authRequest, username...)
		authRequest = append(authRequest, byte(len(password)))
		authRequest = append(authRequest, password...)
		if _, err := conn.Write(authRequest); err != nil {
			return err
		}

		authReply := make([]byte, 2)
		if _, err := io.ReadFull(conn, authReply); err != nil {
			return err
		}
		if authReply[1] != 0x00 {
			return errors.New("SOCKS username/password authentication failed")
		}

		return nil
	}

	if _, err := conn.Write([]byte{androidSocks5Version, 1, androidSocks5MethodNoAuth}); err != nil {
		return err
	}

	reply := make([]byte, 2)
	if _, err := io.ReadFull(conn, reply); err != nil {
		return err
	}
	if reply[0] != androidSocks5Version {
		return fmt.Errorf("unexpected SOCKS version in method reply: %d", reply[0])
	}
	if reply[1] != androidSocks5MethodNoAuth {
		return fmt.Errorf("SOCKS server selected unsupported auth method: %d", reply[1])
	}

	return nil
}

func readAndroidSocksAddr(r io.Reader, buffer []byte) (tsocks.Addr, error) {
	if len(buffer) < tsocks.MaxAddrLen {
		return nil, io.ErrShortBuffer
	}

	if _, err := io.ReadFull(r, buffer[:1]); err != nil {
		return nil, err
	}

	switch buffer[0] {
	case 0x03:
		if _, err := io.ReadFull(r, buffer[1:2]); err != nil {
			return nil, err
		}
		if _, err := io.ReadFull(r, buffer[2:2+int(buffer[1])+2]); err != nil {
			return nil, err
		}
		return buffer[:1+1+int(buffer[1])+2], nil
	case 0x01:
		if _, err := io.ReadFull(r, buffer[1:1+net.IPv4len+2]); err != nil {
			return nil, err
		}
		return buffer[:1+net.IPv4len+2], nil
	case 0x04:
		if _, err := io.ReadFull(r, buffer[1:1+net.IPv6len+2]); err != nil {
			return nil, err
		}
		return buffer[:1+net.IPv6len+2], nil
	default:
		return nil, errors.New("unsupported SOCKS address type")
	}
}

// OpenFlux cannot do UDP ASSOCIATE, and TCP/53 through the Yandex mux times out.
// Resolve TUN DNS in this process (the VPN app is disallowed from its own TUN,
// so net.LookupIP uses Wi-Fi/cell) and return A records only.
type androidDnsOverTcpHandler struct {
	proxyHost string
	proxyPort uint16
	auth      *localSocksAuth
}

func newAndroidDnsOverTcpHandler(proxyHost string, proxyPort uint16, auth *localSocksAuth) tcore.UDPConnHandler {
	return &androidDnsOverTcpHandler{
		proxyHost: proxyHost,
		proxyPort: proxyPort,
		auth:      auth,
	}
}

func (h *androidDnsOverTcpHandler) Connect(conn tcore.UDPConn, target *net.UDPAddr) error {
	if target != nil && target.Port != 53 {
		conn.Close()
		return errors.New("udp is disabled")
	}
	return nil
}

func (h *androidDnsOverTcpHandler) ReceiveTo(conn tcore.UDPConn, data []byte, addr *net.UDPAddr) error {
	if addr == nil || addr.Port != 53 || len(data) < 12 {
		conn.Close()
		return errors.New("udp is disabled")
	}

	go func() {
		resp, err := resolveTunDnsLocally(data)
		if err != nil {
			log.Errorf("tun local dns failed: %v", err)
			conn.Close()
			return
		}
		if _, err := conn.WriteFrom(resp, addr); err != nil {
			conn.Close()
		}
	}()
	return nil
}

func resolveTunDnsLocally(query []byte) ([]byte, error) {
	name, qtype, err := parseDnsQuestion(query)
	if err != nil {
		return nil, err
	}

	if qtype != 1 {
		return buildDnsResponse(query, nil, qtype), nil
	}

	ips, err := net.LookupIP(name)
	if err != nil {
		return buildDnsResponse(query, nil, qtype), nil
	}

	var v4 []net.IP
	for _, ip := range ips {
		if ip4 := ip.To4(); ip4 != nil {
			v4 = append(v4, ip4)
		}
	}
	return buildDnsResponse(query, v4, qtype), nil
}

func parseDnsQuestion(query []byte) (string, uint16, error) {
	if len(query) < 12 {
		return "", 0, errors.New("short dns query")
	}
	offset := 12
	var labels []string
	for {
		if offset >= len(query) {
			return "", 0, errors.New("truncated dns name")
		}
		n := int(query[offset])
		if n == 0 {
			offset++
			break
		}
		if n&0xC0 == 0xC0 {
			return "", 0, errors.New("compressed dns question")
		}
		offset++
		if offset+n > len(query) {
			return "", 0, errors.New("truncated dns label")
		}
		labels = append(labels, string(query[offset:offset+n]))
		offset += n
	}
	if offset+4 > len(query) {
		return "", 0, errors.New("truncated dns question type")
	}
	qtype := uint16(query[offset])<<8 | uint16(query[offset+1])
	return strings.Join(labels, "."), qtype, nil
}

func buildDnsResponse(query []byte, ips []net.IP, qtype uint16) []byte {
	resp := make([]byte, 0, len(query)+16*len(ips))
	resp = append(resp, query...)
	if len(resp) < 12 {
		return resp
	}
	// QR=1, RD copied, RA=1
	resp[2] = query[2] | 0x80
	resp[3] = 0x80
	resp[6] = 0
	resp[7] = 0
	resp[8] = 0
	resp[9] = 0
	resp[10] = 0
	resp[11] = 0

	if qtype != 1 || len(ips) == 0 {
		return resp
	}

	resp[6] = byte(len(ips) >> 8)
	resp[7] = byte(len(ips))
	for _, ip := range ips {
		ip4 := ip.To4()
		if ip4 == nil {
			continue
		}
		resp = append(resp,
			0xC0, 0x0C,
			0x00, 0x01,
			0x00, 0x01,
			0x00, 0x00, 0x00, 0x1E,
			0x00, 0x04,
			ip4[0], ip4[1], ip4[2], ip4[3],
		)
	}
	return resp
}
