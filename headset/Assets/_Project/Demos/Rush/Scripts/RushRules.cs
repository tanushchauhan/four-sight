using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.Rush
{
    public enum Judgement
    {
        None,
        Perfect,
        Good,
    }

    // Orb Rush's rules, apart from anything on screen. Distances are along the track, from the
    // player's eyes: an orb arrives at k_ArrivalDistance, where the hand meets it at arm's reach,
    // and every arrival spot is inside what the phone's camera sees (68 by 42 degrees), since a hand
    // it can't see can't hit anything: at 0.45 m that's about 60 by 34 cm, and the spots keep well
    // inside it, centered a little below the eyes.
    public static class RushRules
    {
        public const float k_ArrivalDistance = 0.45f;
        public const float k_SpawnDistance = 6f;
        // Where an orb can be hit, from the eyes: from well past the arrival spot back to close in.
        public const float k_NearestHit = 0.15f;
        public const float k_FarthestHit = 0.9f;
        // Within this of the arrival spot is a Perfect.
        public const float k_PerfectBand = 0.12f;
        // An orb this far behind the eyes is missed.
        public const float k_MissBehind = 0.15f;
        public const float k_OrbRadius = 0.07f;
        // How far from an orb's middle a hand point can be to touch it: across and up and down, the
        // orb's radius and the hand's own size around the joints tested; along the track, much more,
        // since the hand's distance is the least sure part of tracking it. On the Pixel, which has no
        // depth sensor, the hand read 12-23 cm from the camera while hitting orbs at 45 cm, and 40
        // of the first round's 71 orbs were missed.
        public const float k_TouchAcross = 0.12f;
        public const float k_TouchDepth = 0.2f;
        // Arrival spots across and up from the spot straight ahead and k_Drop below the eyes. The
        // camera's view is only about 17 cm above and below eye level at arm's reach, so a hand
        // reaching for an orb any lower would leave it (as first written, spots went 20 cm down).
        public const float k_Across = 0.14f;
        public const float k_Drop = 0.03f;
        public const float k_Up = 0.05f;
        public const float k_Down = 0.07f;
        // Consecutive orbs land at least this far apart, so the hand has somewhere to go.
        public const float k_MinStep = 0.06f;
        public const float k_RoundSeconds = 60f;
        public const int k_Perfect = 100;
        public const int k_Good = 50;
        public const int k_MaxMultiplier = 4;

        // Seconds between orbs, and their speed in m/s, a share of the way through the round.
        public static float Interval(float progress) => Mathf.Lerp(1.1f, 0.6f, Mathf.Clamp01(progress));

        public static float Speed(float progress) => Mathf.Lerp(2.2f, 3f, Mathf.Clamp01(progress));

        // Where the next orb arrives, across (x) and up (y) from the track's center at the arrival
        // distance, which is k_Drop below the eyes.
        public static Vector2 NextSpot(System.Random random, Vector2 previous)
        {
            for (var attempt = 0; ; attempt++)
            {
                var spot = new Vector2(
                    Mathf.Lerp(-k_Across, k_Across, (float)random.NextDouble()),
                    Mathf.Lerp(-k_Down, k_Up, (float)random.NextDouble()));
                if (attempt >= 20 || Vector2.Distance(spot, previous) >= k_MinStep)
                    return spot;
            }
        }

        // Whether any of the hand's points is touching the orb: inside an ellipsoid around it,
        // k_TouchAcross across and k_TouchDepth along the track. Points are in the track's space,
        // whose z runs along it.
        public static bool Touches(Vector3 orb, IReadOnlyList<Vector3> hand)
        {
            for (var i = 0; i < hand.Count; i++)
            {
                var offset = hand[i] - orb;
                var across = new Vector2(offset.x, offset.y).magnitude / k_TouchAcross;
                var along = offset.z / k_TouchDepth;
                if (across * across + along * along <= 1f)
                    return true;
            }
            return false;
        }

        // What a touch at this distance from the eyes along the track scores, or None if an orb
        // there can't be hit.
        public static Judgement Judge(float distance)
        {
            if (distance < k_NearestHit || distance > k_FarthestHit)
                return Judgement.None;
            return Mathf.Abs(distance - k_ArrivalDistance) <= k_PerfectBand ? Judgement.Perfect : Judgement.Good;
        }

        public static int Multiplier(int combo) => Mathf.Min(k_MaxMultiplier, 1 + combo / 8);

        public static int Points(Judgement judgement, int combo) =>
            (judgement == Judgement.Perfect ? k_Perfect : judgement == Judgement.Good ? k_Good : 0) * Multiplier(combo);
    }
}
