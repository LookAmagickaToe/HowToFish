using System;
using Expanded.Pirates;
using UnityEngine;
using UnityEngine.Rendering;

namespace Expanded.Megalodon
{
    /// <summary>
    /// Getting eaten. You wake up inside the megalodon: a red, dripping dome with a pirate skeleton who
    /// has clearly been here a while, a few other things it swallowed, and - glowing on a barrel - Old
    /// Salt's dentures. A few seconds to grab them (E), then it spits you out at the island, slimy.
    ///
    /// Entirely local: the room is built high above the sea where nobody else will ever look, and the
    /// player is held in it exactly like on the wakeboard.
    /// </summary>
    internal static class MegaStomach
    {
        internal static bool Active { get; private set; }

        private const float Seconds = 7.5f;
        private const float Altitude = 320f;

        private static Transform _room, _dentures;
        private static GameObject _skeleton;
        private static Vector3 _stand;
        private static float _start;
        private static bool _grabbed;
        private static int _line;

        internal static void Begin()
        {
            if (Active) return;
            Player me = Player.LocalPlayer;
            if (me == null) return;

            if (Wakeboard.Riding) Wakeboard.End("eaten", false);
            try { if (DeckCannon.Manning) DeckCannon.Dismount("eaten"); } catch { }

            Vector3 here = me.Transform.position;
            Vector3 centre = new Vector3(here.x, PirateModule.WaterY() + Altitude, here.z);
            float feet = PlayerHold.FeetOffset();
            float floorY = centre.y - 2.6f;
            _stand = new Vector3(centre.x, floorY + 0.12f + feet, centre.z);

            try { BuildRoom(centre, floorY); }
            catch (Exception e) { Diag.Exception("MegaStomach.BuildRoom", e); }

            Active = true;
            _grabbed = false;
            _line = 0;
            _start = Time.time;
            PlayerHold.Begin("stomach", dt => _stand, _stand);
            try { me.Camera.SetRot(0f); } catch { }

            MegaFx.FlashScreen(new Color(0f, 0f, 0f, 1f), 1.4f);
            MegaFx.Banner("CHOMP.", new Color(1f, 0.3f, 0.25f), 1.5f);
            try { AudioManager.PlayGlobalClip("Swallow", false, 1f, 0.1f, false); } catch { }
            ModSave.AddCounter("megalodon.stomach.visits", 1);
            Diag.Info("MegaStomach: eaten. Welcome in.");
        }

        private static void BuildRoom(Vector3 c, float floorY)
        {
            _room = MegaShapes.Root("ExpandedMegalodonStomach");
            _room.position = c;

            // The walls: a sphere seen from the inside, glowing a sickly red.
            var dome = new GameObject("Dome");
            dome.transform.SetParent(_room, false);
            dome.transform.localScale = Vector3.one * 17f;
            dome.AddComponent<MeshFilter>().sharedMesh = MegaShapes.InsideSphere();
            MeshRenderer mr = dome.AddComponent<MeshRenderer>();
            Material wall = MegaShapes.Flat(new Color(0.55f, 0.08f, 0.1f), 0.55f);
            if (wall != null) mr.sharedMaterial = wall;
            mr.shadowCastingMode = ShadowCastingMode.Off;

            // Knee-deep in something yellow-green.
            MegaShapes.Prim(PrimitiveType.Cylinder, _room, new Vector3(0f, floorY - c.y, 0f), new Vector3(15f, 0.05f, 15f),
                            new Color(0.55f, 0.62f, 0.12f), null, 0.35f, false);
            for (int i = 0; i < 9; i++)
            {
                float a = i * 40f * Mathf.Deg2Rad;
                MegaShapes.Prim(PrimitiveType.Sphere, _room, new Vector3(Mathf.Cos(a) * 6f, floorY - c.y + 0.05f, Mathf.Sin(a) * 6f),
                                new Vector3(0.8f, 0.2f, 0.8f), new Color(0.7f, 0.75f, 0.2f), null, 0.4f, false);
            }

            var light = new GameObject("Glow").AddComponent<Light>();
            light.transform.SetParent(_room, false);
            light.transform.localPosition = new Vector3(0f, 2f, 0f);
            light.type = LightType.Point;
            light.color = new Color(1f, 0.35f, 0.3f);
            light.range = 22f;
            light.intensity = 2.5f;

            Vector3 fwd = Vector3.forward, right = Vector3.right;
            float fy = floorY - c.y;

            // The previous tenant.
            _skeleton = ModCharacters.Create("characters_skeleton", Vector3.zero, Quaternion.identity, _room);
            if (_skeleton != null)
            {
                _skeleton.transform.localPosition = fwd * 3.4f + right * -0.6f + Vector3.up * fy;
                _skeleton.transform.localRotation = Quaternion.LookRotation(-fwd);
                Bounds b = ModAssets.Measure(_skeleton);
                if (b.size.y > 0.1f) _skeleton.transform.localScale *= 1.7f / b.size.y;
                ModCharacters.Play(_skeleton, "Wave");
            }

            // Other things it ate.
            Transform chest = MegaShapes.FitKit("chest", _room, 1.1f, new Vector3(0f, 25f, 0f));
            if (chest != null) chest.localPosition = fwd * 3.2f + right * 2.6f + Vector3.up * fy;
            Transform bottle = MegaShapes.FitKit("bottle-large", _room, 0.5f, new Vector3(0f, 0f, 70f));
            if (bottle != null) bottle.localPosition = fwd * 2.2f + right * -2.6f + Vector3.up * (fy + 0.1f);
            Transform gb = MegaShapes.GameBoy(_room);
            gb.localPosition = fwd * 1.9f + right * 1.1f + Vector3.up * (fy + 0.04f);
            gb.localRotation = Quaternion.Euler(-80f, 20f, 0f);

            // The prize, on a barrel, glowing, because of course it is.
            Transform stand = MegaShapes.FitKit("barrel", _room, 0.8f, Vector3.zero);
            Vector3 standPos = fwd * 2.4f + right * -1.2f + Vector3.up * fy;
            float top = fy + 0.95f;
            if (stand != null) stand.localPosition = standPos;
            else
            {
                MegaShapes.Prim(PrimitiveType.Cylinder, _room, standPos + Vector3.up * 0.45f, new Vector3(0.6f, 0.45f, 0.6f), MegaShapes.Wood);
                top = fy + 0.92f;
            }
            _dentures = MegaShapes.Dentures(_room);
            _dentures.localPosition = new Vector3(standPos.x, top + 0.08f, standPos.z);
            _dentures.localScale = Vector3.one * 1.6f;
            var sparkle = new GameObject("Sparkle").AddComponent<Light>();
            sparkle.transform.SetParent(_dentures, false);
            sparkle.transform.localPosition = Vector3.up * 0.3f;
            sparkle.type = LightType.Point;
            sparkle.color = new Color(1f, 0.95f, 0.8f);
            sparkle.range = 3f;
            sparkle.intensity = 3f;
        }

        internal static void Tick()
        {
            if (!Active) return;
            float t = Time.time - _start;

            if (_dentures != null)
            {
                _dentures.Rotate(Vector3.up, 90f * Time.deltaTime, Space.World);
                Vector3 p = _dentures.localPosition;
                p.y += Mathf.Sin(Time.time * 3f) * 0.002f;
                _dentures.localPosition = p;
            }

            if (_skeleton != null)
            {
                if (_line == 0 && t > 1f) { _line++; Shouts.Say(_skeleton.transform, Vector3.up * 2.1f, "First time? Order the fish.", 3f); }
                else if (_line == 1 && t > 3.6f && !_grabbed) { _line++; Shouts.Say(_skeleton.transform, Vector3.up * 2.1f, "The teeth? Take 'em. [E] They bite.", 3.5f); }
            }

            if (t > Seconds) SpitOut();
        }

        /// <summary>E inside the stomach: grab Old Salt's teeth.</summary>
        internal static void Grab()
        {
            if (!Active || _grabbed) return;
            _grabbed = true;
            if (_dentures != null) _dentures.gameObject.SetActive(false);
            MegaFx.Banner("GOT OLD SALT'S TEETH!", new Color(1f, 0.95f, 0.7f), 2.5f);
            try { AudioManager.PlayGlobalClip("Win", false, 0.8f, 0.1f, false); } catch { }
            if (_skeleton != null) Shouts.Say(_skeleton.transform, Vector3.up * 2.1f, "Good luck out there. I'll hold the fort.", 3f);
            ModNet.SendToServer(Msg.Dentures);
        }

        private static void SpitOut()
        {
            Active = false;
            PlayerHold.End(Vector3.zero, "spat out");
            if (_room != null) UnityEngine.Object.Destroy(_room.gameObject);
            _room = null;
            _dentures = null;
            _skeleton = null;

            Player me = Player.LocalPlayer;
            if (me != null)
            {
                try { me.LocalTeleport(SpawnManager.PlayerSpawnPos, SpawnManager.PlayerSpawnRot, true); }
                catch (Exception e) { Diag.Exception("MegaStomach teleport", e); }
            }

            MegaFx.FlashScreen(new Color(0.4f, 0.7f, 0.1f, 0.9f), 1.2f);
            MegaFx.Slime(7f);
            try { AudioManager.PlayRandomGlobalClip("PiranhaVomit_V", 1, 2, false, 1f, 0.1f); } catch { }
            _yellAt = Time.time + 1.3f;
            Diag.Info("MegaStomach: spat out at the island.");
        }

        private static float _yellAt = -1f;

        /// <summary>Runs even after the stomach is gone, for the "...I'm back." a moment later.</summary>
        internal static void LateYell()
        {
            if (_yellAt > 0f && Time.time >= _yellAt)
            {
                _yellAt = -1f;
                Shouts.Yell(MegaLines.Back);
            }
        }

        internal static void Clear()
        {
            if (Active) PlayerHold.End(Vector3.zero, "session over");
            Active = false;
            if (_room != null) UnityEngine.Object.Destroy(_room.gameObject);
            _room = null;
            _dentures = null;
            _skeleton = null;
            _yellAt = -1f;
        }
    }
}
