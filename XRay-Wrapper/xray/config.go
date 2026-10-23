package xray

import (
	"C"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/xtls/xray-core/app/dispatcher"
	"github.com/xtls/xray-core/app/log"
	"github.com/xtls/xray-core/app/policy"
	"github.com/xtls/xray-core/app/proxyman"
	appStats "github.com/xtls/xray-core/app/stats"
	fstats "github.com/xtls/xray-core/features/stats"
	"github.com/xtls/xray-core/common/cmdarg"
	"github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/common/serial"
	"github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/proxy/http"
	"github.com/xtls/xray-core/proxy/socks"

	clog "github.com/xtls/xray-core/common/log"
)

// activeInstance keeps the running core so traffic counters can be read in-process.
var activeInstance *core.Instance

//export GetTrafficCounters
func GetTrafficCounters(uplink *C.longlong, downlink *C.longlong) {
	up, down := readInboundTraffic()
	*uplink = C.longlong(up)
	*downlink = C.longlong(down)
}

func readInboundTraffic() (int64, int64) {
	instance := activeInstance
	if instance == nil {
		return 0, 0
	}

	manager, ok := instance.GetFeature(fstats.ManagerType()).(fstats.Manager)
	if !ok {
		return 0, 0
	}

	return counterValue(manager, "inbound>>>local>>>traffic>>>uplink"),
		counterValue(manager, "inbound>>>local>>>traffic>>>downlink")
}

func counterValue(manager fstats.Manager, name string) int64 {
	counter := manager.GetCounter(name)
	if counter == nil {
		return 0
	}

	return counter.Value()
}

//export GetConfigFormat
func GetConfigFormat(path *C.char) *C.char {
	file := C.GoString(path)
	ext := strings.TrimPrefix(filepath.Ext(file), ".")
	format := core.GetFormatByExtension(ext)

	if format == "" {
		format = "auto"
	}

	return C.CString(format)
}

//export IsFileExists
func IsFileExists(path *C.char) bool {
	file := C.GoString(path)
	if file == "" {
		return false
	}

	info, err := os.Stat(file)
	return err == nil && !info.IsDir()
}

//export LoadConfig
func LoadConfig(ext *C.char, path *C.char) *C.char {
	format := C.GoString(ext)
	file := cmdarg.Arg{C.GoString(path)}

	config, err := core.LoadConfig(format, file)
	if err != nil {
		fmt.Println("error | failed to load config file >", err)
		return C.CString("")
	}

	configJson, err := json.Marshal(config)
	if err != nil {
		fmt.Println("error | failed to encode config to json >", err)
		return C.CString("")
	}

	return C.CString(string(configJson))
}

func convertJsonToObject(config *C.char) *core.Config {
	configJson := C.GoString(config)
	configObj := &core.Config{}

	json.Unmarshal([]byte(configJson), configObj)
	stripManagementApps(configObj)
	return configObj
}

// managementAppMarkers identifies xray-core "management" application configs
// that a plain client tunnel never needs: the gRPC commander/API, the stats
// engine, the local policy module and the observatory.
//
// An exposed Xray gRPC API reachable on 127.0.0.1 is an *instant* "VPN
// detected" verdict for anti-circumvention probes (e.g. RKNHardering's
// XrayApiScanner issues a real HandlerService.listOutbounds gRPC call against
// every localhost port). We already replace every inbound at runtime, so the
// API has no transport to ride on, but dropping the apps themselves guarantees
// the commander can never be instantiated regardless of where the config came
// from (subscription, raw import, etc.).
var managementAppMarkers = []string{
	".commander.",
	".stats.",
	".policy.",
	".observatory.",
}

// stripManagementApps removes management application configs from a loaded
// core.Config. Transport-critical apps (dispatcher, proxyman inbound/outbound,
// dns, router, log) are matched by different type URLs and are left intact.
func stripManagementApps(configObj *core.Config) {
	if configObj == nil || len(configObj.App) == 0 {
		return
	}

	filtered := configObj.App[:0]
	for _, app := range configObj.App {
		if app == nil {
			continue
		}

		if isManagementApp(app.Type) {
			continue
		}

		filtered = append(filtered, app)
	}

	configObj.App = filtered
}

func isManagementApp(typeURL string) bool {
	for _, marker := range managementAppMarkers {
		if strings.Contains(typeURL, marker) {
			return true
		}
	}

	return false
}

func convertLogLevelToSeverity(logLevel *C.char) clog.Severity {
	switch level := strings.ToLower(C.GoString(logLevel)); level {
	case "debug":
		return clog.Severity_Debug
	case "info":
		return clog.Severity_Info
	case "warning":
		return clog.Severity_Warning
	case "error":
		return clog.Severity_Error
	default:
		return clog.Severity_Unknown
	}
}

// ensureCoreApps makes sure the feature managers the tunnel needs are present.
// Subscription configs often list their own apps (log, dns, router, observatory)
// and omit the proxyman/dispatcher pair, and a non-empty app list disables the
// defaults inside core.New: the inbound manager then stays nil and the core
// panics with "interface is nil, not inbound.Manager" on start.
func ensureCoreApps(configObj *core.Config) {
	if configObj == nil {
		return
	}

	required := []*serial.TypedMessage{
		serial.ToTypedMessage(&dispatcher.Config{}),
		serial.ToTypedMessage(&proxyman.InboundConfig{}),
		serial.ToTypedMessage(&proxyman.OutboundConfig{}),
		// Traffic counters for the UI: the stats manager is kept in-process only,
		// no API inbound is added, so nothing is exposed on localhost.
		serial.ToTypedMessage(&appStats.Config{}),
		serial.ToTypedMessage(&policy.Config{
			System: &policy.SystemPolicy{
				Stats: &policy.SystemPolicy_Stats{
					InboundUplink:    true,
					InboundDownlink:  true,
					OutboundUplink:   true,
					OutboundDownlink: true,
				},
			},
		}),
	}

	for _, element := range required {
		exists := false
		for _, app := range configObj.App {
			if app != nil && app.Type == element.Type {
				exists = true
				break
			}
		}

		if !exists {
			configObj.App = append(configObj.App, element)
		}
	}
}

func insertElementToConfigApp(element *serial.TypedMessage, configApp []*serial.TypedMessage) {
	for i := 0; i < len(configApp); i++ {
		if configApp[i].Type == element.Type {
			configApp[i] = element
			return
		}
	}

	configApp = append(configApp, element)
}

func overrideLog(logLevel clog.Severity, logPath *C.char) *serial.TypedMessage {
	path := C.GoString(logPath)

	return serial.ToTypedMessage(&log.Config{
		ErrorLogType:  log.LogType_File,
		ErrorLogPath:  path + "/error.log",
		ErrorLogLevel: logLevel,
		AccessLogType: log.LogType_File,
		AccessLogPath: path + "/access.log",
	})
}

func overrideInbound(port net.Port, isSocks bool, isUdpEnabled bool, auth *localSocksAuth) []*core.InboundHandlerConfig {
	if isSocks == false {
		return overrideInboundToHttp(port)
	} else {
		return overrideInboundToSocks(port, isUdpEnabled, auth)
	}
}

func overrideInboundToHttp(port net.Port) []*core.InboundHandlerConfig {
	return []*core.InboundHandlerConfig{
		{
			ReceiverSettings: serial.ToTypedMessage(&proxyman.ReceiverConfig{
				PortList: &net.PortList{
					Range: []*net.PortRange{
						net.SinglePortRange(port),
					},
				},
				Listen: &net.IPOrDomain{
					Address: &net.IPOrDomain_Ip{
						Ip: []byte{127, 0, 0, 1},
					},
				},
			}),
			ProxySettings: serial.ToTypedMessage(&http.ServerConfig{}),
			Tag:           "local",
		},
	}
}

func overrideInboundToSocks(port net.Port, isUdpEnabled bool, auth *localSocksAuth) []*core.InboundHandlerConfig {
	serverConfig := &socks.ServerConfig{
		UdpEnabled: isUdpEnabled,
	}
	if auth != nil && auth.enabled() {
		serverConfig.AuthType = socks.AuthType_PASSWORD
		serverConfig.Accounts = auth.accounts()
	}

	return []*core.InboundHandlerConfig{
		{
			ReceiverSettings: serial.ToTypedMessage(&proxyman.ReceiverConfig{
				PortList: &net.PortList{
					Range: []*net.PortRange{
						net.SinglePortRange(port),
					},
				},
				Listen: &net.IPOrDomain{
					Address: &net.IPOrDomain_Ip{
						Ip: []byte{127, 0, 0, 1},
					},
				},
			}),
			ProxySettings: serial.ToTypedMessage(serverConfig),
			Tag:           "local",
		},
	}
}
