using System;
using UnityEngine;

namespace HoloLensCaptureDemo
{
    public static class CaptureGeometry
    {
        // Image coordinates use a top-left origin. Camera view space looks along -Z.
        public static Ray ImageRay(Matrix4x4 cameraToWorld, Matrix4x4 projection, float x, float y)
        {
            if (Mathf.Abs(projection.determinant) < 0.000001f)
                throw new ArgumentException("Invalid camera projection matrix.");
            var view = projection.inverse * new Vector4(2f * x - 1f, 1f - 2f * y, 1f, 1f);
            if (Mathf.Abs(view.w) < 0.000001f)
                throw new ArgumentException("Cannot unproject the photo pixel.");
            var direction = new Vector3(view.x, view.y, view.z) / view.w;
            return new Ray(cameraToWorld.MultiplyPoint3x4(Vector3.zero),
                cameraToWorld.MultiplyVector(direction).normalized);
        }
    }
}
