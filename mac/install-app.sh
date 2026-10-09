#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 hrmcngs

# Prepare and verify the new bundle before stopping or replacing the current IME.
set -euo pipefail
source_app="${1:?Usage: install-app.sh source.app target.app}"
target_app="${2:?Usage: install-app.sh source.app target.app}"
[[ -d "$source_app" ]] || { echo "Missing app: $source_app" >&2; exit 1; }
mkdir -p "$(dirname "$target_app")"
# A hidden sibling of Input Methods avoids discovery and keeps renames on its volume.
stage="$(mktemp -d "$(dirname "$(dirname "$target_app")")/.meltype-install.XXXXXX")"
backup_ready=0
replacement_started=0
cleanup() {
    local result=$?
    trap - EXIT
    if [[ $result -ne 0 && -d "$stage/previous.app" ]]; then
        if [[ $backup_ready -ne 1 ]]; then
            echo "Backup move failed; preserve recovery files at $stage" >&2
            exit "$result"
        fi
        if [[ -e "$target_app" ]] && ! mv "$target_app" "$stage/failed.app"; then
            echo "Cannot remove partial installation; previous app preserved at $stage/previous.app" >&2
            exit "$result"
        fi
        if ! mv "$stage/previous.app" "$target_app"; then
            echo "Restore the previous app from $stage/previous.app" >&2
            exit "$result"
        fi
    elif [[ $result -ne 0 && $replacement_started -eq 1 && -e "$target_app" ]]; then
        if ! mv "$target_app" "$stage/failed.app"; then
            echo "Partial installation preserved at $target_app; recovery files at $stage" >&2
            exit "$result"
        fi
    fi
    for staged_app in "$stage/Meltype.app" "$stage/previous.app" "$stage/failed.app"; do
        /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$staged_app" 2>/dev/null || true
    done
    rm -rf "$stage"
    exit "$result"
}
trap cleanup EXIT
# Refuse a cross-volume switch before copying or stopping the existing IME.
stage_device="$(stat -f %d "$stage")"
target_device="$(stat -f %d "$(dirname "$target_app")")"
[[ "$stage_device" == "$target_device" ]] || {
    echo "Staging and installation must be on the same volume." >&2
    exit 1
}
ditto "$source_app" "$stage/Meltype.app"
codesign --verify --deep --strict "$stage/Meltype.app"
pkill -x Meltype 2>/dev/null || true
if [[ -e "$target_app" ]]; then
    mv "$target_app" "$stage/previous.app"
    backup_ready=1
fi
replacement_started=1
mv "$stage/Meltype.app" "$target_app"
codesign --verify --deep --strict "$target_app"
