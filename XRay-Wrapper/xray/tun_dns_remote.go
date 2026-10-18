package xray

import (
	"encoding/binary"
	"net"
	"strings"
	"sync"
	"sync/atomic"
)

// tor-remote is the Android tunnel marker for ONLY_TOR. DNS answers use an
// address from 198.18.0.0/16, and the matching TCP connection is dialed with
// the original hostname so Tor resolves it on the same circuit.
const tunDnsRemoteMarker = "tor-remote"

var tunDnsRemote atomic.Bool

func setTunDnsRemote(enabled bool) {
	tunDnsRemote.Store(enabled)
	clearTunFakeHosts()
}

func tunDnsRemoteEnabled() bool {
	return tunDnsRemote.Load()
}

var tunFakeMu sync.Mutex
var tunFakeByName = map[string]net.IP{}
var tunFakeByIP = map[string]string{}
var tunFakeNext uint32 = 1

func clearTunFakeHosts() {
	tunFakeMu.Lock()
	tunFakeByName = map[string]net.IP{}
	tunFakeByIP = map[string]string{}
	tunFakeNext = 1
	tunFakeMu.Unlock()
}

func assignTunFakeIP(name string) net.IP {
	tunFakeMu.Lock()
	defer tunFakeMu.Unlock()
	if ip, ok := tunFakeByName[name]; ok {
		return append(net.IP(nil), ip...)
	}
	var ip net.IP
	for tries := 0; tries < 8; tries++ {
		id := tunFakeNext
		if tunFakeNext >= 65534 {
			tunFakeNext = 1
		} else {
			tunFakeNext++
		}
		ip = net.IPv4(198, 18, byte(id>>8), byte(id)).To4()
		if _, used := tunFakeByIP[ip.String()]; !used {
			break
		}
	}
	stored := append(net.IP(nil), ip...)
	tunFakeByName[name] = stored
	tunFakeByIP[stored.String()] = name
	return append(net.IP(nil), stored...)
}

func lookupTunFakeHost(ip net.IP) string {
	ip4 := ip.To4()
	if ip4 == nil {
		return ""
	}
	tunFakeMu.Lock()
	defer tunFakeMu.Unlock()
	return tunFakeByIP[ip4.String()]
}

func answerTunRemoteDns(query []byte) []byte {
	name, qtype, qEnd, ok := parseTunDnsQuestion(query)
	if !ok {
		return nil
	}
	question := append([]byte(nil), query[12:qEnd]...)
	if qtype != 1 {
		return dnsNoError(query, question, nil)
	}
	host := normalizeDnsName(name)
	if host == "" {
		return dnsNoError(query, question, nil)
	}
	return dnsNoError(query, question, assignTunFakeIP(host))
}

func parseTunDnsQuestion(msg []byte) (name string, qtype uint16, qEnd int, ok bool) {
	if len(msg) < 12 || binary.BigEndian.Uint16(msg[4:6]) != 1 {
		return "", 0, 0, false
	}
	off := 12
	var labels []string
	for {
		if off >= len(msg) {
			return "", 0, 0, false
		}
		n := int(msg[off])
		if n == 0 {
			off++
			break
		}
		if n&0xC0 != 0 || n > 63 || off+1+n > len(msg) || len(labels) > 64 {
			return "", 0, 0, false
		}
		labels = append(labels, string(msg[off+1:off+1+n]))
		off += 1 + n
	}
	if off+4 > len(msg) {
		return "", 0, 0, false
	}
	qtype = binary.BigEndian.Uint16(msg[off : off+2])
	return strings.Join(labels, "."), qtype, off + 4, true
}

func normalizeDnsName(name string) string {
	name = strings.TrimSuffix(strings.ToLower(name), ".")
	if name == "" || len(name) > 253 || strings.Contains(name, "..") {
		return ""
	}
	for _, r := range name {
		if (r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-' || r == '.' || r == '_' {
			continue
		}
		return ""
	}
	return name
}

func dnsNoError(query, question []byte, ip net.IP) []byte {
	ancount := 0
	ip4 := ip.To4()
	if ip4 != nil {
		ancount = 1
	}
	resp := make([]byte, 12+len(question)+(16*ancount))
	resp[0], resp[1] = query[0], query[1]
	resp[2] = 0x81
	resp[3] = 0x80
	binary.BigEndian.PutUint16(resp[4:6], 1)
	binary.BigEndian.PutUint16(resp[6:8], uint16(ancount))
	copy(resp[12:], question)
	if ancount == 0 {
		return resp
	}
	off := 12 + len(question)
	resp[off] = 0xC0
	resp[off+1] = 0x0C
	binary.BigEndian.PutUint16(resp[off+2:off+4], 1)
	binary.BigEndian.PutUint16(resp[off+4:off+6], 1)
	binary.BigEndian.PutUint32(resp[off+6:off+10], 120)
	binary.BigEndian.PutUint16(resp[off+10:off+12], 4)
	copy(resp[off+12:off+16], ip4)
	return resp
}
