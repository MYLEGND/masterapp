#!/bin/zsh
set -euo pipefail

project_dir="${0:A:h}/.."
project_path="$project_dir/Legend.xcodeproj"
derived_data_path="${TMPDIR:-/tmp}/legend-ios-derived"

xcodebuild -list -project "$project_path"
xcodebuild \
  -project "$project_path" \
  -scheme Legend \
  -configuration Debug \
  -sdk iphonesimulator \
  -derivedDataPath "$derived_data_path" \
  CODE_SIGNING_ALLOWED=NO \
  build-for-testing
