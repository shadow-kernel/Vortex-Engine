using System;
using System.Windows;
using System.Windows.Input;
using Editor.DllWrapper;

namespace Editor.Core.Services
{
    /// <summary>
    /// Simple FPS-style camera controller.
    /// Click in viewport, then use WASD + mouse to move freely.
    /// Hold right-click to look around.
    /// </summary>
    public class EditorCameraController
    {
        private static EditorCameraController _instance;
        public static EditorCameraController Instance => _instance ?? (_instance = new EditorCameraController());

        // Camera position
        private float _posX = 0.0f;
        private float _posY = 2.0f;
        private float _posZ = -5.0f;

        // Camera angles (degrees)
        private float _yaw = 0.0f;
        private float _pitch = 0.0f;
        private float _roll = 0.0f;  // Added roll support for tilted camera views

        // Mouse tracking
        private Point _lastMouse;
        private bool _rightMouseDown;

        // Movement keys
        private bool _wKey, _sKey, _aKey, _dKey, _qKey, _eKey, _shiftKey;

        // Settings
        public float MoveSpeed { get; set; } = 5.0f;
        public float LookSpeed { get; set; } = 0.2f;
        public float SprintMultiplier { get; set; } = 2.5f;

        // Public properties
        public float PositionX => _posX;
        public float PositionY => _posY;
        public float PositionZ => _posZ;
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public float Roll => _roll;
        public bool IsFlyMode => _rightMouseDown;

        private EditorCameraController()
        {
            UpdateCamera();
        }

        public void Reset()
        {
            _posX = 0; _posY = 2; _posZ = -5;
            _yaw = 0; _pitch = 0; _roll = 0;
            UpdateCamera();
        }

        public void FocusOn(float x, float y, float z, float distance = 5)
        {
            _posX = x;
            _posY = y + 2;
            _posZ = z - distance;
            _roll = 0;  // Reset roll when focusing
            UpdateCamera();
        }
        
        /// <summary>
        /// Set camera position and rotation directly (used when switching to a game camera view).
        /// </summary>
        public void SetPositionAndRotation(float x, float y, float z, float yaw, float pitch)
        {
            _posX = x;
            _posY = y;
            _posZ = z;
            _yaw = yaw;
            _pitch = Math.Max(-89, Math.Min(89, pitch));
            _roll = 0;
            UpdateCamera();
        }
        
        /// <summary>
        /// Set camera position and full rotation including roll (for viewing through game cameras).
        /// Uses Entity Euler angles: rotX = pitch, rotY = yaw, rotZ = roll
        /// </summary>
        public void SetFromEntityTransform(float posX, float posY, float posZ, float rotX, float rotY, float rotZ)
        {
            _posX = posX;
            _posY = posY;
            _posZ = posZ;
            
            // Entity rotation: X = pitch, Y = yaw, Z = roll
            // EditorCamera convention is different, need to map correctly
            _yaw = rotY;      // Horizontal rotation (looking left/right)
            _pitch = rotX;    // Vertical rotation (looking up/down) - NOT inverted!
            _roll = rotZ;     // Tilt rotation
            
            UpdateCamera();
        }

        public void OnMouseDown(MouseButtonEventArgs e, Point pos)
        {
            _lastMouse = pos;
            if (e.RightButton == MouseButtonState.Pressed)
                _rightMouseDown = true;
        }

        public void OnMouseUp(MouseButtonEventArgs e)
        {
            if (e.RightButton == MouseButtonState.Released)
                _rightMouseDown = false;
        }

        public void OnMouseMove(Point pos)
        {
            if (!_rightMouseDown) 
            {
                _lastMouse = pos;
                return;
            }

            float dx = (float)(pos.X - _lastMouse.X);
            float dy = (float)(pos.Y - _lastMouse.Y);
            _lastMouse = pos;

            _yaw += dx * LookSpeed;
            _pitch += dy * LookSpeed;  // FIXED: Removed minus sign to fix inverted Y-axis
            _pitch = Math.Max(-89, Math.Min(89, _pitch));


            UpdateCamera();
        }

        public void OnMouseWheel(int delta)
        {
            // Dolly forward/backward
            float amount = delta > 0 ? 1.0f : -1.0f;
            MoveInLookDirection(amount);
            UpdateCamera();
        }

        public void OnKeyDown(Key key)
        {
            switch (key)
            {
                case Key.W: _wKey = true; break;
                case Key.S: _sKey = true; break;
                case Key.A: _aKey = true; break;
                case Key.D: _dKey = true; break;
                case Key.Q: _qKey = true; break;
                case Key.E: _eKey = true; break;
                case Key.LeftShift: case Key.RightShift: _shiftKey = true; break;
                case Key.Home: Reset(); break;
            }
        }

        public void OnKeyUp(Key key)
        {
            switch (key)
            {
                case Key.W: _wKey = false; break;
                case Key.S: _sKey = false; break;
                case Key.A: _aKey = false; break;
                case Key.D: _dKey = false; break;
                case Key.Q: _qKey = false; break;
                case Key.E: _eKey = false; break;
                case Key.LeftShift: case Key.RightShift: _shiftKey = false; break;
            }
        }

        public void Update(float dt)
        {
            // Only move when right mouse is held
            if (!_rightMouseDown) return;

            float speed = MoveSpeed * dt;
            if (_shiftKey) speed *= SprintMultiplier;

            // W/S moves in look direction (including up/down based on pitch)
            if (_wKey) MoveInLookDirection(speed);
            if (_sKey) MoveInLookDirection(-speed);
            if (_dKey) MoveRight(speed);
            if (_aKey) MoveRight(-speed);
            // E/Q for pure vertical movement (optional)
            if (_eKey) _posY += speed;
            if (_qKey) _posY -= speed;

            UpdateCamera();
        }

        private void MoveInLookDirection(float amount)
        {
            // Move in the actual look direction (including pitch for Y movement)
            float yawRad = _yaw * (float)(Math.PI / 180);
            float pitchRad = _pitch * (float)(Math.PI / 180);

            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));

            _posX += fx * amount;
            _posY += fy * amount;
            _posZ += fz * amount;
        }

        private void MoveRight(float amount)
        {
            float yawRad = _yaw * (float)(Math.PI / 180);
            _posX += (float)Math.Cos(yawRad) * amount;
            _posZ -= (float)Math.Sin(yawRad) * amount;
        }

        private void UpdateCamera()
        {
            float yawRad = _yaw * (float)(Math.PI / 180);
            float pitchRad = _pitch * (float)(Math.PI / 180);
            float rollRad = _roll * (float)(Math.PI / 180);

            // Forward direction
            float fx = (float)(Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(Math.Cos(yawRad) * Math.Cos(pitchRad));

            // Look target = position + forward
            float tx = _posX + fx;
            float ty = _posY + fy;
            float tz = _posZ + fz;
            
            // Calculate up vector with roll
            // Start with world up, then apply roll rotation around forward axis
            float upX, upY, upZ;
            
            if (Math.Abs(_roll) < 0.001f)
            {
                // No roll - use simple up vector
                upX = 0; upY = 1; upZ = 0;
            }
            else
            {
                // Calculate right vector (cross of forward and world up)
                float rightX = (float)(Math.Cos(yawRad));
                float rightY = 0;
                float rightZ = (float)(-Math.Sin(yawRad));
                
                // Calculate actual up from forward and right (cross product)
                float baseUpX = fy * rightZ - fz * rightY;
                float baseUpY = fz * rightX - fx * rightZ;
                float baseUpZ = fx * rightY - fy * rightX;
                
                // Normalize base up
                float upLen = (float)Math.Sqrt(baseUpX * baseUpX + baseUpY * baseUpY + baseUpZ * baseUpZ);
                if (upLen > 0.0001f)
                {
                    baseUpX /= upLen;
                    baseUpY /= upLen;
                    baseUpZ /= upLen;
                }
                else
                {
                    baseUpX = 0; baseUpY = 1; baseUpZ = 0;
                }
                
                // Apply roll rotation using Rodrigues' rotation formula
                // Rotate baseUp around forward axis by roll angle
                float cosR = (float)Math.Cos(rollRad);
                float sinR = (float)Math.Sin(rollRad);
                
                // Dot product of forward and baseUp
                float dot = fx * baseUpX + fy * baseUpY + fz * baseUpZ;
                
                // Cross product of forward and baseUp
                float crossX = fy * baseUpZ - fz * baseUpY;
                float crossY = fz * baseUpX - fx * baseUpZ;
                float crossZ = fx * baseUpY - fy * baseUpX;
                
                // Rodrigues formula: v_rot = v*cos(?) + (k×v)*sin(?) + k*(k·v)*(1-cos(?))
                upX = baseUpX * cosR + crossX * sinR + fx * dot * (1 - cosR);
                upY = baseUpY * cosR + crossY * sinR + fy * dot * (1 - cosR);
                upZ = baseUpZ * cosR + crossZ * sinR + fz * dot * (1 - cosR);
            }

            try
            {
                VortexAPI.SetViewCamera(_posX, _posY, _posZ, tx, ty, tz, upX, upY, upZ);
            }
            catch
            {
                // Ignore if renderer not ready yet
            }
        }
    }
}
