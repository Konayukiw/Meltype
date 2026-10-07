// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using System.Text;
using Meltype.Config;
using Meltype.Detection;
using Meltype.Input;

namespace Meltype.Composition;

/// <summary>入力欄への書き込み 1 回分。DeleteBefore 文字をキャレットの前から消してから Text を入れる (確定し直すとき以外は 0)。</summary>
public readonly record struct TextEdit(int DeleteBefore, string Text);

/// <summary>
/// 1 回のキー入力の結果。Consumed が false ならそのキーはアプリにそのまま渡す (Commits を入れた後で)。
/// View は変換ボックスの内容 (null なら変換ボックスを閉じる)。
/// </summary>
public sealed record SessionResult(bool Consumed, IReadOnlyList<TextEdit> Commits, CompositionView? View)
{
    /// <summary>Swift などから読みやすいように JSON にする (NativeAOT でも使えるよう手書き)。</summary>
    public string ToJson()
    {
        var builder = new StringBuilder();
        builder.Append("{\"consumed\":").Append(Consumed ? "true" : "false").Append(",\"commits\":[");
        for (var i = 0; i < Commits.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append("{\"deleteBefore\":").Append(Commits[i].DeleteBefore).Append(",\"text\":");
            AppendString(builder, Commits[i].Text);
            builder.Append('}');
        }
        builder.Append("],\"view\":");
        if (View is not { } view)
        {
            builder.Append("null}");
            return builder.ToString();
        }
        builder.Append("{\"text\":");
        AppendString(builder, view.Text);
        builder.Append(",\"converting\":").Append(view.Converting ? "true" : "false");
        builder.Append(",\"selectedIndex\":").Append(view.SelectedIndex);
        builder.Append(",\"selectedClause\":").Append(view.SelectedClause);
        builder.Append(",\"hint\":");
        AppendString(builder, view.Hint);
        builder.Append(",\"candidates\":");
        AppendArray(builder, view.Candidates);
        builder.Append(",\"clauses\":");
        AppendArray(builder, view.Clauses ?? []);
        // 選んでいる候補の意味 (無ければ null)。少し止まってから出すのは Swift・Python 側
        builder.Append(",\"suggestion\":");
        if (view.Suggestion is { } suggestion) AppendString(builder, suggestion);
        else builder.Append("null");
        builder.Append(",\"meaning\":");
        if (view.Meaning is { } meaning) AppendString(builder, meaning);
        else builder.Append("null");
        builder.Append("}}");
        return builder.ToString();
    }

    private static void AppendArray(StringBuilder builder, IReadOnlyList<string> items)
    {
        builder.Append('[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) builder.Append(',');
            AppendString(builder, items[i]);
        }
        builder.Append(']');
    }

    private static void AppendString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}

/// <summary>
/// OS の正式な IME の仕組み (Mac の Input Method Kit、Linux の IBus / fcitx5) から使う、Meltype の入力の本体。
/// Windows 版はキーボードフックで打鍵を横取りするが、正式な IME では OS がキーを 1 つずつ渡してきて、
/// 「使ったか (アプリに渡さないか)」をその場で返す。そのやり取りを同期的に行う。
///
/// キーは Windows の仮想キーコード (A-Z = 0x41-0x5A、Space = 0x20 …) で渡す。入力する文字は ch で渡す (キーボード配列の違いは OS 側で解決済み)。
/// 変換ボックスの表示・確定する文字は、戻り値の <see cref="SessionResult"/> で返す。1 つのスレッドから使う。
/// </summary>
public sealed class MeltypeSession
{
    private readonly CaptureGate _gate;
    private readonly CompositionController _controller;
    private readonly Host _host = new();
    private readonly Func<Settings> _settings;

    public MeltypeSession(CompositionDetector detector, IKanjiConverter converter, CompositionOptions options, Func<Settings> settings)
    {
        _settings = settings;
        _gate = new CaptureGate(() => { });
        _controller = new CompositionController(_gate, detector, converter, _host, options);
    }

    /// <summary>
    /// 既定の辞書・学習データ (保存場所は <see cref="AppPaths"/>) で作る。converter は OS 側の変換エンジン、
    /// moreCandidates は読みに対する候補の一覧 (無ければ null)、wordChecker は OS のスペルチェッカー (無ければ null)。
    /// </summary>
    public static MeltypeSession CreateDefault(IKanjiConverter converter, Func<string, IReadOnlyList<string>>? moreCandidates, IWordChecker? wordChecker)
    {
        AppPaths.MigrateFromOldName();
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var settings = Settings.Load(AppPaths.ConfigFile);
        // 設定で「ファイルにログを書く」を ON にしていれば、Mac でも meltype.log に書く (動かないときの調査用)。
        Diagnostics.Log.SetFileOutput(settings.FileLog ? AppPaths.LogFile : null);
        Diagnostics.Log.RecordText = settings.LogTypedText;
        var userDirectory = AppPaths.UserDictionaryDirectory;
        var detector = CompositionDetector.CreateDefault(userDirectory);
        // OS のスペルチェッカーが無ければ (Linux)、同梱のよく使う英単語の一覧を使う (meeting を英語と分かるように)。
        detector.SpellChecker = wordChecker is { IsAvailable: true } ? wordChecker : Detection.BuiltInWordChecker.Shared;
        var languages = new LanguageMemory(AppPaths.LanguageMemoryFile);
        detector.Memory = languages;
        var options = new CompositionOptions
        {
            LiveConversion = () => settings.LiveConversion,
            AutoCorrect = () => settings.AutoCorrectAfterCommit && settings.DetectionLevel != DetectionLevel.Manual,
            Level = () => settings.DetectionLevel,
            Candidates = CandidateDictionary.Load(userDirectory),
            ContextRules = ContextRules.Load(userDirectory),
            History = new ConversionHistory(AppPaths.ConversionHistoryFile),
            UserDictionary = new UserDictionary(AppPaths.UserDictionaryFile),
            MoreCandidates = moreCandidates,
            Misspellings = MisspellingDictionary.Load(userDirectory),
            Languages = languages,
            Translations = TranslationDictionary.Load(),
            TranslationCandidates = () => settings.TranslationCandidates,
            Meanings = MeaningDictionary.Load(),
            CandidateMeanings = () => settings.ShowCandidateMeanings,
            RomajiTypos = RomajiTypoCorrector.Load(detector.Romaji),
            CorrectTypos = () => settings.CorrectTypos,
            SpaceAroundEnglish = () => settings.SpaceAroundEnglish,
            TranslationHistory = new TranslationHistory(AppPaths.TranslationHistoryFile),
        };
        return new MeltypeSession(detector, converter, options, () => settings);
    }

    /// <summary>英数 (直接入力) か。true の間はキーをすべてアプリに渡す (Mac の「英数」キー、「かな」キーで戻す)。</summary>
    public bool Direct { get; set; }

    /// <summary>変換ボックスに何か入っているか。</summary>
    public bool IsComposing => _controller.IsComposing;

    /// <summary>
    /// キーを 1 つ処理する。before / after は入力欄のキャレットの前後の文字列 (分かれば。英語とも日本語とも読める語の判定と変換の文脈に使う)。
    /// </summary>
    public SessionResult HandleKey(int vk, char? ch, bool shift, bool control, bool alt, bool command, string? before = null, string? after = null)
    {
        _host.Begin(ch, shift, before, after);
        var down = new KeyEvent(vk, ch ?? 0, false, false, false, Environment.TickCount64);
        // Ctrl・Option・Command と一緒のキーは、変換ボックスが空ならアプリの操作 (コピーなど) なので触らない。
        var modifier = control || alt || command;
        if (Direct || !_settings().Enabled)
        {
            return _host.Result(consumed: false);
        }
        // 英数へ切り替えるときなどに、Shift を押したことを変換ボックスにも伝える (Shift + 英字は大文字)。
        if (shift && _controller.IsComposing) Feed(new KeyEvent(VirtualKeys.LShift, 0, false, false, false, down.TimeMs));
        if (modifier && _controller.IsComposing) Feed(new KeyEvent(control ? VirtualKeys.LControl : VirtualKeys.LMenu, 0, false, false, false, down.TimeMs));

        var swallowed = Feed(down, e => !modifier && StartsComposition(e, ch, shift));
        // このキーをアプリに送り直した (= 使わなかった) なら、アプリに渡す。
        var consumed = swallowed && !_host.ReplayedCurrent;
        Feed(down with { IsUp = true });
        if (modifier && _controller.IsComposing) Feed(new KeyEvent(control ? VirtualKeys.LControl : VirtualKeys.LMenu, 0, false, true, false, down.TimeMs));
        if (shift && _controller.IsComposing) Feed(new KeyEvent(VirtualKeys.LShift, 0, false, true, false, down.TimeMs));
        return _host.Result(consumed);
    }

    /// <summary>フォーカスが外れたときなど。未確定の内容をそのまま確定する。</summary>
    public SessionResult CommitPending()
    {
        _host.Begin(null, false, null, null);
        _controller.CommitPending();
        _controller.ResetContext();
        return _host.Result(consumed: true);
    }

    /// <summary>候補ウィンドウで候補をクリックしたとき。</summary>
    public SessionResult SelectCandidate(int index)
    {
        _host.Begin(null, false, null, null);
        _controller.SelectCandidate(index);
        return _host.Result(consumed: true);
    }

    private bool Feed(KeyEvent e, Func<KeyEvent, bool>? starts = null)
    {
        var swallowed = _gate.OnKey(e, starts ?? (_ => false));
        _controller.Pump();
        return swallowed;
    }

    /// <summary>変換ボックスを開くキーか (Windows 版の MeltypeEngine.StartsComposition と同じ考え方)。</summary>
    private static bool StartsComposition(KeyEvent e, char? ch, bool shift)
    {
        if (ch is not { } c) return false;
        if (VirtualKeys.IsLetter(e.Vk) && char.IsAsciiLetter(c)) return true;
        // 句読点・かぎかっこ・長音・数字・記号 (Shift で打つものも)
        return CompositionController.StartsWithSymbol(c);
    }

    /// <summary>変換ボックスからの指示を集めて、1 回のキー入力の結果にまとめる。</summary>
    private sealed class Host : ICompositionHost
    {
        private readonly List<TextEdit> _commits = [];
        private int _pendingDelete;
        private char? _char;
        private bool _shift;
        private string? _before, _after;
        private CompositionView? _view;
        private bool _hidden;

        public bool ReplayedCurrent { get; private set; }

        public void Begin(char? ch, bool shift, string? before, string? after)
        {
            _commits.Clear();
            _pendingDelete = 0;
            _char = ch;
            _shift = shift;
            _before = before;
            _after = after;
            ReplayedCurrent = false;
            _hidden = false;
        }

        public SessionResult Result(bool consumed)
        {
            if (_pendingDelete > 0) _commits.Add(new TextEdit(_pendingDelete, ""));
            _pendingDelete = 0;
            return new SessionResult(consumed, _commits.ToList(), _hidden ? null : _view);
        }

        public void CommitText(string text)
        {
            _commits.Add(new TextEdit(_pendingDelete, text));
            _pendingDelete = 0;
        }

        public void DeleteBackward(int count) => _pendingDelete += count;

        public void Replay(KeyEvent e)
        {
            // 送り直すのは「今処理しているキー」(押したとき)。修飾キーやキーを離したことは、OS がアプリに渡すので何もしない。
            if (e.IsDown && !VirtualKeys.IsModifier(e.Vk)) ReplayedCurrent = true;
        }

        public void Replay(MouseButtonEvent e)
        {
        }

        public char? CharFromKey(KeyEvent e, bool shift) => e.Scan is > 0 and < 0x10000 ? (char)e.Scan : null;

        public bool IsShiftDown() => _shift;

        /// <summary>入力欄の文字は、キーを渡されたその場で読んでもらっている (OS が渡してきた値をそのまま使う)。</summary>
        public bool SurroundingTextIsCurrent => true;

        public void RequestSurroundingText(Action<string?, string?> callback) => callback(_before, _after);

        public void Show(CompositionView view)
        {
            _view = view;
            _hidden = false;
        }

        public void Hide()
        {
            _view = null;
            _hidden = true;
        }
    }
}
