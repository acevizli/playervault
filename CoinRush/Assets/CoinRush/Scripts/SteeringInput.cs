using UnityEngine;
using UnityEngine.InputSystem;

namespace CoinRush
{
    /// <summary>
    /// Turns a press-and-drag gesture into a steering vector in the range [-1, 1] on both axes.
    ///
    /// This is a floating virtual joystick: wherever the finger lands becomes the centre, and how far
    /// it travels from there is how hard the ball is pushed. Nothing is drawn — the anchor is wherever
    /// you pressed, which is what mobile players expect from a thumb they cannot see under.
    ///
    /// It reads <see cref="Pointer"/> rather than <see cref="Touchscreen"/> on purpose. Pointer is the
    /// Input System's abstract base for mouse, pen and touch alike, so the mouse in the Editor drives
    /// exactly the same code path the finger drives on a phone. No device required to iterate.
    /// Keyboard is polled too, as a convenience for testing in the Editor.
    ///
    /// Plain C# class, not a MonoBehaviour: it holds gesture state but needs no Unity lifecycle of its
    /// own, and keeping it off the GameObject means one less component to wire up in the Inspector.
    /// </summary>
    public sealed class SteeringInput
    {
        readonly float _dragRadiusPixels;

        Vector2 _anchor;
        bool _dragging;

        /// <param name="dragRadiusPixels">
        /// How far the finger must travel from the anchor for full tilt. Scaled off screen height so
        /// the gesture feels the same on a 720p phone and a tablet.
        /// </param>
        public SteeringInput(float dragRadiusPixels)
        {
            _dragRadiusPixels = Mathf.Max(1f, dragRadiusPixels);
        }

        /// <summary>The steering vector for this frame. Call once per Update.</summary>
        public Vector2 Read()
        {
            var keyboard = ReadKeyboard();
            if (keyboard.sqrMagnitude > 0.001f)
            {
                // A key is down, so the player is clearly at a desk. Let it win over a stale drag.
                _dragging = false;
                return keyboard;
            }

            return ReadPointer();
        }

        Vector2 ReadPointer()
        {
            var pointer = Pointer.current;
            if (pointer == null)
            {
                return Vector2.zero;
            }

            var position = pointer.position.ReadValue();

            if (!pointer.press.isPressed)
            {
                _dragging = false;
                return Vector2.zero;
            }

            if (!_dragging)
            {
                // First frame of the press: this is where the joystick lives until the finger lifts.
                _anchor = position;
                _dragging = true;
                return Vector2.zero;
            }

            var offset = (position - _anchor) / _dragRadiusPixels;
            return Vector2.ClampMagnitude(offset, 1f);
        }

        static Vector2 ReadKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return Vector2.zero;
            }

            var x = 0f;
            var y = 0f;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) x -= 1f;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) x += 1f;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) y -= 1f;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) y += 1f;

            return Vector2.ClampMagnitude(new Vector2(x, y), 1f);
        }
    }
}
