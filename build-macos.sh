#!/usr/bin/env bash
#
# build-macos.sh — Build script for Invisible Gorilla XRay Client on macOS
#
# Tested on: macOS Sequoia 15.7.x (Apple Silicon & Intel)
#
# Automates the full build cycle:
#   1. Check & auto-install dependencies (Go, Xcode CLT, .NET SDK 7.0)
#   2. Build Go wrapper: XRayCore.dylib (c-shared) + gorilla-xray (CLI binary)
#   3. Download geoip.dat and geosite.dat
#   4. Build/publish .NET application (if cross-platform UI is available)
#   5. Package distribution bundle with binary + data files
#
# NOTE: The .NET WPF GUI (net7.0-windows) is Windows-only.
#       On macOS this script builds the XRayCore engine + geo databases
#       and packages them into a ready-to-use distribution bundle.
#       For a macOS GUI, the project needs porting to Avalonia UI or MAUI.
#
# Usage:
#   ./build-macos.sh                        # Full build (all steps)
#   ./build-macos.sh --step go              # Only build Go wrapper
#   ./build-macos.sh --step geo             # Only download geo databases
#   ./build-macos.sh --step dotnet          # Only build .NET app (requires UI port)
#   ./build-macos.sh --step bundle          # Only package distribution
#   ./build-macos.sh --publish              # Build + publish self-contained binary
#   ./build-macos.sh --config Debug         # Build in Debug mode
#   ./build-macos.sh --skip-dotnet          # Skip .NET build (WPF incompatible)
#   ./build-macos.sh --help                 # Show help
#

set -euo pipefail

# ─── Settings ─────────────────────────────────────────────────────────────────

readonly VERSION="3.2.5.0"
readonly SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
readonly WRAPPER_DIR="$SCRIPT_DIR/XRay-Wrapper"
readonly APP_DIR="$SCRIPT_DIR/InvisibleGorilla-XRay"
readonly LIBRARIES_DIR="$APP_DIR/Libraries"
readonly SOLUTION_FILE="$SCRIPT_DIR/InvisibleGorilla-XRay.sln"

readonly GEOIP_URL="https://github.com/v2fly/geoip/releases/latest/download/geoip.dat"
readonly GEOSITE_URL="https://github.com/v2fly/domain-list-community/releases/latest/download/dlc.dat"

# Defaults
STEP="all"
CONFIGURATION="Release"
PUBLISH=false
OUTPUT_DIR="./publish"
DIST_DIR="./dist"
SKIP_DOTNET=false

# Detect architecture
ARCH="$(uname -m)"
if [[ "$ARCH" == "arm64" ]]; then
    RUNTIME="osx-arm64"
    GO_ARCH="arm64"
else
    RUNTIME="osx-x64"
    GO_ARCH="amd64"
fi

# ─── Colors & output helpers ──────────────────────────────────────────────────

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
MAGENTA='\033[0;35m'
DIM='\033[2m'
NC='\033[0m'

SPIN_CHARS=('⠋' '⠙' '⠹' '⠸' '⠼' '⠴' '⠦' '⠧' '⠇' '⠏')

step_header() {
    echo ""
    echo -e "${CYAN}============================================================${NC}"
    echo -e "${CYAN}  $1${NC}"
    echo -e "${CYAN}============================================================${NC}"
    echo ""
}

ok()   { echo -e "${GREEN}[OK]${NC} $1"; }
info() { echo -e "${YELLOW}[..]${NC} $1"; }
err()  { echo -e "${RED}[!!]${NC} $1"; }

command_exists() { command -v "$1" &>/dev/null; }

format_size() {
    local bytes=$1
    if (( bytes >= 1073741824 )); then
        echo "$(echo "scale=1; $bytes / 1073741824" | bc) GB"
    elif (( bytes >= 1048576 )); then
        echo "$(echo "scale=1; $bytes / 1048576" | bc) MB"
    elif (( bytes >= 1024 )); then
        echo "$(echo "scale=1; $bytes / 1024" | bc) KB"
    else
        echo "$bytes B"
    fi
}

# Download with retry and progress
download_file() {
    local url="$1"
    local output="$2"
    local label="${3:-}"
    local min_size="${4:-1048576}"
    local max_retries=3

    [[ -n "$label" ]] && info "$label"

    for attempt in $(seq 1 $max_retries); do
        if curl -fSL --progress-bar \
            --retry 2 \
            --retry-delay 3 \
            -H "User-Agent: InvisibleGorilla-BuildScript/1.0" \
            -o "$output" \
            "$url"; then

            local actual_size
            actual_size=$(stat -f%z "$output" 2>/dev/null || stat -c%s "$output" 2>/dev/null || echo 0)

            if (( actual_size >= min_size )); then
                return 0
            fi

            err "Download incomplete: $(format_size "$actual_size"), minimum $(format_size "$min_size")"
            rm -f "$output"
        fi

        if (( attempt < max_retries )); then
            info "Download failed, retry ($attempt/$max_retries)..."
            sleep $((attempt * 2))
        fi
    done

    err "Failed to download after $max_retries attempts: $url"
    return 1
}

# Spinner for long-running processes
run_with_spinner() {
    local message="$1"
    shift
    local pid

    "$@" &
    pid=$!

    local i=0
    while kill -0 "$pid" 2>/dev/null; do
        printf "\r     ${DIM}%s %s${NC}%*s" "${SPIN_CHARS[$((i % ${#SPIN_CHARS[@]}))]}" "$message" 55 ""
        sleep 0.1
        i=$((i + 1))
    done

    wait "$pid"
    local exit_code=$?
    printf "\r%*s\r" 70 ""
    return $exit_code
}

# ─── Parse arguments ──────────────────────────────────────────────────────────

show_help() {
    cat <<'HELP'
Invisible Gorilla XRay Client — macOS Build Script

Usage: ./build-macos.sh [OPTIONS]

Options:
  --step <STEP>       Run specific build step:
                        all     - Full build (default)
                        go      - Only build Go wrapper (XRayCore.dylib)
                        geo     - Only download geo databases
                        dotnet  - Only build .NET application
                        bundle  - Only package distribution
  --config <CFG>      Build configuration: Debug or Release (default: Release)
  --publish           Publish as self-contained single binary
  --runtime <RID>     .NET runtime identifier (default: auto-detected)
  --output <DIR>      Output directory for publish (default: ./publish)
  --dist <DIR>        Distribution bundle directory (default: ./dist)
  --skip-dotnet       Skip .NET build step (for WPF-only projects)
  --help              Show this help

Architecture Detection:
  Automatically detects Apple Silicon (arm64) or Intel (x86_64)
  and selects the correct Go and .NET runtime identifiers.

Platform Notes:
  The .NET WPF GUI (net7.0-windows) is Windows-only.
  On macOS, this script builds:
    - XRayCore.dylib    (xray-core proxy engine)
    - geoip.dat         (IP geolocation database)
    - geosite.dat       (domain routing database)

  These are packaged into a distribution bundle that can be used
  with any macOS-compatible frontend or as a standalone library.

Examples:
  ./build-macos.sh                              # Full build
  ./build-macos.sh --step go                    # Only XRayCore.dylib
  ./build-macos.sh --step go --step geo         # Go wrapper + geo files
  ./build-macos.sh --publish --skip-dotnet      # Build + bundle without .NET
  ./build-macos.sh --step dotnet --config Debug # .NET Debug (requires UI port)
HELP
    exit 0
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --step)       STEP="$2"; shift 2 ;;
        --config)     CONFIGURATION="$2"; shift 2 ;;
        --publish)    PUBLISH=true; shift ;;
        --runtime)    RUNTIME="$2"; shift 2 ;;
        --output)     OUTPUT_DIR="$2"; shift 2 ;;
        --dist)       DIST_DIR="$2"; shift 2 ;;
        --skip-dotnet) SKIP_DOTNET=true; shift ;;
        --help|-h)    show_help ;;
        *)            err "Unknown option: $1"; echo ""; show_help ;;
    esac
done

# ─── Dependency: Xcode Command Line Tools ─────────────────────────────────────

ensure_xcode_clt() {
    if xcode-select -p &>/dev/null; then
        local clt_path
        clt_path="$(xcode-select -p)"
        # Verify cc actually works
        if cc --version &>/dev/null; then
            local cc_ver
            cc_ver="$(cc --version 2>&1 | head -1)"
            ok "C compiler: $cc_ver"
            return
        fi
    fi

    info "Xcode Command Line Tools not found, installing..."
    info "A dialog will appear — click 'Install' and wait for completion"
    xcode-select --install 2>/dev/null || true

    echo -ne "${DIM}     Waiting for Xcode CLT installation"
    until xcode-select -p &>/dev/null && cc --version &>/dev/null; do
        echo -ne "."
        sleep 5
    done
    echo -e "${NC}"

    local cc_ver
    cc_ver="$(cc --version 2>&1 | head -1)"
    ok "C compiler: $cc_ver (installed)"
}

# ─── Dependency: Go ──────────────────────────────────────────────────────────

install_go_brew() {
    if ! command_exists brew; then
        return 1
    fi
    info "Installing Go via Homebrew..."
    brew install go
    return $?
}

install_go_direct() {
    info "Fetching latest Go version from go.dev..."

    local go_version go_pkg go_url
    go_version=""

    # Try to get latest version from API
    if command_exists curl; then
        go_version=$(curl -fsSL "https://go.dev/dl/?mode=json" 2>/dev/null \
            | grep -o '"version":"go[0-9.]*"' \
            | head -1 \
            | cut -d'"' -f4) || true
    fi

    # Fallback version
    if [[ -z "$go_version" ]]; then
        go_version="go1.23.6"
        info "Could not query API, using $go_version"
    fi

    go_pkg="${go_version}.darwin-${GO_ARCH}.pkg"
    go_url="https://go.dev/dl/${go_pkg}"
    local tmp_pkg="/tmp/${go_pkg}"

    download_file "$go_url" "$tmp_pkg" "Downloading $go_version..." 20000000

    info "Installing $go_version (may require password)..."
    sudo installer -pkg "$tmp_pkg" -target / 2>/dev/null
    rm -f "$tmp_pkg"

    # Update PATH for this session
    export PATH="/usr/local/go/bin:$HOME/go/bin:$PATH"
}

ensure_go() {
    if command_exists go; then
        local go_ver
        go_ver="$(go version | grep -oE 'go[0-9]+\.[0-9]+(\.[0-9]+)?')"
        ok "Go $go_ver"
        return
    fi

    info "Go not found, installing..."

    # Try Homebrew first, then direct download
    if install_go_brew 2>/dev/null; then
        true
    else
        install_go_direct
    fi

    if ! command_exists go; then
        # Check common install locations
        for p in /usr/local/go/bin "$HOME/go/bin" /opt/homebrew/bin; do
            if [[ -x "$p/go" ]]; then
                export PATH="$p:$PATH"
                break
            fi
        done
    fi

    if ! command_exists go; then
        err "Go installed but not found in PATH."
        err "Add to your shell profile (~/.zshrc or ~/.bash_profile):"
        err "  export PATH=\"/usr/local/go/bin:\$PATH\""
        err "Then restart the terminal and run this script again."
        exit 1
    fi

    local go_ver
    go_ver="$(go version | grep -oE 'go[0-9]+\.[0-9]+(\.[0-9]+)?')"
    ok "Go $go_ver (installed)"
}

# ─── Dependency: .NET SDK 7.0 ────────────────────────────────────────────────

ensure_dotnet() {
    if command_exists dotnet; then
        local sdk_list
        sdk_list="$(dotnet --list-sdks 2>/dev/null || true)"
        if echo "$sdk_list" | grep -q '^7\.'; then
            local ver
            ver="$(echo "$sdk_list" | grep '^7\.' | head -1 | awk '{print $1}')"
            ok ".NET SDK $ver"
            return
        fi
        info ".NET SDK found, but version 7.x is missing"
    else
        info ".NET SDK not found, installing..."
    fi

    local install_script="/tmp/dotnet-install.sh"
    download_file "https://dot.net/v1/dotnet-install.sh" "$install_script" \
        "Downloading dotnet-install.sh..." 1024

    chmod +x "$install_script"
    info "Installing .NET SDK 7.0..."
    "$install_script" --channel 7.0 --install-dir "$HOME/.dotnet"
    rm -f "$install_script"

    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

    # Add to shell profile if not already there
    local shell_rc="$HOME/.zshrc"
    [[ -f "$HOME/.bash_profile" && ! -f "$HOME/.zshrc" ]] && shell_rc="$HOME/.bash_profile"

    if [[ -f "$shell_rc" ]] && ! grep -q 'DOTNET_ROOT' "$shell_rc" 2>/dev/null; then
        {
            echo ""
            echo "# .NET SDK"
            echo "export DOTNET_ROOT=\"\$HOME/.dotnet\""
            echo "export PATH=\"\$DOTNET_ROOT:\$DOTNET_ROOT/tools:\$PATH\""
        } >> "$shell_rc"
        info "Added .NET to $shell_rc"
    fi

    if ! command_exists dotnet; then
        err ".NET SDK installed but not found in PATH."
        err "Add to your shell profile:"
        err "  export DOTNET_ROOT=\"\$HOME/.dotnet\""
        err "  export PATH=\"\$DOTNET_ROOT:\$PATH\""
        exit 1
    fi

    local ver
    ver="$(dotnet --version 2>/dev/null)"
    ok ".NET SDK $ver (installed)"
}

# ─── Prerequisites check ─────────────────────────────────────────────────────

check_prerequisites() {
    step_header "Checking and installing dependencies"

    local need_go=false
    local need_dotnet=false

    case "$STEP" in
        all)
            need_go=true
            if ! $SKIP_DOTNET; then
                need_dotnet=true
            fi
            ;;
        go)     need_go=true ;;
        dotnet) need_dotnet=true ;;
        geo|bundle) ;; # No special deps needed
    esac

    if $need_go; then
        ensure_xcode_clt
        ensure_go
    fi

    if $need_dotnet; then
        ensure_dotnet
    fi

    ok "All dependencies ready"
}

# ─── Step 1: Build Go wrapper + CLI binary ────────────────────────────────────

build_go_wrapper() {
    step_header "Step 1: Build XRayCore (Go)"

    if [[ ! -d "$WRAPPER_DIR" ]]; then
        err "XRay-Wrapper directory not found: $WRAPPER_DIR"
        exit 1
    fi

    pushd "$WRAPPER_DIR" >/dev/null

    # 1a. Build shared library (XRayCore.dylib) for FFI/embedding
    info "Building XRayCore.dylib for $ARCH..."
    CGO_ENABLED=1 \
    GOOS=darwin \
    GOARCH="$GO_ARCH" \
    go build \
        --buildmode=c-shared \
        -o XRayCore.dylib \
        -trimpath \
        -ldflags "-s -w -buildid=" \
        .

    ok "XRayCore.dylib built ($(format_size "$(stat -f%z XRayCore.dylib 2>/dev/null || stat -c%s XRayCore.dylib)"))"

    mkdir -p "$LIBRARIES_DIR"
    cp XRayCore.dylib "$LIBRARIES_DIR/"
    ok "XRayCore.dylib → $LIBRARIES_DIR"
    rm -f XRayCore.h XRayCore.dylib

    # 1b. Build standalone CLI binary (gorilla-xray)
    info "Building gorilla-xray CLI binary for $ARCH..."
    CGO_ENABLED=1 \
    GOOS=darwin \
    GOARCH="$GO_ARCH" \
    go build \
        -o gorilla-xray \
        -trimpath \
        -ldflags "-s -w -buildid= -X main.version=$VERSION" \
        ./cmd/gorilla-xray/

    ok "gorilla-xray built ($(format_size "$(stat -f%z gorilla-xray 2>/dev/null || stat -c%s gorilla-xray)"))"

    popd >/dev/null
}

# ─── Step 2: Download geo databases ──────────────────────────────────────────

download_geo_files() {
    step_header "Step 2: Download geoip.dat and geosite.dat"

    local geoip_path="$APP_DIR/geoip.dat"
    local geosite_path="$APP_DIR/geosite.dat"

    download_file "$GEOIP_URL" "$geoip_path" "Downloading geoip.dat..."
    local geoip_size
    geoip_size="$(format_size "$(stat -f%z "$geoip_path" 2>/dev/null || stat -c%s "$geoip_path")")"
    ok "geoip.dat ($geoip_size)"

    download_file "$GEOSITE_URL" "$geosite_path" "Downloading geosite.dat..."
    local geosite_size
    geosite_size="$(format_size "$(stat -f%z "$geosite_path" 2>/dev/null || stat -c%s "$geosite_path")")"
    ok "geosite.dat ($geosite_size)"
}

# ─── Step 3: Build .NET application ──────────────────────────────────────────

build_dotnet_app() {
    if $SKIP_DOTNET; then
        info "Step 3: .NET build skipped (--skip-dotnet)"
        info "The WPF GUI (net7.0-windows) requires Windows."
        info "For macOS GUI, port the project to Avalonia UI or .NET MAUI."
        return 0
    fi

    if $PUBLISH; then
        step_header "Step 3: Publish .NET ($CONFIGURATION, $RUNTIME)"
    else
        step_header "Step 3: Build .NET ($CONFIGURATION)"
    fi

    if [[ ! -f "$SOLUTION_FILE" ]]; then
        err "Solution file not found: $SOLUTION_FILE"
        exit 1
    fi

    # Check if the project targets windows-only
    local csproj="$APP_DIR/InvisibleGorilla-XRay.csproj"
    if [[ -f "$csproj" ]] && grep -q 'net7.0-windows\|UseWPF' "$csproj"; then
        echo ""
        err "┌─────────────────────────────────────────────────────────┐"
        err "│  The .NET project uses WPF (net7.0-windows)            │"
        err "│  WPF is Windows-only and cannot build on macOS.        │"
        err "│                                                        │"
        err "│  To build a macOS GUI, the project needs porting to:   │"
        err "│    • Avalonia UI (https://avaloniaui.net)              │"
        err "│    • .NET MAUI   (https://dot.net/maui)               │"
        err "│                                                        │"
        err "│  The XRayCore.dylib + geo files are ready to use.     │"
        err "│  Use --skip-dotnet to suppress this message.           │"
        err "└─────────────────────────────────────────────────────────┘"
        echo ""
        return 0
    fi

    pushd "$SCRIPT_DIR" >/dev/null

    info "Restoring NuGet packages..."
    dotnet restore "$SOLUTION_FILE"
    ok "NuGet packages restored"

    if $PUBLISH; then
        local abs_output
        abs_output="$(mkdir -p "$OUTPUT_DIR" && cd "$OUTPUT_DIR" && pwd)"

        info "Publishing application..."
        dotnet publish "$SOLUTION_FILE" \
            -c "$CONFIGURATION" \
            -r "$RUNTIME" \
            --self-contained true \
            -o "$abs_output"
        ok "Published to: $abs_output"
    else
        info "Building application..."
        dotnet build "$SOLUTION_FILE" -c "$CONFIGURATION"
        ok "Application built ($CONFIGURATION)"
    fi

    popd >/dev/null
}

# ─── Step 4: Package distribution bundle ─────────────────────────────────────

package_bundle() {
    step_header "Step 4: Package distribution bundle"

    local bundle_dir="$SCRIPT_DIR/$DIST_DIR/InvisibleGorilla-XRay-macOS-$ARCH"
    rm -rf "$bundle_dir"
    mkdir -p "$bundle_dir/lib"

    local found_items=0

    # Copy CLI binary
    local cli_src="$WRAPPER_DIR/gorilla-xray"
    if [[ -f "$cli_src" ]]; then
        cp "$cli_src" "$bundle_dir/"
        chmod +x "$bundle_dir/gorilla-xray"
        ok "Bundled: gorilla-xray (CLI binary)"
        found_items=$((found_items + 1))
    else
        err "gorilla-xray not found — run build step 'go' first"
    fi

    # Copy XRayCore.dylib
    local dylib_src="$LIBRARIES_DIR/XRayCore.dylib"
    if [[ -f "$dylib_src" ]]; then
        cp "$dylib_src" "$bundle_dir/lib/"
        ok "Bundled: lib/XRayCore.dylib"
        found_items=$((found_items + 1))
    else
        err "XRayCore.dylib not found — run build step 'go' first"
    fi

    # Copy geo databases
    for dat in geoip.dat geosite.dat; do
        local dat_src="$APP_DIR/$dat"
        if [[ -f "$dat_src" ]]; then
            cp "$dat_src" "$bundle_dir/"
            ok "Bundled: $dat"
            found_items=$((found_items + 1))
        else
            err "$dat not found — run build step 'geo' first"
        fi
    done

    if (( found_items == 0 )); then
        err "No files to bundle. Run the full build first."
        rm -rf "$bundle_dir"
        return 1
    fi

    cat > "$bundle_dir/README.txt" <<EOF
Invisible Gorilla XRay Client — macOS Distribution
===================================================

Version: $VERSION
Architecture: $ARCH ($(uname -m))
Built: $(date '+%Y-%m-%d %H:%M:%S')
macOS: $(sw_vers -productVersion 2>/dev/null || echo 'unknown')

Contents:
  gorilla-xray        — Standalone CLI proxy client (run directly)
  lib/XRayCore.dylib  — XRay proxy core engine (c-shared library for FFI)
  geoip.dat           — IP geolocation routing database
  geosite.dat         — Domain routing database

Quick Start:
  # Start proxy with your xray config:
  ./gorilla-xray -config your-config.json

  # Start on a specific port with SOCKS5:
  ./gorilla-xray -config config.json -port 1080 -socks

  # Test connection latency:
  ./gorilla-xray -config config.json -test

  # Enable debug logging:
  ./gorilla-xray -config config.json -log-level debug -log-path ./logs

  # Show version:
  ./gorilla-xray -version

  # Show all options:
  ./gorilla-xray -help

macOS Proxy Setup:
  After starting gorilla-xray, configure macOS to use the proxy:

  # Enable HTTP proxy (System Preferences → Network → Proxies):
  networksetup -setwebproxy "Wi-Fi" 127.0.0.1 10801
  networksetup -setsecurewebproxy "Wi-Fi" 127.0.0.1 10801

  # Or use SOCKS5 proxy:
  networksetup -setsocksfirewallproxy "Wi-Fi" 127.0.0.1 1080

  # Disable when done:
  networksetup -setwebproxystate "Wi-Fi" off
  networksetup -setsecurewebproxystate "Wi-Fi" off

Library Usage (XRayCore.dylib):
  The shared library exports C functions for embedding:
    - StartServer(config, port, logLevel, logPath, isSocks, isUdpEnabled)
    - StopServer()
    - TestConnection(config, port) -> int (ping ms, or error code)
    - GetXrayCoreVersion() -> char*

  Load via dlopen() or equivalent FFI in your application.

Repository: https://github.com/hvkeyn/InvisibleGorilla-XRayClient
EOF
    ok "Bundled: README.txt"

    # Create archive
    local archive_name="InvisibleGorilla-XRay-macOS-${ARCH}-v${VERSION}.tar.gz"
    pushd "$SCRIPT_DIR/$DIST_DIR" >/dev/null
    tar -czf "$archive_name" "InvisibleGorilla-XRay-macOS-$ARCH"
    popd >/dev/null

    local archive_path="$SCRIPT_DIR/$DIST_DIR/$archive_name"
    local archive_size
    archive_size="$(format_size "$(stat -f%z "$archive_path" 2>/dev/null || stat -c%s "$archive_path")")"

    echo ""
    ok "Distribution bundle created:"
    echo -e "     ${DIM}Directory: $bundle_dir${NC}"
    echo -e "     ${DIM}Archive:   $archive_path ($archive_size)${NC}"
    echo ""
    echo -e "     ${DIM}Contents:${NC}"
    ls -lh "$bundle_dir/" | tail -n +2 | while read -r line; do
        echo -e "     ${DIM}  $line${NC}"
    done
    if [[ -d "$bundle_dir/lib" ]]; then
        ls -lh "$bundle_dir/lib/" | tail -n +2 | while read -r line; do
            echo -e "     ${DIM}  lib/$line${NC}"
        done
    fi
}

# ─── Main ─────────────────────────────────────────────────────────────────────

SECONDS=0

echo ""
echo -e "${MAGENTA}  Invisible Gorilla - XRay Client :: macOS Build Script${NC}"
echo -e "${DIM}  v${VERSION} | $(uname -m) | macOS $(sw_vers -productVersion 2>/dev/null || echo 'unknown')${NC}"
echo ""

# Validate step
case "$STEP" in
    all|go|geo|dotnet|bundle) ;;
    *)
        err "Unknown step: $STEP"
        err "Valid steps: all, go, geo, dotnet, bundle"
        exit 1
        ;;
esac

check_prerequisites

case "$STEP" in
    all)
        build_go_wrapper
        download_geo_files
        build_dotnet_app
        package_bundle
        ;;
    go)     build_go_wrapper ;;
    geo)    download_geo_files ;;
    dotnet) build_dotnet_app ;;
    bundle) package_bundle ;;
esac

elapsed=$SECONDS
mins=$((elapsed / 60))
secs=$((elapsed % 60))

echo ""
echo -e "${GREEN}============================================================${NC}"
printf "${GREEN}  Done in %d:%02d${NC}\n" "$mins" "$secs"
echo -e "${GREEN}============================================================${NC}"
echo ""
