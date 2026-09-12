// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

using Prowl.PaperUI;
using Prowl.Quill;

using Shared;

namespace MonoGameSample;

/// <summary>
/// Hosts the shared Paper demo inside a MonoGame <see cref="Game"/>, wiring MonoGame's
/// window, input and render loop to the Paper UI runtime.
/// </summary>
public sealed class PaperGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private MonoGameCanvasRenderer _renderer = null!;
    private Paper _paper = null!;

    private KeyboardState _prevKeyboard;
    private MouseState _prevMouse;

    public PaperGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1080,
            PreferredBackBufferHeight = 850,
        };
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
    }

    protected override void Initialize()
    {
        Window.Title = "MonoGame Sample";
        base.Initialize();
    }

    protected override void LoadContent()
    {
        var canvasEffect = Content.Load<Effect>("CanvasShader");
        _renderer = new MonoGameCanvasRenderer(GraphicsDevice, canvasEffect);

        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;

        _paper = new Paper(_renderer, width, height, new FontAtlasSettings());
        _paper.SetClipboardHandler(new InMemoryClipboardHandler());

        // Loads the demo fonts and other resources.
        PaperDemo.Initialize(_paper);

        Window.ClientSizeChanged += OnClientSizeChanged;
        Window.TextInput += OnTextInput;

        _prevKeyboard = Keyboard.GetState();
        _prevMouse = Mouse.GetState();
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        int width = Math.Max(1, Window.ClientBounds.Width);
        int height = Math.Max(1, Window.ClientBounds.Height);

        _graphics.PreferredBackBufferWidth = width;
        _graphics.PreferredBackBufferHeight = height;
        _graphics.ApplyChanges();

        _renderer.UpdateProjection(width, height);
        _paper.SetResolution(width, height);
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        // Skip control characters; text fields handle backspace/enter via key state.
        if (!char.IsControl(e.Character))
            _paper.AddInputCharacter(e.Character.ToString());
    }

    protected override void Update(GameTime gameTime)
    {
        UpdateMouse();
        UpdateKeyboard();
        base.Update(gameTime);
    }

    private void UpdateMouse()
    {
        var mouse = Mouse.GetState();

        // Always report movement so hover state stays live.
        _paper.SetPointerState(PaperMouseBtn.Unknown, mouse.X, mouse.Y, false, true);

        UpdateMouseButton(PaperMouseBtn.Left, mouse.LeftButton, _prevMouse.LeftButton, mouse.X, mouse.Y);
        UpdateMouseButton(PaperMouseBtn.Right, mouse.RightButton, _prevMouse.RightButton, mouse.X, mouse.Y);
        UpdateMouseButton(PaperMouseBtn.Middle, mouse.MiddleButton, _prevMouse.MiddleButton, mouse.X, mouse.Y);

        // MonoGame accumulates the wheel value in steps of 120 per notch.
        int wheelDelta = mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheelDelta != 0)
            _paper.SetPointerWheel(wheelDelta / 120f);

        _prevMouse = mouse;
    }

    private void UpdateMouseButton(PaperMouseBtn button, ButtonState current, ButtonState previous, int x, int y)
    {
        if (current == ButtonState.Pressed && previous == ButtonState.Released)
        {
            _paper.SetPointerState(button, x, y, true, false);
        }
        else if (current == ButtonState.Released && previous == ButtonState.Pressed)
        {
            _paper.SetPointerState(button, x, y, false, false);
        }
    }

    private void UpdateKeyboard()
    {
        var keyboard = Keyboard.GetState();

        foreach (var (xnaKey, paperKey) in KeyMap.Entries)
        {
            bool down = keyboard.IsKeyDown(xnaKey);
            bool wasDown = _prevKeyboard.IsKeyDown(xnaKey);

            if (down && !wasDown)
            {
                _paper.SetKeyState(paperKey, true);
            }
            else if (!down && wasDown)
            {
                _paper.SetKeyState(paperKey, false);
            }
        }

        _prevKeyboard = keyboard;
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(40, 43, 48));

        float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _paper.BeginFrame(deltaTime, 1.0f);

        PaperDemo.RenderUI();

        _paper.EndFrame();

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _renderer.Dispose();

        base.UnloadContent();
    }

    /// <summary>
    /// MonoGame has no cross-platform clipboard, so the demo uses a simple in-process buffer.
    /// Copy/paste works within the app but not with the OS clipboard.
    /// </summary>
    private sealed class InMemoryClipboardHandler : IClipboardHandler
    {
        private string _text = string.Empty;
        public string GetClipboardText() => _text;
        public void SetClipboardText(string text) => _text = text ?? string.Empty;
    }
}
