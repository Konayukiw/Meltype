// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

import Foundation
import InputMethodKit

// テストだけの変換スタブ。実際の NativeCore / InputController / KeyMapping と一緒にコンパイルする。
final class MeltypeConverter {
    static let shared = MeltypeConverter()
    func clauses(for text: String, context: String?) -> [(reading: String, text: String)] { [] }
    func candidates(for text: String) -> [String] { [] }
}
var candidatesWindow: IMKCandidates? = nil

@main
enum BoundaryTests {
    static func main() throws {
        let core = NativeCore.shared
        let cases: [(String, String, Int32?, String?)] = [
            ("e", "\u{0301}\u{0300}", nil, "e"),
            ("ka", "\u{3099}\u{FE0F}", nil, "か"),
            ("ka", "\u{E0100}\u{E0101}", nil, "か"),
            ("e", "\u{0301}\u{0300}", 0x75, "え"),
            ("ka", "👩‍👩‍👧‍👦", nil, "か "),
            ("ka", "e\u{0301}", nil, "か "),
            ("@kuraido", "\u{0301}\u{0300}", nil, "@kuraido"),
            ("", "\u{0301}\u{0300}", nil, nil),
        ]
        for (raw, external, modeKey, expected) in cases {
            guard let session = core.createSession() else { fatalError("Native session creation failed") }
            defer { core.destroySession(session) }
            for scalar in raw.unicodeScalars {
                let vk: Int32 = (97...122).contains(scalar.value) ? Int32(scalar.value) - 32 : 0x07
                guard core.handleKey(session, vk: vk, character: Int32(scalar.value), modifiers: 0,
                                     before: nil, after: "world") != nil else { fatalError("Key returned NULL") }
            }
            if let modeKey {
                _ = core.handleKey(session, vk: modeKey, character: 0, modifiers: 0, before: nil, after: nil)
            }
            guard let result = core.commitBeforeExternalText(session, text: external) else { fatalError("Commit returned NULL") }
            precondition(result.view == nil, "Composition was not cleared")
            precondition(result.commits.count == (expected == nil ? 0 : 1), "Unexpected edits")
            if let expected {
                precondition(Array(result.commits[0].text.unicodeScalars) == Array(expected.unicodeScalars), "Unexpected committed scalars")
                precondition(result.commits[0].deleteBefore == 0, "Unexpected deletion")
            }
            print("PASS: \(raw.debugDescription) + \(external.debugDescription)")
        }
        print("8/8 Swift + NativeAOT boundary cases passed (converter stub; no IMK GUI)")
    }
}
