// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Composition;
using Meltype.Config;
using Meltype.Input;

namespace Meltype.Tests;

/// <summary>区切りを点数で選ぶ (α版、設定「区切りを点数で選ぶ (α版)」) のテスト。</summary>
internal static class ScoredSegmentationTests
{
    private static readonly CompositionDetector Detector = CreateDetector();

    private static CompositionDetector CreateDetector()
    {
        var detector = CompositionDetector.CreateDefault();
        detector.SpellChecker = Detection.BuiltInWordChecker.Shared;
        detector.UseScoredSegmentation = () => true;
        return detector;
    }

    /// <summary>打って Enter で確定した文字列 (変換エンジンは使わないので、日本語はかなのまま)。</summary>
    private static string Type(string typed)
    {
        var session = new MeltypeSession(Detector, new CompositionTests.FakeConverter(), new CompositionOptions(), () => new Settings());
        var output = "";
        foreach (var character in typed + "\n")
        {
            var vk = character == '\n' ? VirtualKeys.Return : char.ToUpperInvariant(character);
            var result = session.HandleKey(vk, character == '\n' ? null : character, char.IsAsciiLetterUpper(character), false, false, false);
            foreach (var edit in result.Commits)
            {
                if (edit.DeleteBefore > 0) output = output[..(output.Length - Math.Min(edit.DeleteBefore, output.Length))];
                output += edit.Text;
            }
            if (!result.Consumed) output += character;
        }
        return output;
    }

    [Test]
    public static void ParticleHa_AfterEnglishWord()
    {
        // 英単語の最後の子音と は が 1 つの単位 (sha・tha・dha) になっても、英単語 + は に分ける。今までの区切りでは しゃ・てゃ・でゃ になっていた
        foreach (var (typed, expected) in new[] {
            ("soredemedalsha", "それでmedalsは"), ("tsukattepresidentha", "つかってpresidentは"), ("kinouhasnaredha", "きのうはsnaredは"),
            ("atodestraightsha", "あとでstraightsは") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void ParticleHa_KeepsJapaneseWords()
    {
        // よく使う日本語の語の中の しゃ は区切らない (dens|は・bus|は にしない)
        foreach (var (typed, expected) in new[] { ("densha", "でんしゃ"), ("shabushabu", "しゃぶしゃぶ"), ("kaisha", "かいしゃ"), ("kinouhadenshaninotta", "きのうはでんしゃにのった") })
            Assert.Equal(expected, Type(typed), typed);
    }

    [Test]
    public static void LongWord_BeatsTwoShortWords()
    {
        // 長い 1 語 (string・glowers) を、短い語の組 (most + ring) や前の日本語を巻き込んだ並び (buglowers) より優先する
        foreach (var (typed, expected) in new[] { ("kyoumostringkamo", "きょうもstringかも"), ("zenbuglowerswo", "ぜんぶglowersを"), ("macOSnoupdate", "macOSのupdate"), ("seeyoulater", "seeyoulater") })
            Assert.Equal(expected, Type(typed), typed);
    }
}
