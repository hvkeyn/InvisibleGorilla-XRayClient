package xray

import (
	"encoding/binary"
	"net"
	"testing"
)

func TestRemoteDnsKeepsTheNameAndReusesTheAddress(t *testing.T) {
	setTunDnsRemote(true)
	query := []byte{
		0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		7, 'e', 'x', 'a', 'm', 'p', 'l', 'e', 3, 'c', 'o', 'm', 0,
		0x00, 0x01, 0x00, 0x01,
	}
	first := answerTunRemoteDns(query)
	if first == nil || binary.BigEndian.Uint16(first[6:8]) != 1 {
		t.Fatal("A query produced no answer")
	}
	ip := net.IP(first[len(first)-4:])
	if lookupTunFakeHost(ip) != "example.com" {
		t.Fatalf("mapped host = %q", lookupTunFakeHost(ip))
	}
	second := answerTunRemoteDns(query)
	if !net.IP(second[len(second)-4:]).Equal(ip) {
		t.Fatal("same name received a different address")
	}

	aaaa := append([]byte(nil), query...)
	binary.BigEndian.PutUint16(aaaa[len(aaaa)-4:len(aaaa)-2], 28)
	empty := answerTunRemoteDns(aaaa)
	if empty == nil || binary.BigEndian.Uint16(empty[6:8]) != 0 {
		t.Fatal("AAAA should not leave the device")
	}
}

func TestRemoteDnsRejectsACountryStyleRestriction(t *testing.T) {
	if tunDnsRemoteMarker == "" {
		t.Fatal("marker empty")
	}
	odd := []byte{
		0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		3, 'a', ' ', 'b', 0, 0x00, 0x01, 0x00, 0x01,
	}
	resp := answerTunRemoteDns(odd)
	if resp == nil || binary.BigEndian.Uint16(resp[6:8]) != 0 {
		t.Fatal("unsafe name must not be published")
	}
	if lookupTunFakeHost(net.IPv4(198, 18, 0, 1)) == "a b" {
		t.Fatal("unsafe name was mapped")
	}
}
