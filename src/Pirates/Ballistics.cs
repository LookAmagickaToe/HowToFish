using System;

namespace Expanded.Pirates
{
    /// <summary>
    /// Cannon aiming maths. Free of UnityEngine on purpose so it is covered by the headless tests:
    /// a wrong sign here would otherwise only show up as "the pirates never hit anything".
    /// </summary>
    public static class Ballistics
    {
        /// <summary>
        /// Elevation angle (radians) that lands a projectile fired at <paramref name="speed"/> on a
        /// target <paramref name="dx"/> metres away horizontally and <paramref name="dy"/> metres
        /// above the muzzle, under gravity <paramref name="g"/>.
        ///
        /// Returns the low arc - a flat, fast broadside - when one exists. Returns false when the
        /// target is out of range; <paramref name="angle"/> is then 45 degrees, the angle of
        /// maximum reach, so a shot at an unreachable target still flies as far as it can.
        /// </summary>
        public static bool SolveLowArc(double dx, double dy, double speed, double g, out double angle)
        {
            angle = Math.PI / 4.0;
            if (speed <= 0 || g <= 0) return false;

            dx = Math.Abs(dx);
            if (dx < 1e-4)
            {
                // Straight up or down: aim along the vertical.
                angle = dy >= 0 ? Math.PI / 2.0 : -Math.PI / 2.0;
                return dy <= speed * speed / (2.0 * g);
            }

            double v2 = speed * speed;
            double disc = v2 * v2 - g * (g * dx * dx + 2.0 * dy * v2);
            if (disc < 0) return false;

            angle = Math.Atan((v2 - Math.Sqrt(disc)) / (g * dx));
            return true;
        }

        /// <summary>Time of flight to cover <paramref name="dx"/> horizontally at the given angle.</summary>
        public static double FlightTime(double dx, double speed, double angle)
        {
            double horizontal = speed * Math.Cos(angle);
            return horizontal > 1e-6 ? Math.Abs(dx) / horizontal : 0;
        }

        /// <summary>Height relative to the muzzle after <paramref name="t"/> seconds.</summary>
        public static double HeightAt(double t, double speed, double angle, double g)
        {
            return speed * Math.Sin(angle) * t - 0.5 * g * t * t;
        }
    }
}
