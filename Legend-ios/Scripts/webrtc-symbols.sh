#!/bin/bash
# The upstream SwiftPM binary omits dSYMs; use its matching, checksum-pinned release.
set -euo pipefail

version=152.0.0
digest=7c99e7cc12b59e4db357581271b412e9ce031ce21a23590a909f7ae1f0b20f87
mode=${1:?Expected prepare or install}
cache=${2:?Expected symbol cache directory}
symbols="$cache/WebRTC.framework.dSYM"

case "$mode" in
  prepare)
    if [[ -f "$symbols/Contents/Resources/DWARF/WebRTC" ]]; then
      xcrun dwarfdump --uuid "$symbols"
      exit 0
    fi
    mkdir -p "$cache"
    staging=$(mktemp -d "$cache/download.XXXXXX")
    trap 'rm -rf "$staging"' EXIT
    curl --fail --location --retry 2 --connect-timeout 30 --max-time 600 \
      "https://github.com/stasel/WebRTC/releases/download/$version/WebRTC-M${version%%.*}-dSYM.zip" \
      -o "$staging/symbols.zip"
    actual=$(shasum -a 256 "$staging/symbols.zip" | awk '{print $1}')
    [[ "$actual" == "$digest" ]] || { echo 'error: WebRTC symbols checksum mismatch.' >&2; exit 1; }
    unzip -q "$staging/symbols.zip" 'WebRTC-ios-arm64.dSYM/*' -d "$staging"
    mv "$staging/WebRTC-ios-arm64.dSYM" "$symbols"
    ;;
  install)
    binary=${3:?Expected embedded framework binary}
    destination=${4:?Expected output dSYM directory}
    [[ -f "$symbols/Contents/Resources/DWARF/WebRTC" ]] || {
      echo 'error: WebRTC symbols missing. Archive using the shared Legend scheme to prepare them.' >&2
      exit 1
    }
    binary_ids=$(xcrun dwarfdump --uuid "$binary" | awk '{print $2, $3}' | sort)
    symbol_ids=$(xcrun dwarfdump --uuid "$symbols" | awk '{print $2, $3}' | sort)
    [[ -n "$binary_ids" && "$binary_ids" == "$symbol_ids" ]] || {
      echo 'error: WebRTC framework and dSYM UUIDs differ. Update the pinned publisher symbols for this package version.' >&2
      exit 1
    }
    # Keep these paths in sync with the build phase's declared outputs.
    # ditto creates randomly named temporary files that Xcode's sandbox denies.
    mkdir -p "$destination/Contents/Resources/DWARF" \
      "$destination/Contents/Resources/Relocations/aarch64"
    for relative in Contents/Info.plist Contents/Resources/DWARF/WebRTC \
      Contents/Resources/Relocations/aarch64/WebRTC.yml; do
      cat "$symbols/$relative" > "$destination/$relative"
    done
    echo "Verified WebRTC archive symbols: $binary_ids"
    ;;
  *) echo 'error: Expected prepare or install.' >&2; exit 1 ;;
esac
