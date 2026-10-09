#!/bin/bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Yukishiro
#
# Meltype (Linux 版テスト版) を外す。設定と学習データ (~/.local/share/Meltype) も消すなら --data を付ける:
#   bash uninstall.sh [--data]
set -euo pipefail
echo "Meltype を外します (管理者のパスワードを聞かれます)"
sudo rm -f /usr/share/ibus/component/meltype.xml
# fcitx5 のアドオン
sudo rm -f /usr/share/fcitx5/addon/meltype.conf /usr/share/fcitx5/inputmethod/meltype.conf
{ find /usr/lib /usr/lib64 -name meltype.so -path '*fcitx5*' 2> /dev/null || true; } | xargs -r sudo rm -f
if pgrep -x fcitx5 > /dev/null; then (fcitx5 -r -d > /dev/null 2>&1 &) ; fi
sudo rm -rf /opt/meltype
pkill -f ibus-engine-meltype 2> /dev/null || true
ibus write-cache 2> /dev/null || true
ibus restart 2> /dev/null || true
if [[ "${1:-}" == "--data" ]]; then
    rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/Meltype"
    echo "設定と学習データも消しました。"
fi
echo "外しました。入力ソースに Meltype が残っていたら、設定 → キーボード → 入力ソース から外してください。"
