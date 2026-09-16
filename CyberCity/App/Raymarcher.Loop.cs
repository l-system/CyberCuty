using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace CyberCity.App;

public sealed partial class Raymarcher
{
    /// <summary>
    /// Which camera-relative direction each held movement key contributes.
    /// Port of raymarcher.py's KEY_ACTIONS + direction_map. The Python version
    /// builds a persistent "keys currently held" set from discrete KEYDOWN/KEYUP
    /// events, then reads that set once per frame. Silk.NET keyboards expose
    /// IsKeyPressed(key) as a direct per-frame poll of "is this key down right
    /// now", which is the same information with less bookkeeping — so movement
    /// here polls directly each frame instead of maintaining a mirrored set.
    /// </summary>
    private static readonly (Key Key, Func<Camera, Vector3> Direction)[] MovementBindings =
    {
        (Key.W, static c => c.Front),
        (Key.S, static c => -c.Front),
        (Key.A, static c => -c.Right),
        (Key.D, static c => c.Right),
        (Key.E, static c => c.WorldUp),
        (Key.Q, static c => -c.WorldUp),
    };

    /// <summary>Port of _update_camera.</summary>
    private void UpdateCamera(float deltaTime)
    {
        if (_primaryKeyboard is null)
        {
            return;
        }

        Vector3 movementDirection = Vector3.Zero;
        foreach (var (key, direction) in MovementBindings)
        {
            if (_primaryKeyboard.IsKeyPressed(key))
            {
                movementDirection += direction(_camera);
            }
        }

        if (movementDirection.LengthSquared() > 0)
        {
            movementDirection = Vector3.Normalize(movementDirection);
        }

        _camera.IsRunning = _primaryKeyboard.IsKeyPressed(Key.ShiftLeft) || _primaryKeyboard.IsKeyPressed(Key.ShiftRight);

        _camera.SetTargetMovementDirection(movementDirection);
        _camera.UpdateMovement(deltaTime);
        _camera.UpdateRoll(deltaTime);
        _camera.UpdateVectors();
    }

    /// <summary>
    /// Updates the window title with a rolling FPS average, refreshed twice a
    /// second rather than every frame — setting Title is a native window-manager
    /// call, not free, and a per-frame update would just add back the kind of
    /// needless per-frame work the last review pass removed.
    /// </summary>
    private void UpdateFrameCounter(double deltaTime)
    {
        _frameCount++;
        _fpsAccumulatedSeconds += deltaTime;

        const double updateIntervalSeconds = 0.5;
        if (_fpsAccumulatedSeconds < updateIntervalSeconds)
        {
            return;
        }

        double fps = _frameCount / _fpsAccumulatedSeconds;
        _window.Title = $"{WindowTitle} — {fps:F1} FPS";

        _frameCount = 0;
        _fpsAccumulatedSeconds = 0;
    }

    /// <summary>
    /// Port of _render, called once per frame. Only sets what actually changes
    /// frame to frame (time, camera position/target/up) — everything static
    /// (font-atlas metrics, texture bindings, FOV, IOR, initial resolution) is
    /// set once in BindStaticUniforms (Raymarcher.cs's OnLoad) instead; see that
    /// method's doc comment for why that's safe. Silk.NET's Render event already
    /// gives us a per-frame callback with a delta time, so — unlike the Python
    /// run() loop, which explicitly interleaves _handle_events / _update_camera /
    /// _render / display.flip / clock.tick — camera update and drawing are just
    /// done back to back here in the single per-frame callback.
    /// </summary>
    private void OnRender(double deltaTime)
    {
        UpdateFrameCounter(deltaTime);
        UpdateCamera((float)deltaTime);

        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        float elapsedSeconds = (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        _gl.Uniform1(_uniforms.ITime, elapsedSeconds);

        var (cameraPos, cameraTarget, cameraUp) = _camera.GetViewMatrix();
        _gl.Uniform3(_uniforms.CameraPosition, cameraPos);
        _gl.Uniform3(_uniforms.CameraTarget, cameraTarget);
        _gl.Uniform3(_uniforms.CameraUp, cameraUp);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Uploads a 16-element vec4 array in one call, matching Python's single
    /// glUniform4fv(location, 16, values) call. As with DirectoryTextureBuilder's
    /// TexStorage3D note: I couldn't independently verify this exact
    /// (location, count, float*) overload shape without a compiler on hand — it
    /// follows the same native-signature-mirroring pattern confirmed elsewhere in
    /// this port (TexImage2D), but double-check this call first if uniforms come
    /// through as zero/garbage at runtime.
    /// </summary>
    private unsafe void UploadVec4Array(int location, Vector4[] values)
    {
        Span<float> flattened = stackalloc float[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            flattened[i * 4 + 0] = values[i].X;
            flattened[i * 4 + 1] = values[i].Y;
            flattened[i * 4 + 2] = values[i].Z;
            flattened[i * 4 + 3] = values[i].W;
        }

        fixed (float* ptr = flattened)
        {
            _gl.Uniform4(location, (uint)values.Length, ptr);
        }
    }

    /// <summary>Port of the K_ESCAPE branch of _handle_events.</summary>
    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (key != Key.Escape)
        {
            return;
        }

        _mouseCaptured = !_mouseCaptured;
        _lastMousePosition = null; // avoid a large jump on re-capture

        foreach (var mouse in _input.Mice)
        {
            mouse.Cursor.CursorMode = _mouseCaptured ? CursorMode.Raw : CursorMode.Normal;
        }
    }

    /// <summary>
    /// Port of the MOUSEMOTION branch of _handle_events. Pygame delivers relative
    /// motion directly via event.rel; Silk.NET's MouseMove gives absolute cursor
    /// position, so the delta is computed by hand against the previous frame's
    /// position (the standard pattern for this in Silk.NET — see e.g. its own
    /// "2.2 - Camera" tutorial).
    /// </summary>
    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!_mouseCaptured)
        {
            return;
        }

        if (_lastMousePosition is not { } lastPosition)
        {
            _lastMousePosition = position;
            return;
        }

        Vector2 delta = position - lastPosition;
        _lastMousePosition = position;

        _camera.ProcessMouseMovement(delta.X, -delta.Y);
    }

    /// <summary>
    /// Keeps the GL viewport, cached width/height, and the shader's iResolution
    /// uniform all in sync on resize. Not present in the Python original since
    /// pygame's window there was fixed-size — Silk.NET's is resizable by default.
    /// This is the one BindStaticUniforms value that isn't truly "set once
    /// forever", just not something that needs updating every frame either.
    /// </summary>
    private void OnFramebufferResize(Vector2D<int> newSize)
    {
        _width = newSize.X;
        _height = newSize.Y;

        _gl.Viewport(newSize);

        _shaderProgram.Use();
        _gl.Uniform2(_uniforms.IResolution, (float)_width, (float)_height);
    }

    /// <summary>Port of _cleanup (pygame.quit() itself is handled by Dispose()).</summary>
    private void OnClosing()
    {
        _shaderProgram.Dispose();
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _fontAtlas.Dispose();
        _gl.DeleteTexture(_directoryTextureArrayId);
    }
}