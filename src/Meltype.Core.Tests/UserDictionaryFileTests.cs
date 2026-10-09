// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Text;
using Meltype.Composition;

namespace Meltype.Tests;

/// <summary>ユーザー辞書の取り込み・書き出し (ほかの日本語入力からの乗り換え)。</summary>
internal static class UserDictionaryFileTests
{
    [Test]
    public static void Import_MicrosoftImeAndGoogleFormats()
    {
        // Microsoft IME の「一覧の出力」(UTF-16 LE、BOM 付き)
        var msime = "!Microsoft IME Dictionary Tool\r\n!Version:\r\n!Format:WORDLIST\r\n\r\nゆきしろ\t雪代\t人名\r\nメルタイプ\tMeltype\t固有名詞\r\nあ\t亜\t名詞\r\n";
        var result = UserDictionaryFile.Parse([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(msime)]);
        Assert.Equal("UTF-16", result.Encoding);
        Assert.Equal(2, result.Words.Count, "1 文字の読み (あ) は飛ばす");
        Assert.Equal(new UserWord("めるたいぷ", "Meltype"), result.Words[1], "カタカナの読みはひらがなにする");
        Assert.Equal(1, result.Skipped);

        // Google 日本語入力のエクスポート (UTF-8、BOM なし、4 列)
        var google = Encoding.UTF8.GetBytes("# コメント\nきごうとう\t記号等\t名詞\t\nkaomoji\t(^^)\t顔文字\tコメント\n");
        var g = UserDictionaryFile.Parse(google);
        Assert.Equal("UTF-8", g.Encoding);
        Assert.Equal(new UserWord("きごうとう", "記号等"), g.Words[0]);

        // BOM の無い UTF-16
        Assert.Equal(1, UserDictionaryFile.Parse(Encoding.Unicode.GetBytes("ゆきしろ\t雪代\t名詞\r\n")).Words.Count, "BOM の無い UTF-16");
    }

    [Test]
    public static void Export_RoundTrips_AndSkipsDuplicates()
    {
        var dictionary = new UserDictionary(null, builtIn: false);
        Assert.Equal(2, dictionary.AddRange([new UserWord("ゆきしろ", "雪代"), new UserWord("めるたいぷ", "Meltype"), new UserWord("ゆきしろ", "雪代")]), "同じ語は 1 回だけ");
        var bytes = UserDictionaryFile.Export(dictionary.Words);
        Assert.True(bytes is [0xFF, 0xFE, ..], "Microsoft IME と同じ UTF-16 LE (BOM 付き)");
        var back = UserDictionaryFile.Parse(bytes);
        Assert.Equal(2, back.Words.Count, "書き出したものを取り込める");
        Assert.Equal(0, dictionary.AddRange(back.Words), "登録済みの語は増やさない");
    }

    // macOS の「ユーザ辞書」から Finder にドラッグして書き出した plist (#40)
    private const string MacPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
        	<dict>
        		<key>phrase</key>
        		<string>雪代</string>
        		<key>shortcut</key>
        		<string>ゆきしろ</string>
        	</dict>
        	<dict>
        		<key>phrase</key>
        		<string>Meltype</string>
        		<key>shortcut</key>
        		<string>メルタイプ</string>
        	</dict>
        	<dict>
        		<key>phrase</key>
        		<string>亜</string>
        		<key>shortcut</key>
        		<string>あ</string>
        	</dict>
        	<dict>
        		<key>phrase</key>
        		<string>1 行目
        2 行目</string>
        		<key>shortcut</key>
        		<string>ふくすう</string>
        	</dict>
        </array>
        </plist>
        """;

    [Test]
    public static void Import_MacPlist()
    {
        var result = UserDictionaryFile.Parse(Encoding.UTF8.GetBytes(MacPlist));
        Assert.Equal("plist", result.Encoding);
        Assert.Equal(2, result.Words.Count, "1 文字の読み・複数行の定型文は飛ばす");
        Assert.Equal(new UserWord("ゆきしろ", "雪代"), result.Words[0]);
        Assert.Equal(new UserWord("めるたいぷ", "Meltype"), result.Words[1], "カタカナの読みはひらがなにする");
        Assert.Equal(2, result.Skipped);
        Assert.True(UserDictionaryFile.Parse([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(MacPlist)]).Words.Count == 2, "BOM 付きでも読む");
        Assert.True(Throws(() => UserDictionaryFile.Parse(Encoding.ASCII.GetBytes("bplist00\0\0"))), "バイナリの plist は読めないと伝える");
    }

    [Test]
    public static void UserDictionary_ReadsPlistInFolder_AndKeepsUserdict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"meltype-plist-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "userdict.txt");
            File.WriteAllText(path, "ゆきしろ\t雪城\nかいしゃ\t会社名\n");
            var folder = Path.Combine(directory, "dictionaries");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ユーザ辞書.plist"), MacPlist);
            File.WriteAllBytes(Path.Combine(folder, "binary.plist"), Encoding.ASCII.GetBytes("bplist00"));
            var dictionary = new UserDictionary(path, builtIn: false, importDirectory: folder);
            Assert.Equal(2, dictionary.Count, "userdict.txt の語だけを数える (.plist の語は保存しない)");
            Assert.Equal(2, dictionary.ImportedCount, "読めない plist は飛ばす");
            Assert.Equal("雪城,雪代", string.Join(",", dictionary.Lookup("ゆきしろ")), "userdict.txt の登録が先");
            Assert.Equal("Meltype", dictionary.Lookup("めるたいぷ")[0]);
            Assert.Equal("会社名", dictionary.Lookup("かいしゃ")[0]);
            dictionary.Add("てすと", "テスト");
            var saved = File.ReadAllText(path);
            Assert.True(saved.Contains("かいしゃ\t会社名") && !saved.Contains("めるたいぷ"), "保存は userdict.txt の語だけ");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
}
