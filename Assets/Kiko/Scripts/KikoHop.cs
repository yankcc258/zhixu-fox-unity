using System;

namespace Kiko
{
    // Screen-space flight shared by the desktop pet and the regression checks.
    public static class KikoHop
    {
        public const float Reach = 240f, Rise = 180f, Drop = 300f;
        public static bool Reachable(float up, float gap, float scale) =>
            up <= Rise * scale && up >= -Drop * scale && gap <= Reach * scale;
        public static bool AlreadyThere(float dx, float dy) => Math.Abs(dx) <= 6 && Math.Abs(dy) <= 4;

        public struct Flight
        {
            public float FromX, FromY, ToX, ToY, Gravity, Velocity, Duration;
            public float ApexPhase => Velocity / Gravity / Duration;
            public void Sample(float phase, out float x, out float y)
            {
                float u = Math.Max(0, Math.Min(1, phase)), t = u * Duration;
                x = FromX + (ToX - FromX) * u;
                y = u >= 1 ? ToY : FromY - Velocity * t + .5f * Gravity * t * t;
            }
        }

        // Choose an apex just above the HIGHER endpoint, not an extra arc proportional
        // to the entire climb. Reserve room for ears so high platforms cannot launch her offscreen.
        public static Flight Plan(float x0, float y0, float x1, float y1, float scale, float minFeetY)
        {
            float high = Math.Min(y0, y1);
            float clearance = Math.Min(32 * scale, 18 * scale + Math.Abs(x1 - x0) * .04f);
            float apex = Math.Min(high, Math.Max(minFeetY, high - clearance));
            float g = 1400 * scale;
            float v = (float)Math.Sqrt(2 * g * Math.Max(0, y0 - apex));
            float down = (float)Math.Sqrt(2 * Math.Max(0, y1 - apex) / g);
            return new Flight { FromX = x0, FromY = y0, ToX = x1, ToY = y1,
                Gravity = g, Velocity = v, Duration = Math.Max(.1f, v / g + down) };
        }
    }
}
