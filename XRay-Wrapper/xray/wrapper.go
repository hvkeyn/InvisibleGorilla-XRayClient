package xray

import (
	"C"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"runtime"
	"runtime/debug"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/xtls/xray-core/common/net"
	"github.com/xtls/xray-core/core"

	clog "github.com/xtls/xray-core/common/log"
	_ "github.com/xtls/xray-core/main/distro/all"
)

const (
	PingTimeout int = -1
	PingError   int = -2
)

var serverStopMutex sync.Mutex
var serverStopChannel chan struct{}
var serverLifecycleActive bool
var serverStopRequested bool

// describeConfig lists the apps and handler counts so a failed start can be explained.
func describeConfig(configObj *core.Config) string {
	if configObj == nil {
		return "config is nil"
	}

	names := make([]string, 0, len(configObj.App))
	for _, app := range configObj.App {
		if app != nil {
			names = append(names, app.Type)
		}
	}

	return fmt.Sprintf("apps=%d [%s] inbounds=%d outbounds=%d",
		len(configObj.App), strings.Join(names, ", "), len(configObj.Inbound), len(configObj.Outbound))
}

func writeDebugFile(logPath *C.char, text string) {
	path := C.GoString(logPath)
	if path == "" {
		return
	}

	_ = os.WriteFile(path+".debug", []byte(text), 0644)
}

// safeStart runs server.Start and converts a panic into an error plus a stack file.
func safeStart(server *core.Instance, logPath *C.char) (err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			stack := make([]byte, 1<<20)
			length := runtime.Stack(stack, false)
			message := fmt.Sprintf("panic: %v\n%s", recovered, stack[:length])
			writeDebugFile(logPath, message)
			err = fmt.Errorf("core panic: %v", recovered)
		}
	}()

	return server.Start()
}

// safeNew creates the core instance, converting a panic into an error.
func safeNew(configObj *core.Config, logPath *C.char, debugInfo string) (server *core.Instance, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			stack := make([]byte, 1<<20)
			length := runtime.Stack(stack, false)
			writeDebugFile(logPath, fmt.Sprintf("%s\npanic: %v\n%s", debugInfo, recovered, stack[:length]))
			server = nil
			err = fmt.Errorf("core panic: %v", recovered)
		}
	}()

	writeDebugFile(logPath, debugInfo)
	return core.New(configObj)
}

//export StartServer
func StartServer(config *C.char, port int, logLevel *C.char, logPath *C.char, isSocks bool, isUdpEnabled bool, username *C.char, password *C.char) {
	serverStopMutex.Lock()
	serverLifecycleActive = true
	serverStopRequested = false
	serverStopChannel = nil
	serverStopMutex.Unlock()

	logSeverity := convertLogLevelToSeverity(logLevel)
	configObj := convertJsonToObject(config)
	configObj.Inbound = overrideInbound(
		net.Port(port),
		isSocks,
		isUdpEnabled,
		newLocalSocksAuth(C.GoString(username), C.GoString(password)))

	if logSeverity != clog.Severity_Unknown {
		log := overrideLog(logSeverity, logPath)
		insertElementToConfigApp(log, configObj.App)
		tryMakingDirectory(logPath)
	}

	// The core panics (instead of returning an error) when a config confuses the
	// proxyman feature set. Catch it here: a panic in this process kills the whole
	// app, and the stack is the only way to explain why.
	debugInfo := describeConfig(configObj)
	server, err := safeNew(configObj, logPath, debugInfo)
	if err != nil {
		reportStartFailure(logPath, "failed to initialize the server", err)
		return
	}

	activeInstance = server

	if err := safeStart(server, logPath); err != nil {
		reportStartFailure(logPath, "failed to start server", err)
		return
	}

	defer server.Close()
	defer clearServerStopState()

	runtime.GC()
	debug.FreeOSMemory()

	stopChannel := make(chan struct{})
	serverStopMutex.Lock()
	serverStopChannel = stopChannel
	pendingStop := serverStopRequested
	serverStopRequested = false
	serverStopMutex.Unlock()

	if pendingStop {
		closeServerStopChannel(stopChannel)
	}

	osSignalChannel := make(chan os.Signal, 1)
	signal.Notify(osSignalChannel, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(osSignalChannel)

	select {
	case <-osSignalChannel:
	case <-stopChannel:
	}
}

// reportStartFailure stores the core error next to the log file and prints it, so the app can
// explain a refused config on platforms where stdout is not visible (Android).
func reportStartFailure(logPath *C.char, stage string, err error) {
	message := stage + " > " + err.Error()
	fmt.Println("error |", message)

	path := C.GoString(logPath)
	if path == "" {
		return
	}

	_ = os.WriteFile(path+".err", []byte(message), 0644)
}

//export StopServer
func StopServer() {
	// A panic while tearing the core down used to kill the whole app on STOP.
	defer func() {
		if recovered := recover(); recovered != nil {
			stack := make([]byte, 1<<20)
			length := runtime.Stack(stack, false)
			fmt.Println("panic on stop:", recovered)
			_ = os.WriteFile("/tmp/igx-stop-panic.log", []byte(fmt.Sprintf("panic: %v\n%s", recovered, stack[:length])), 0644)
		}
	}()
	serverStopMutex.Lock()
	stopChannel := serverStopChannel
	if stopChannel != nil {
		serverStopChannel = nil
		serverStopMutex.Unlock()
		closeServerStopChannel(stopChannel)
		return
	}

	if serverLifecycleActive {
		serverStopRequested = true
	}
	serverStopMutex.Unlock()
}

//export TestConnection
func TestConnection(config *C.char, port int) int {
	configObj := convertJsonToObject(config)
	configObj.Inbound = overrideInbound(net.Port(port), false, false, nil)

	for _, app := range configObj.App {
		if app != nil {
			fmt.Println("debug | app", app.Type)
		}
	}
	fmt.Println("debug | inbounds", len(configObj.Inbound), "outbounds", len(configObj.Outbound), "apps", len(configObj.App))

	server, err := core.New(configObj)
	if err != nil {
		return PingError
	}

	if err := server.Start(); err != nil {
		server.Close()
		return PingError
	}

	proxyUrl, err := url.Parse("http://127.0.0.1:" + strconv.Itoa(port))
	if err != nil {
		server.Close()
		return PingError
	}

	start := time.Now()
	client := &http.Client{
		Timeout: 8 * time.Second,
		Transport: &http.Transport{
			Proxy:                 http.ProxyURL(proxyUrl),
			TLSHandshakeTimeout:   3 * time.Second,
			ResponseHeaderTimeout: 5 * time.Second,
		},
	}
	req, err := http.NewRequest(http.MethodHead, "https://www.gstatic.com/generate_204", nil)
	if err != nil {
		server.Close()
		return PingError
	}
	response, err := client.Do(req)

	if err != nil {
		server.Close()
		return PingTimeout
	}

	if response.Body != nil {
		response.Body.Close()
	}

	server.Close()
	fmt.Println("info | response code >", response.StatusCode)

	if response.StatusCode == 204 {
		return int(time.Since(start).Milliseconds())
	}

	return PingTimeout
}

//export GetXrayCoreVersion
func GetXrayCoreVersion() *C.char {
	return C.CString(core.Version())
}

func clearServerStopState() {
	serverStopMutex.Lock()
	serverStopChannel = nil
	serverLifecycleActive = false
	serverStopRequested = false
	serverStopMutex.Unlock()
}

func closeServerStopChannel(channel chan struct{}) {
	defer func() {
		_ = recover()
	}()

	close(channel)
}
