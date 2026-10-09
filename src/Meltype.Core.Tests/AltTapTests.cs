// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Input;

namespace Meltype.Tests;

/// <summary>左右の Alt の単独押しで英数 / 日本語 (#85)。</summary>
internal static class AltTapTests
{
    private const int LAlt = VirtualKeys.LMenu, RAlt = VirtualKeys.RMenu;

    private static KeyEvent Down(int vk, bool injected = false) => new(vk, 0, false, false, injected, 0);
    private static KeyEvent Up(int vk, bool injected = false) => new(vk, 0, false, true, injected, 0);

    [Test]
    public static void AltTap_AloneSwitches()
    {
        var d = new AltTapDetector();
        Assert.Equal(AltTap.Pressed, d.OnKey(Down(LAlt)), "左 Alt を押した");
        Assert.Equal(AltTap.Tapped, d.OnKey(Up(LAlt)), "左 Alt の単独押し");
        Assert.Equal(AltTap.Pressed, d.OnKey(Down(RAlt)), "右 Alt を押した");
        Assert.Equal(AltTap.None, d.OnKey(Down(RAlt)), "押しっぱなしの繰り返しは続き");
        Assert.Equal(AltTap.Tapped, d.OnKey(Up(RAlt)), "右 Alt の単独押し (繰り返しの後も)");
    }

    [Test]
    public static void AltTap_CombinationDoesNotSwitch()
    {
        var d = new AltTapDetector();
        d.OnKey(Down(LAlt));
        d.OnKey(Down(VirtualKeys.Tab));
        d.OnKey(Up(VirtualKeys.Tab));
        Assert.Equal(AltTap.None, d.OnKey(Up(LAlt)), "Alt + Tab");

        // 他のキーの後の押しっぱなしの繰り返しで、また単独押しにならない。
        d.OnKey(Down(RAlt));
        d.OnKey(Down(VirtualKeys.Kanji));
        Assert.Equal(AltTap.None, d.OnKey(Down(RAlt)), "Alt + ` の後の繰り返し");
        Assert.Equal(AltTap.None, d.OnKey(Up(RAlt)), "Alt + ` (半角/全角)");

        Assert.Equal(AltTap.None, d.OnKey(Down(LAlt), othersHeld: true), "Ctrl を押したまま Alt");
        Assert.Equal(AltTap.None, d.OnKey(Up(LAlt)), "Ctrl + Alt");

        d.OnKey(Down(LAlt));
        d.OnKey(Down(RAlt));
        Assert.Equal(AltTap.None, d.OnKey(Up(RAlt)), "左右の Alt を両方押した");
        Assert.Equal(AltTap.None, d.OnKey(Up(LAlt)), "左右の Alt を両方押した (左)");

        Assert.Equal(AltTap.Pressed, d.OnKey(Down(LAlt)), "後の単独押しは効く");
        Assert.Equal(AltTap.Tapped, d.OnKey(Up(LAlt)), "後の単独押しは効く (離す)");
    }

    [Test]
    public static void AltTap_ClickAndInjectedDoNotSwitch()
    {
        var d = new AltTapDetector();
        d.OnKey(Down(LAlt));
        d.Cancel();
        Assert.Equal(AltTap.None, d.OnKey(Up(LAlt)), "Alt + クリック");

        Assert.Equal(AltTap.None, d.OnKey(Down(RAlt, injected: true)), "他のソフトが送った Alt");
        Assert.Equal(AltTap.None, d.OnKey(Up(RAlt, injected: true)), "他のソフトが送った Alt (離す)");

        // 修飾キーを離すだけなら組み合わせではない (Shift を離してから Alt を単独で押す、など)。
        d.OnKey(Down(RAlt));
        d.OnKey(Up(VirtualKeys.LShift));
        Assert.Equal(AltTap.Tapped, d.OnKey(Up(RAlt)), "他のキーを離しただけ");
    }
}
