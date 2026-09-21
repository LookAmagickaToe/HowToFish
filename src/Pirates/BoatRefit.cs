using System;
using Expanded.Content;
using UnityEngine;

namespace Expanded.Pirates
{
    /// <summary>
    /// The reward for beating the pirates: the crew's boat is refitted with the captured ship's mast,
    /// sails and colours.
    ///
    /// Why a refit and not a hull swap: the boat's physics - buoyancy, driving, where players stand -
    /// all hang off its own hull colliders. Replacing the hull would mean players walking on an
    /// invisible old deck inside a bigger new one, and changing its colliders would change how it
    /// floats and handles. So the rigging is added on top, purely visual, and the boat keeps
    /// behaving exactly as the game intends. The refit also arms the stern gun (see DeckCannon).
    /// </summary>
    internal static class BoatRefit
    {
        private static Transform _root;
        private static Boat _fittedOn;
        private static bool _fitted;

        internal static void ClientTick()
        {
            bool wanted = SharedState.Has(PirateStory.UnlockPirateShip);
            Boat boat = BoatManager.Boat;

            if (!wanted || boat == null)
            {
                if (_fitted) Clear();
                return;
            }
            if (_fitted && _fittedOn == boat && _root != null) return;

            Build(boat);
        }

        private static void Build(Boat boat)
        {
            Clear();
            _fittedOn = boat;
            _fitted = true;

            try
            {
                Vector3 mid, fwd;
                if (!BoatMount.TryGet(BoatMount.Slot.Midships, out mid, out fwd))
                {
                    Diag.Warn("BoatRefit: could not measure the boat.");
                    return;
                }

                _root = new GameObject(BoatMount.MountRoot + "Refit").transform;
                _root.SetParent(boat.transform, false);

                // Rigging only: no colliders, so the boat's handling is untouched.
                GameObject mast = ModAssets.Create("mast-ropes", Vector3.zero, Quaternion.identity, _root, solid: false)
                                  ?? ModAssets.Create("mast", Vector3.zero, Quaternion.identity, _root, solid: false);
                if (mast != null)
                {
                    mast.transform.localPosition = mid;
                    mast.transform.localRotation = Quaternion.LookRotation(fwd, Vector3.up);
                }

                GameObject flag = ModAssets.Create("flag-pirate-high", Vector3.zero, Quaternion.identity, _root, solid: false);
                if (flag != null)
                {
                    Vector3 stern, sternFwd;
                    if (BoatMount.TryGet(BoatMount.Slot.Stern, out stern, out sternFwd))
                    {
                        flag.transform.localPosition = stern;
                        flag.transform.localRotation = Quaternion.LookRotation(fwd, Vector3.up);
                    }
                }

                Diag.Info("BoatRefit: pirate rigging fitted.");
            }
            catch (Exception e)
            {
                Diag.Exception("BoatRefit.Build", e);
            }
        }

        internal static void Clear()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
            _fittedOn = null;
            _fitted = false;
        }
    }
}
