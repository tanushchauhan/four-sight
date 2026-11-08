using System;

namespace CardboardXR.Demos
{
    // Whether a demo needs a capability on, off, or doesn't mind.
    public enum Need
    {
        Any,
        Off,
        On,
    }

    // What a demo needs from the Core scene while it runs. DemoManager applies it on entering and
    // puts back what was there before on leaving.
    [Serializable]
    public struct DemoRequirements
    {
        public Need SixDof;
        public Need Passthrough;
        // Hand tracking, the largest part of the app's heat. It only runs in 6DoF.
        public Need Hands;
        // Scan the whole room for surfaces on entering, not just for the floor as 6DoF does: for a
        // demo that puts things on tables and walls.
        public bool ScanSurfaces;
        // Leave objects the demo placed on surfaces when leaving it, instead of removing them.
        public bool KeepPlacedObjects;
    }

    // The Core capabilities a demo can switch.
    public struct CoreState : IEquatable<CoreState>
    {
        public bool SixDof;
        public bool Passthrough;
        public bool Hands;

        public CoreState(bool sixDof, bool passthrough, bool hands)
        {
            SixDof = sixDof;
            Passthrough = passthrough;
            Hands = hands;
        }

        // What to switch to for a demo, from what's on now. Passthrough and hands need the camera,
        // so either one asked for turns 6DoF on; a demo that wants 6DoF off can have neither.
        public static CoreState Resolve(CoreState current, DemoRequirements needs)
        {
            var passthrough = Pick(needs.Passthrough, current.Passthrough);
            var hands = Pick(needs.Hands, current.Hands);
            var sixDof = Pick(needs.SixDof, current.SixDof);
            if (needs.SixDof == Need.Off)
            {
                passthrough = false;
                // Hands only run in 6DoF; leaving them on means they're back with it.
                sixDof = false;
            }
            else if (needs.Passthrough == Need.On || needs.Hands == Need.On)
            {
                sixDof = true;
            }
            if (!sixDof)
                passthrough = false;
            return new CoreState(sixDof, passthrough, hands);
        }

        static bool Pick(Need need, bool current) => need == Need.Any ? current : need == Need.On;

        public bool Equals(CoreState other) =>
            SixDof == other.SixDof && Passthrough == other.Passthrough && Hands == other.Hands;

        public override bool Equals(object obj) => obj is CoreState other && Equals(other);

        public override int GetHashCode() => (SixDof ? 1 : 0) | (Passthrough ? 2 : 0) | (Hands ? 4 : 0);

        public override string ToString() =>
            $"{(SixDof ? "6DoF" : "3DoF")}, passthrough {(Passthrough ? "on" : "off")}, hands {(Hands ? "on" : "off")}";
    }
}
