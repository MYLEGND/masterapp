#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
platform=${1:-both}
if [ "$#" -gt 1 ]; then
    printf 'Usage: legend-release [both|android|ios]\n' >&2
    exit 2
fi
case "$platform" in
    both|android|ios) ;;
    *) printf 'Usage: legend-release [both|android|ios]\n' >&2; exit 2 ;;
esac

if [ "$platform" != android ]; then
    /usr/bin/xcodebuild -version >/dev/null
fi

if [ "$platform" != ios ]; then
    "$root/Legend-Android/release"
fi

if [ "$platform" != android ]; then
    ios_build=$(python3 "$root/scripts/reserve-ios-build.py")
    archive_dir="$HOME/Library/Developer/Xcode/Archives/$(date +%Y-%m-%d)"
    archive_path="$archive_dir/Legend-build$ios_build-$(date +%H%M%S)-$$.xcarchive"
    mkdir -p "$archive_dir"
    /usr/bin/xcodebuild \
        -project "$root/Legend-ios/Legend.xcodeproj" \
        -scheme Legend \
        -configuration Release \
        -destination 'generic/platform=iOS' \
        -archivePath "$archive_path" \
        -allowProvisioningUpdates \
        CURRENT_PROJECT_VERSION="$ios_build" \
        archive
    printf '\niOS archive: %s\nFind it in Xcode: Window > Organizer > Archives.\n' "$archive_path"
fi

printf '\nRequested builds completed. No store upload was performed.\n'
