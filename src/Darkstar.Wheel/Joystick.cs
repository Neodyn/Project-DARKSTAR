using System.Runtime.InteropServices;

namespace Darkstar.Wheel;

/// <summary>What one joystick reports right now.</summary>
/// <param name="Connected">Whether Windows has a device under that number at all.</param>
/// <param name="Buttons">Buttons 1-32 as bits, button 1 in the lowest.</param>
/// <param name="Pov">The first hat in hundredths of a degree clockwise from forward, or 65535 when centred.</param>
internal readonly record struct JoystickState(bool Connected, uint Buttons, int Pov);

/// <summary>
/// Joysticks through winmm, the API every Windows has. It sees the first 32 buttons and the first
/// hat of up to 16 devices - enough for a wheel button and a hat, and no package to install.
/// </summary>
internal static class Joystick
{
    /// <summary>Windows numbers game controllers 0-15.</summary>
    public const int MaxDevices = 16;

    public static JoystickState Read(int id)
    {
        var info = new Native.JOYINFOEX
        {
            dwSize = (uint)Marshal.SizeOf<Native.JOYINFOEX>(),
            dwFlags = Native.JOY_RETURNALL | Native.JOY_RETURNPOVCTS,
        };

        return Native.joyGetPosEx(id, ref info) == Native.JOYERR_NOERROR
            ? new JoystickState(true, info.dwButtons, (int)info.dwPOV)
            : new JoystickState(false, 0, Native.JOY_POVCENTERED);
    }

    /// <summary>Whether <paramref name="button"/> (counted from 1) is held in <paramref name="buttons"/>.</summary>
    public static bool IsDown(uint buttons, int button) =>
        button is >= 1 and <= 32 && (buttons & (1u << (button - 1))) != 0;

    /// <summary>The lowest button set in <paramref name="buttons"/>, counted from 1; 0 for none.</summary>
    public static int FirstButton(uint buttons)
    {
        for (var bit = 0; bit < 32; bit++)
            if ((buttons & (1u << bit)) != 0) return bit + 1;
        return 0;
    }
}
