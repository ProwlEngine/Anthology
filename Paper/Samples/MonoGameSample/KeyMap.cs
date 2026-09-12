// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
using Microsoft.Xna.Framework.Input;

using Prowl.PaperUI;

namespace MonoGameSample;

/// <summary>
/// Static mapping from MonoGame <see cref="Keys"/> to Paper's <see cref="PaperKey"/>.
/// MonoGame's names differ from Paper's for digits, the numpad and the OEM/punctuation keys,
/// so the table is spelled out explicitly rather than parsed by name.
/// </summary>
internal static class KeyMap
{
    public static readonly (Keys, PaperKey)[] Entries =
    {
        // Letters
        (Keys.A, PaperKey.A), (Keys.B, PaperKey.B), (Keys.C, PaperKey.C), (Keys.D, PaperKey.D),
        (Keys.E, PaperKey.E), (Keys.F, PaperKey.F), (Keys.G, PaperKey.G), (Keys.H, PaperKey.H),
        (Keys.I, PaperKey.I), (Keys.J, PaperKey.J), (Keys.K, PaperKey.K), (Keys.L, PaperKey.L),
        (Keys.M, PaperKey.M), (Keys.N, PaperKey.N), (Keys.O, PaperKey.O), (Keys.P, PaperKey.P),
        (Keys.Q, PaperKey.Q), (Keys.R, PaperKey.R), (Keys.S, PaperKey.S), (Keys.T, PaperKey.T),
        (Keys.U, PaperKey.U), (Keys.V, PaperKey.V), (Keys.W, PaperKey.W), (Keys.X, PaperKey.X),
        (Keys.Y, PaperKey.Y), (Keys.Z, PaperKey.Z),

        // Number row
        (Keys.D0, PaperKey.Num0), (Keys.D1, PaperKey.Num1), (Keys.D2, PaperKey.Num2),
        (Keys.D3, PaperKey.Num3), (Keys.D4, PaperKey.Num4), (Keys.D5, PaperKey.Num5),
        (Keys.D6, PaperKey.Num6), (Keys.D7, PaperKey.Num7), (Keys.D8, PaperKey.Num8),
        (Keys.D9, PaperKey.Num9),

        // Function keys
        (Keys.F1, PaperKey.F1), (Keys.F2, PaperKey.F2), (Keys.F3, PaperKey.F3), (Keys.F4, PaperKey.F4),
        (Keys.F5, PaperKey.F5), (Keys.F6, PaperKey.F6), (Keys.F7, PaperKey.F7), (Keys.F8, PaperKey.F8),
        (Keys.F9, PaperKey.F9), (Keys.F10, PaperKey.F10), (Keys.F11, PaperKey.F11), (Keys.F12, PaperKey.F12),

        // Editing / navigation
        (Keys.Enter, PaperKey.Enter), (Keys.Escape, PaperKey.Escape), (Keys.Back, PaperKey.Backspace),
        (Keys.Tab, PaperKey.Tab), (Keys.Space, PaperKey.Space),
        (Keys.Insert, PaperKey.Insert), (Keys.Home, PaperKey.Home), (Keys.PageUp, PaperKey.PageUp),
        (Keys.Delete, PaperKey.Delete), (Keys.End, PaperKey.End), (Keys.PageDown, PaperKey.PageDown),
        (Keys.Right, PaperKey.Right), (Keys.Left, PaperKey.Left), (Keys.Down, PaperKey.Down), (Keys.Up, PaperKey.Up),
        (Keys.CapsLock, PaperKey.CapsLock), (Keys.PrintScreen, PaperKey.PrintScreen),
        (Keys.Scroll, PaperKey.ScrollLock), (Keys.Pause, PaperKey.Pause),

        // Punctuation / OEM keys
        (Keys.OemMinus, PaperKey.Minus), (Keys.OemPlus, PaperKey.Equals),
        (Keys.OemOpenBrackets, PaperKey.LeftBracket), (Keys.OemCloseBrackets, PaperKey.RightBracket),
        (Keys.OemPipe, PaperKey.Backslash), (Keys.OemSemicolon, PaperKey.Semicolon),
        (Keys.OemQuotes, PaperKey.Apostrophe), (Keys.OemTilde, PaperKey.Grave),
        (Keys.OemComma, PaperKey.Comma), (Keys.OemPeriod, PaperKey.Period), (Keys.OemQuestion, PaperKey.Slash),

        // Keypad
        (Keys.NumLock, PaperKey.NumLock), (Keys.Divide, PaperKey.KeypadDivide),
        (Keys.Multiply, PaperKey.KeypadMultiply), (Keys.Subtract, PaperKey.KeypadMinus),
        (Keys.Add, PaperKey.KeypadPlus), (Keys.Decimal, PaperKey.KeypadDecimal),
        (Keys.NumPad0, PaperKey.Keypad0), (Keys.NumPad1, PaperKey.Keypad1), (Keys.NumPad2, PaperKey.Keypad2),
        (Keys.NumPad3, PaperKey.Keypad3), (Keys.NumPad4, PaperKey.Keypad4), (Keys.NumPad5, PaperKey.Keypad5),
        (Keys.NumPad6, PaperKey.Keypad6), (Keys.NumPad7, PaperKey.Keypad7), (Keys.NumPad8, PaperKey.Keypad8),
        (Keys.NumPad9, PaperKey.Keypad9),

        // Modifiers
        (Keys.LeftControl, PaperKey.LeftControl), (Keys.LeftShift, PaperKey.LeftShift),
        (Keys.LeftAlt, PaperKey.LeftAlt), (Keys.LeftWindows, PaperKey.LeftSuper),
        (Keys.RightControl, PaperKey.RightControl), (Keys.RightShift, PaperKey.RightShift),
        (Keys.RightAlt, PaperKey.RightAlt), (Keys.RightWindows, PaperKey.RightSuper),
    };
}
