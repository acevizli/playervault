using UnityEngine;
using UnityEngine.InputSystem;

namespace CoinRush
{
    /// <summary>
    /// Turns a press-and-drag gesture into a steering vector in the range [-1, 1] on both axes.
    ///
    /// A floating virtual joystick: the point where the finger first touches is the centre, and
    /// the distance dragged from there sets how hard the ball is pushed. Nothing is drawn.
    ///
    /// Reads <see cref="Pointer"/> instead of <see cref="Touchscreen"/>. Pointer covers mouse, pen
    /// and touch, so the mouse in the Editor uses the same code path as a finger on a phone. The
    /// keyboard is also read, for testing in the Editor.
    ///
    /// A plain C# class instead of a MonoBehaviour, since it needs no Unity lifecycle and this
    /// avoids another component to set up in the Inspector.
    /// </summary>
    public sealed class SteeringInput
    {
        readonly float _dragRadiusPixels;

        Vector2 _anchor;
        bool _dragging;

        /// <param name="dragRadiusPixels">
        /// How far the finger must move from the start point for full speed. Scaled by screen height
        /// so the gesture feels the same on a small phone and a tablet.
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
                // A key is pressed, so keyboard input takes priority over any drag.
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
                // First frame of the press: this point is the joystick centre until the finger lifts.
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
