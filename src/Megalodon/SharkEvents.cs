using System.IO;
using Expanded.Pirates;
using UnityEngine;

namespace Expanded.Megalodon
{
    internal enum SharkMode : byte
    {
        Gone = 0,
        Stalk,        // just the fin, closing in
        Chase,        // half out of the water behind the rider
        Strike,       // playing a scripted move (see SharkEvent)
        Hidden,       // under water, out of sight (the fake-out)
        PlayDead,     // belly up. Definitely dead. Honest.
        Finale,       // surfaced ahead of the boat, jaws wide open
        Distracted,   // going for the grilled food someone threw in
        Dead,
        Leaving
    }

    internal enum EvKind : byte
    {
        Lunge = 1,      // strike at the rider
        Chomp,          // close-range snap at a slow rider (or a swimmer)
        Dive,           // noses under and vanishes
        Emerge,         // the fake-out: bursts across the bow
        RopeBite,       // bites the tow rope shorter
        TailSlap,       // surfaces beside the rider and slams its tail: a wave that launches you
        PlayDead,       // belly up
        SternChomp,     // bites the boat's stern: engine trouble
        JumpOver,       // leaps clean over the boat
        Swallow,        // eats a barrel mine (Extra = mine id)
        Burp,           // ...and spits it back
        Decoy,          // snaps up grilled food thrown in the water
        RodeoStart,     // someone landed on its back (Extra = their owner id)
        RodeoEnd,
        Finale,         // STOPF IHM DAS MAUL
        Death,
        Leave,
        Appear,
        Phase,          // Extra = new phase
        Eaten,          // Extra = owner id of whoever it ate
        Bitten          // Extra = owner id, Style = the board they're down to
    }

    /// <summary>
    /// One scripted move. The host decides it and sends it once; every machine plays it from the same
    /// numbers, so a lunge follows the same arc on every screen without streaming positions.
    ///
    /// A strike has two parts: a telegraph (it sinks and closes in under the surface - a shadow and
    /// bubbles are all you see), then the strike itself: the MOUTH follows a parabola from A to B,
    /// peaking Apex metres above the straight line, and the jaws shut at fraction Snap of the way.
    /// </summary>
    internal sealed class SharkEvent
    {
        internal const byte StyleBreach = 1, StyleSkim = 2, StyleSmall = 3, StyleHuge = 4;

        public EvKind Kind;
        public ushort Id;
        public Vector3 From, A, B;
        public float Telegraph, Duration, Apex, Snap;
        public int Extra;
        public byte Style;

        /// <summary>Local time this machine started playing it.</summary>
        public float Start;

        public float Total => Telegraph + Duration;

        /// <summary>Moves whose pose comes from the event rather than from the position stream.</summary>
        public bool OverridesPose
        {
            get
            {
                switch (Kind)
                {
                    case EvKind.Lunge: case EvKind.Chomp: case EvKind.Dive: case EvKind.Emerge:
                    case EvKind.RopeBite: case EvKind.TailSlap: case EvKind.PlayDead: case EvKind.SternChomp:
                    case EvKind.JumpOver: case EvKind.Decoy: case EvKind.Death:
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>Moves whose jaws can catch a person in the water.</summary>
        public bool BitesPeople => Kind == EvKind.Lunge || Kind == EvKind.Chomp;

        public float SnapTime => Telegraph + Snap * Duration;

        public Vector3 Mouth(float u) => Vector3.Lerp(A, B, u) + Vector3.up * (Apex * 4f * u * (1f - u));

        public Vector3 Tangent(float u)
        {
            Vector3 d = (B - A) + Vector3.up * (Apex * 4f * (1f - 2f * u));
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        /// <summary>Where the jaws are when they shut.</summary>
        public Vector3 SnapPoint => Mouth(Snap);

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : fallback;
        }

        /// <summary>
        /// The body's centre and rotation <paramref name="t"/> seconds into the move.
        /// <paramref name="submerged"/> is true while it is deep enough to be just a shadow.
        /// </summary>
        public void Pose(float t, float mouthOffset, out Vector3 body, out Quaternion rot, out bool submerged)
        {
            submerged = false;
            float water = PirateModule.WaterY();
            switch (Kind)
            {
                case EvKind.TailSlap:
                {
                    // A = body centre where it surfaces, B = where the tail comes down (the wave).
                    Vector3 heading = Flat(A - B, Vector3.forward);
                    if (t < Telegraph)
                    {
                        float s = t / Mathf.Max(0.01f, Telegraph);
                        body = Vector3.Lerp(From, A, s);
                        body.y = Mathf.Lerp(water - 4f, A.y, s * s);
                        rot = Quaternion.LookRotation(heading);
                        submerged = s < 0.7f;
                    }
                    else
                    {
                        float u = Mathf.Clamp01((t - Telegraph) / Mathf.Max(0.01f, Duration));
                        float lift = Mathf.Sin(Mathf.PI * u);
                        body = A + Vector3.down * (0.8f * lift);
                        // Nose down, tail up to the sky, then SLAM.
                        rot = Quaternion.LookRotation(heading) * Quaternion.Euler(70f * lift, 0f, 0f);
                    }
                    return;
                }
                case EvKind.PlayDead:
                {
                    Vector3 heading = Flat(B - A, Vector3.forward);
                    body = A + Vector3.up * (0.1f + Mathf.Sin(t * 1.4f) * 0.12f);
                    rot = Quaternion.LookRotation(heading) * Quaternion.Euler(0f, 0f, 180f + Mathf.Sin(t * 0.9f) * 6f);
                    return;
                }
                case EvKind.Death:
                {
                    Vector3 heading = Flat(B - A, Vector3.forward);
                    float s = Mathf.Clamp01(t / Mathf.Max(0.01f, Duration));
                    body = A + Vector3.down * (s * s * 8f) + Vector3.up * 0.3f;
                    rot = Quaternion.LookRotation(heading) * Quaternion.Euler(s * 25f, 0f, 180f * Mathf.Clamp01(t / 1.5f));
                    submerged = s > 0.7f;
                    return;
                }
            }

            // A strike.
            if (t < Telegraph)
            {
                Vector3 dirA = Flat(Tangent(0f), Vector3.forward);
                Vector3 end = A - dirA * mouthOffset;
                float s = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.01f, Telegraph));
                body = Vector3.Lerp(From, end, s);
                float deep = water - 3.5f;
                float rise = Mathf.InverseLerp(0.7f, 1f, s);
                body.y = Mathf.Lerp(deep, end.y, rise * rise);
                Vector3 travel = Flat(end - From, dirA);
                rot = Quaternion.LookRotation(Vector3.Slerp(travel, dirA, s));
                submerged = rise < 0.3f;
                return;
            }

            float k = Mathf.Clamp01((t - Telegraph) / Mathf.Max(0.01f, Duration));
            Vector3 m = Mouth(k);
            Vector3 d = Tangent(k);
            body = m - d * mouthOffset;
            rot = Quaternion.LookRotation(d);
        }

        // ------------------------------------------------------------------ wire format

        public void Write(BinaryWriter w)
        {
            w.Write((byte)Kind);
            w.Write(Id);
            Cannonballs.WriteVec(w, From);
            Cannonballs.WriteVec(w, A);
            Cannonballs.WriteVec(w, B);
            w.Write(Telegraph);
            w.Write(Duration);
            w.Write(Apex);
            w.Write(Snap);
            w.Write(Extra);
            w.Write(Style);
        }

        public static SharkEvent Read(BinaryReader r)
        {
            return new SharkEvent
            {
                Kind = (EvKind)r.ReadByte(),
                Id = r.ReadUInt16(),
                From = Cannonballs.ReadVec(r),
                A = Cannonballs.ReadVec(r),
                B = Cannonballs.ReadVec(r),
                Telegraph = r.ReadSingle(),
                Duration = r.ReadSingle(),
                Apex = r.ReadSingle(),
                Snap = r.ReadSingle(),
                Extra = r.ReadInt32(),
                Style = r.ReadByte()
            };
        }
    }
}
