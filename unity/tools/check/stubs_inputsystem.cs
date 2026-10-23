// Input System stub (com.unity.inputsystem 1.x) for compile-checking the BOVIE_INPUTSYSTEM pen path.
namespace UnityEngine.InputSystem
{
    public class ButtonControl { public bool isPressed; }
    public class AxisControl { public float ReadValue() { return 0; } }
    public class Vector2Control { public Vector2 ReadValue() { return Vector2.zero; } }
    public class Pen { public static Pen current; public ButtonControl inRange = new ButtonControl(), tip = new ButtonControl(); public AxisControl pressure = new AxisControl(); public Vector2Control tilt = new Vector2Control(), position = new Vector2Control(); }
}
