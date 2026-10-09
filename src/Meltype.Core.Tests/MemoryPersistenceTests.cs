// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 0924haruto12

using Meltype.Composition;
using Meltype.Learning;

namespace Meltype.Tests;

internal static class MemoryPersistenceTests
{
    [Test]
    public static void MemoryFiles_ReadLegacySchemasAndPreserveEntriesAfterSaving()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meltype-memory-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var languagesPath = Path.Combine(directory, "languages.json");
            var conversionsPath = Path.Combine(directory, "conversions.json");
            var translationsPath = Path.Combine(directory, "translations.json");
            var modelPath = Path.Combine(directory, "model.json");
            // Count / Explicit の無い旧版の学習データも受け入れる。
            File.WriteAllText(languagesPath, "{\"sushi\":{\"English\":true,\"Used\":\"2026-10-07T00:00:00Z\"}}");
            File.WriteAllText(conversionsPath, "{\"すし\":{\"Text\":\"鮨\",\"Used\":\"2026-10-07T00:00:00Z\"}}");
            File.WriteAllText(translationsPath, "{\"すし\":{\"sushi\":2}}");
            File.WriteAllText(modelPath, "{\"version\":1,\"prefixes\":{\"sushi\":{\"japanese\":2,\"english\":1,\"lastUsed\":\"2026-10-07T00:00:00Z\"}}}");
            var languages = new LanguageMemory(languagesPath);
            var conversions = new ConversionHistory(conversionsPath);
            var translations = new TranslationHistory(translationsPath);
            var model = new UserModel(modelPath);
            Assert.Equal(true, languages.Get("sushi"));
            Assert.Equal("鮨", conversions.Get("すし"));
            Assert.Equal(2, translations.Get("すし").Single().Count);
            Assert.Equal(2, model.Get("sushi")!.Japanese);
            Assert.True(!File.Exists(modelPath + ".broken"), "有効な旧形式を壊れたファイルとして扱わない");

            languages.Remember("cafe", english: false, explicitChoice: true);
            conversions.Remember("くらいど", "クラウド");
            translations.Remember("すし", "sushi");
            model.Learn("konn", SessionOutcome.JapaneseAccepted, decidedEnglish: false);
            model.Save();

            var restoredLanguages = new LanguageMemory(languagesPath);
            var restoredConversions = new ConversionHistory(conversionsPath);
            Assert.Equal(true, restoredLanguages.Get("sushi"));
            Assert.Equal(false, restoredLanguages.Get("cafe"));
            Assert.Equal("鮨", restoredConversions.Get("すし"));
            Assert.Equal("クラウド", restoredConversions.Get("くらいど"));
            Assert.Equal(3, new TranslationHistory(translationsPath).Get("すし").Single().Count);
            var restoredModel = new UserModel(modelPath);
            Assert.Equal(2, restoredModel.Get("sushi")!.Japanese);
            Assert.Equal(1, restoredModel.Get("konn")!.Japanese);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
