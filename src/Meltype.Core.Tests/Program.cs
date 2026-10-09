// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Tests;

/// <summary>
/// OS に依存しない部分 (Meltype.Core) のテスト。Windows 以外 (Mac・Linux) でも動く。
///   dotnet run --project src/Meltype.Core.Tests                 … すべてのテスト
///   dotnet run --project src/Meltype.Core.Tests -- Composition  … 名前に一致するテストだけ
///   dotnet run --project src/Meltype.Core.Tests -- --eval       … 品質テストの正解率と外れた例
///   dotnet run --project src/Meltype.Core.Tests -- --explain konnichiwa hello … 1 文字ずつの判定理由
/// Windows では src/Meltype.Tests から実行すると、Windows のスペルチェッカーを使い、Windows 専用のテストもまとめて流す。
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--eval":
                Quality.Print(Quality.Run());
                return 0;
            case "--repro":
                // 打ったキーをそのまま打ってみて、結果を JSON で返す (GitHub の bot が報告を再現するのに使う)。
                //   --repro nihongowohanasu [enter|space|none]
                Repro.Type(args.ElementAtOrDefault(1) ?? "", args.ElementAtOrDefault(2) ?? "enter");
                return 0;
            case "--segments":
                // 調べる用: 打った文字の単位と、英語 / 日本語の区間分け (Enter の前)
                {
                    var text = new Meltype.Composition.CompositionText(CompositionTests.Detector);
                    foreach (var c in args.ElementAtOrDefault(1) ?? "") text.Append(c);
                    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                    Console.WriteLine("units: " + string.Join(" ", text.Units.Select(u => $"{u.Raw}={u.Kana}")) + (text.Pending.Length > 0 ? $" +{text.Pending}" : ""));
                    Console.WriteLine("segments: " + string.Join(" | ", text.Segments(final: true).Select(s => (s.IsEnglish ? "EN:" : "JA:") + s.Raw)));
                }
                return 0;
            case "--eval-json":
                Checks.EvalJson(args[1]);
                return 0;
            case "--mixed-bench":
                // --mixed-bench 出力ファイル: 日本語の文の中に英単語を入れて打ち、英単語と日本語に正しく分かれるかを数える (区切りの点数化の試作の比較用)
                Checks.MixedBench(args[1]);
                return 0;
            case "--expect":
                // --expect 入力ファイル 出力ファイル (Pull Request のチェック用)
                Checks.Expect(args[1], args[2]);
                return 0;
            case "--henkan":
                Henkan.Run(string.Join(" ", args.Skip(1)));
                Henkan.Shutdown();
                return 0;
            case "--jht":
                // --jht 出てほしい文 [/ 読み] (japanese-henkan-test。精度の調べもの用)
                {
                    var text = string.Join(" ", args.Skip(1));
                    var slash = text.IndexOf(" / ", StringComparison.Ordinal);
                    Jht.Run(slash >= 0 ? text[..slash] : text, slash >= 0 ? text[(slash + 3)..] : null, null, Henkan.Keyboard, Henkan.EngineName);
                    Henkan.Shutdown();
                }
                return 0;
            case "--jht-batch":
                // 標準入力から 1 行に 1 つ「出てほしい文 [/ 読み]」を受け取り、1 行ずつ結果を返す (多くの文を続けて試すとき。起動の待ちを省く)
                Jht.Batch(null, Henkan.Keyboard, () => Henkan.EngineName);
                Henkan.Shutdown();
                return 0;
            case "--explain":
                if (Environment.GetEnvironmentVariable("MELTYPE_UTF8") == "1") Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                TestHost.Explain(args.Skip(1));
                return 0;
            default:
                return TestHost.Run([typeof(Program).Assembly], args.FirstOrDefault());
        }
    }
}
