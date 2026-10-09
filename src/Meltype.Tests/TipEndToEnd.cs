// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 lnkiai

using System.Runtime.InteropServices;

namespace Meltype.Tests;

/// <summary>
/// 調査用: 登録した Meltype IME (TSF の DLL) を、このプロセスの入力欄で実際に動かして確かめる。
///   Meltype.Tests --tip-e2e
/// サーバー (TipServer) は別のスレッドで動かす (DLL は UI スレッドで同期的にサーバーを待つため)。
/// 打鍵は SendInput で送るので、数秒間このウィンドウが前面に出る。
/// </summary>
internal static class TipEndToEnd
{
    private static readonly Guid ClsidTextService = new("417D801B-A9BD-4C26-BD16-356A825A6998");
    private static readonly Guid ProfileGuid = new("21F643F4-72D5-4946-BF70-0A126AB52D05");
    private static readonly Guid ClsidProfiles = new("33C53A50-F456-4884-B049-85FD643ECFED");

    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        [PreserveSig]
        int ActivateProfile(uint profileType, ushort langid, ref Guid clsid, ref Guid profile, IntPtr hkl, uint flags);
    }

    private const uint TF_PROFILETYPE_INPUTPROCESSOR = 1;
    private const uint TF_IPPMF_FORPROCESS = 0x10000000;
    private const uint TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE = 0x00000004;

    public static int Run()
    {
        // サーバー: 別のスレッドで
        var ready = new ManualResetEventSlim();
        Control? serverInvoker = null;
        uint serverThreadId = 0;
        var serverThread = new Thread(() =>
        {
            var settings = new Config.Settings { Mode = Config.InputMode.Tsf, LiveConversion = false };
            var invoker = new Control();
            invoker.CreateControl();
            serverInvoker = invoker;
            serverThreadId = GetCurrentThreadId();
            using var service = new Composition.CompositionService(invoker, Composition.CompositionDetector.CreateDefault(), new Composition.CompositionOptions
            {
                UserDictionary = new Composition.UserDictionary(null),
                History = new Composition.ConversionHistory(null),
                Languages = new Composition.LanguageMemory(null),
                TranslationHistory = new Composition.TranslationHistory(null),
                Engine = () => Config.ConversionEngine.System,
            });
            using var server = new Tip.TipServer(invoker, service, () => settings);
            server.Start();
            ready.Set();
            Application.Run();
        });
        serverThread.SetApartmentState(ApartmentState.STA);
        serverThread.IsBackground = true;
        // MELTYPE_E2E_EXTERNAL_SERVER=1: このプロセスではサーバーを動かさず、常駐している Meltype.exe につなぐ
        if (Environment.GetEnvironmentVariable("MELTYPE_E2E_EXTERNAL_SERVER") != "1")
        {
            serverThread.Start();
            ready.Wait();
        }

        Application.EnableVisualStyles();
        var form = new Form { Text = "Meltype IME test", Width = 600, Height = 300, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        var box = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 14) };
        form.Controls.Add(box);
        var results = new List<(string Name, string Expected, string Actual)>();
        form.Shown += async (_, _) =>
        {
            try
            {
                var manager = (ITfInputProcessorProfileMgr)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidProfiles)!)!;
                var clsid = ClsidTextService;
                var profile = ProfileGuid;
                var hr = Environment.GetEnvironmentVariable("MELTYPE_E2E_NO_ACTIVATE") == "1" ? 0 :
                    manager.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, 0x0411, ref clsid, ref profile, IntPtr.Zero, TF_IPPMF_FORPROCESS | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
                Console.WriteLine($"ActivateProfile: 0x{hr:X8} (入力欄のスレッド {GetCurrentThreadId()}, サーバーのスレッド {serverThreadId})");
                // 背景のプロセスからは前面に出られないので、Alt を 1 回押したことにしてから前面に出す
                Send(0x12, false);
                Send(0x12, true);
                SetForegroundWindow(form.Handle);
                form.Activate();
                box.Focus();
                Console.WriteLine($"前面: {GetForegroundWindow() == form.Handle}");
                await Task.Delay(800);

                async Task Case(string name, string keys, string expected)
                {
                    box.Clear();
                    await Task.Delay(200);
                    TypeKeys(keys);
                    await Task.Delay(1200);
                    results.Add((name, expected, box.Text));
                }

                await Case("かな", "kyouha\n", "きょうは");
                await Case("漢字", "kyouhaiitenkidesune \n", "今日はいい天気ですね");
                await Case("英単語", "hello world", "hello world");
                await Case("混在", "kyouhagoogledekensaku \n", "今日はgoogleで検索");
                await Case("BackSpace", "kyouhaa\b\n", "きょうは");
                await Case("Esc", "kyouha\u001b", "");
                // 半角/全角 (§ で送る) で英数にすると、そのまま英字。もう一度押すと日本語に戻る
                await Case("半角/全角", "§kyouha§", "kyouha");
                await Case("日本語に戻る", "kyouha\n", "きょうは");
                // 確定したあと ← でキャレットを動かしてから英単語を打っても、前の文字を消して確定し直さない
                await Case("動かしたあとは直さない", "i\n←want ", "want い");

                if (ShotDirectory is { } directory)
                {
                    // 見た目の確認用: 打っている途中 / 変換中 (候補の一覧と意味) を画像にする
                    box.Clear();
                    box.AppendText("確定済みの文: ");
                    await Task.Delay(200);
                    TypeKeys("kyouhaiitenkidesune");
                    await Task.Delay(600);
                    Capture(form, Path.Combine(directory, "tip-typing.png"));
                    TypeKeys(" ");
                    await Task.Delay(500);
                    Capture(form, Path.Combine(directory, "tip-converting.png"));
                    await Task.Delay(2000);
                    Capture(form, Path.Combine(directory, "tip-meaning.png"));
                    TypeKeys("\u001b\u001b\u001b");
                    await Task.Delay(300);
                    box.Clear();
                    // よくある書き間違い: 「もしかして」の行
                    TypeKeys("buresureddo");
                    await Task.Delay(600);
                    Capture(form, Path.Combine(directory, "tip-suggestion.png"));
                    TypeKeys("\u001b\u001b\u001b");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"失敗: {ex}");
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        serverInvoker?.BeginInvoke(Application.ExitThread);
        var failed = 0;
        foreach (var (name, expected, actual) in results)
        {
            var ok = actual == expected;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}: 期待「{expected}」 実際「{actual}」");
        }
        return failed == 0 && results.Count > 0 ? 0 : 1;
    }

    public static string? ShotDirectory { get; set; }

    /// <summary>フォームとその下・右 (候補の一覧が出る所) を画面から切り取る。</summary>
    private static void Capture(Form form, string path)
    {
        var bounds = form.Bounds;
        var area = Rectangle.Intersect(new Rectangle(bounds.Left, bounds.Top, bounds.Width + 500, bounds.Height + 420), SystemInformation.VirtualScreen);
        using var bitmap = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        bitmap.Save(path);
        Console.WriteLine($"画像: {path}");
    }

    // ' ' = Space, '\n' = Enter, '\b' = BackSpace, \u001b = Esc
    private static void TypeKeys(string keys)
    {
        foreach (var c in keys)
        {
            var vk = c switch
            {
                ' ' => 0x20,
                '\n' => 0x0D,
                '\b' => 0x08,
                '\u001b' => 0x1B,
                '§' => 0xF3,
                '←' => 0x25,
                >= 'a' and <= 'z' => c - 'a' + 'A',
                _ => throw new ArgumentException($"送れない文字: {c}"),
            };
            Send((ushort)vk, false);
            Send((ushort)vk, true);
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }

    private static void Send(ushort vk, bool up)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (vk is >= 0x21 and <= 0x28 ? Native.KEYEVENTF_EXTENDEDKEY : 0) } },
        };
        Native.SendAll([input], "テストの打鍵");
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
