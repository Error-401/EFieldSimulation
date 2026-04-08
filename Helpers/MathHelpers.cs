using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EFieldSimulation.Helpers;

public static class MathHelpers
{
    /// <summary>
    /// Map a value in [0,1] to a Jet-like color (blue→cyan→green→yellow→red).
    /// Returns (R, G, B) in [0, 255].
    /// </summary>
    public static (byte R, byte G, byte B) JetColorMap(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        float r, g, b;

        if (t < 0.125f)
        {
            r = 0; g = 0; b = 0.5f + t * 4f;
        }
        else if (t < 0.375f)
        {
            r = 0; g = (t - 0.125f) * 4f; b = 1;
        }
        else if (t < 0.625f)
        {
            r = (t - 0.375f) * 4f; g = 1; b = 1 - (t - 0.375f) * 4f;
        }
        else if (t < 0.875f)
        {
            r = 1; g = 1 - (t - 0.625f) * 4f; b = 0;
        }
        else
        {
            r = 1 - (t - 0.875f) * 4f; g = 0; b = 0;
        }

        return (
            (byte)(Math.Clamp(r, 0, 1) * 255),
            (byte)(Math.Clamp(g, 0, 1) * 255),
            (byte)(Math.Clamp(b, 0, 1) * 255));
    }

    /// <summary>
    /// Map a value to a diverging Blue-White-Red colormap.
    /// t in [0,1], 0.5 = white.
    /// </summary>
    public static (byte R, byte G, byte B) DivergingColorMap(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.5f)
        {
            float s = t * 2; // 0..1
            byte val = (byte)(s * 255);
            return (val, val, 255);
        }
        else
        {
            float s = (t - 0.5f) * 2; // 0..1
            byte val = (byte)((1 - s) * 255);
            return (255, val, val);
        }
    }
}