using UnityEngine;

namespace HoloLensCaptureDemo
{
    public sealed class EditorCameraMovement : MonoBehaviour
    {
        private void Update()
        {
#if UNITY_EDITOR
            if (Input.GetMouseButton(1))
            {
                var euler = transform.eulerAngles;
                euler.y += Input.GetAxis("Mouse X") * 2f;
                euler.x -= Input.GetAxis("Mouse Y") * 2f;
                transform.eulerAngles = euler;
                var movement = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
                transform.Translate(movement * Time.deltaTime);
            }
#endif
        }
    }
}
