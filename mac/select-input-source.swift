// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 hrmcngs

import Carbon
import Foundation

let target = "io.github.yksr-melt.inputmethod.Meltype.Japanese"

func sourceID(_ source: TISInputSource) -> String {
    guard let value = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else {
        return ""
    }
    return String(describing: unsafeBitCast(value, to: CFTypeRef.self))
}

for _ in 0..<10 {
    let sources = TISCreateInputSourceList(nil, false).takeRetainedValue() as NSArray
    for item in sources {
        let source = item as! TISInputSource
        if sourceID(source) == target && TISSelectInputSource(source) == noErr {
            if let current = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
               sourceID(current) == target {
                print("Meltype selected.")
                exit(0)
            }
        }
    }
    Thread.sleep(forTimeInterval: 0.3)
}
fputs("Unable to select Meltype.\n", stderr)
exit(1)
