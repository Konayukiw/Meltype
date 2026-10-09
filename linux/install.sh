#!/bin/bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Yukishiro
#
# Meltype (Linux 版テスト版) を入れる。zip を展開したフォルダーで実行する:
#   bash install.sh
# プログラムは /opt/meltype に、IBus のコンポーネントは /usr/share/ibus/component に入れる (管理者のパスワードを聞かれる)。
# fcitx5 を使っていれば、fcitx5 のアドオンも入れる (IBus が無ければ IBus の部品は入れない)。fcitx5 だけに入れるなら --fcitx5 を付ける。
set -euo pipefail
cd "$(dirname "$0")"
target=/opt/meltype

if [[ ! -f libMeltypeNative.so || ! -f ibus-engine-meltype ]]; then
    echo "libMeltypeNative.so が見つかりません。zip を展開したフォルダーで実行してください。" >&2
    exit 1
fi

# fcitx5 を使っているか (fcitx5 が入っていて、zip に fcitx5 のアドオンがある)。IBus が無く fcitx5 だけなら、IBus は入れない
use_fcitx5=false
use_ibus=true
if [[ -f fcitx5/meltype.so ]] && { command -v fcitx5 > /dev/null || [[ "${1:-}" == "--fcitx5" ]]; }; then
    use_fcitx5=true
    if [[ "${1:-}" == "--fcitx5" ]] || ! command -v ibus > /dev/null; then use_ibus=false; fi
fi

# IBus と、Python から IBus を使う部品 (Ubuntu の標準の画面なら、ふつうは入っている)
if $use_ibus; then
    missing=()
    command -v ibus > /dev/null || missing+=(ibus)
    python3 -c 'import gi; gi.require_version("IBus", "1.0"); from gi.repository import IBus' 2> /dev/null || missing+=(python3-gi gir1.2-ibus-1.0)
    if [[ ${#missing[@]} -gt 0 ]]; then
        echo "必要な部品を入れます: ${missing[*]}"
        sudo apt-get install -y "${missing[@]}"
    fi
fi

echo "$target に入れます (管理者のパスワードを聞かれます)"
sudo rm -rf "$target"
sudo mkdir -p "$target"
sudo cp -R libMeltypeNative.so ibus-engine-meltype icon.png mozc LICENSE THIRD-PARTY-NOTICES.md "$target/"
sudo chmod 755 "$target/ibus-engine-meltype" "$target/mozc/meltype_mozc_helper"
if $use_ibus; then
    sed "s|@DIR@|$target|g" meltype.xml | sudo tee /usr/share/ibus/component/meltype.xml > /dev/null
    # IBus に読み込み直させる (動いている Meltype も止まり、次に使うときに新しいものが起動する)
    ibus write-cache 2> /dev/null || true
    ibus restart 2> /dev/null || true
fi

if $use_fcitx5; then
    # fcitx5 のアドオンの置き場所 (fcitx5 の部品 libclassicui.so と同じフォルダー)
    classicui=$({ find /usr/lib /usr/lib64 -name libclassicui.so -path '*fcitx5*' 2> /dev/null || true; } | head -n 1)
    if [[ -z "$classicui" ]]; then
        echo "fcitx5 のアドオンの置き場所が見つかりません。fcitx5 には入れません。" >&2
        use_fcitx5=false
    else
        sudo cp fcitx5/meltype.so "$(dirname "$classicui")/meltype.so"
        sudo mkdir -p /usr/share/fcitx5/addon /usr/share/fcitx5/inputmethod
        sudo cp fcitx5/meltype-addon.conf /usr/share/fcitx5/addon/meltype.conf
        sed "s|@DIR@|$target|g" fcitx5/meltype.conf | sudo tee /usr/share/fcitx5/inputmethod/meltype.conf > /dev/null
        # 動いている fcitx5 に読み込み直させる
        if pgrep -x fcitx5 > /dev/null; then (fcitx5 -r -d > /dev/null 2>&1 &) ; fi
    fi
fi

echo
echo "インストールしました。"
if $use_ibus; then
    echo "IBus で使うときは、初めてのときは:"
    echo "  1. いったんログアウトしてログインし直す"
    echo "  2. 設定 → キーボード → 入力ソース →「+ 入力ソースを追加」→ 日本語 → Meltype を追加"
    echo "  3. 画面右上の入力ソースのメニュー (または Super + Space) で Meltype を選ぶ"
fi
if $use_fcitx5; then
    echo "fcitx5 で使うときは:"
    echo "  1. fcitx5 の設定 (fcitx5-configtool) →「入力メソッド」で Meltype を追加する (検索欄に Meltype と入れる)"
    echo "  2. Ctrl + Space などで Meltype に切り替える"
fi
