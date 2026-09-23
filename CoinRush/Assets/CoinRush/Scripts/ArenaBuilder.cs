using System.Collections.Generic;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Places the coins and hazards at runtime from a <see cref="LevelDefinition"/>.
    ///
    /// They are generated instead of placed by hand in the scene. A ring layout takes a few lines
    /// of trigonometry, rebuilds instantly when the level restarts, and avoids the transform
    /// mistakes that hand-placed objects caused earlier. The downside is that individual coins
    /// cannot be positioned by hand.
    /// </summary>
    public sealed class ArenaBuilder : MonoBehaviour
    {
        [Header("Coins")]
        [SerializeField] float coinHeight = 0.7f;
        [SerializeField] Material coinMaterial;

        [Header("Hazards")]
        [SerializeField] float hazardSize = 1.4f;
        [SerializeField] Material hazardMaterial;

        [Tooltip("Hazards turn back at this distance from the centre. Keep it inside the walls.")]
        [SerializeField] float hazardWanderRadius = 12.5f;

        readonly List<Coin> _coins = new List<Coin>();
        readonly List<Hazard> _hazards = new List<Hazard>();

        Transform _container;
        HazardMonster.Palette _palette;

        /// <summary>The coins in the level as of the last <see cref="Build"/>.</summary>
        public IReadOnlyList<Coin> Coins => _coins;

        /// <summary>The hazards in the level as of the last <see cref="Build"/>.</summary>
        public IReadOnlyList<Hazard> Hazards => _hazards;

        /// <summary>Clears any existing level and lays out the one described by <paramref name="level"/>.</summary>
        public void Build(LevelDefinition level)
        {
            Clear();

            _container = new GameObject("Level").transform;
            _container.SetParent(transform, worldPositionStays: false);

            for (var i = 0; i < level.coinCount; i++)
            {
                _coins.Add(CreateCoin(PointOnRing(i, level.coinCount, level.coinRingRadius, coinHeight)));
            }

            // The container only keeps the hierarchy tidy. Each hazard moves itself.
            var hazardRoot = new GameObject("Hazards").transform;
            hazardRoot.SetParent(_container, worldPositionStays: false);

            for (var i = 0; i < level.hazardCount; i++)
            {
                // Spread evenly on a ring, offset half a step so they do not start on top of
                // the coins.
                const float angleOffset = 0.5f;
                var position = PointOnRing(
                    i + angleOffset, level.hazardCount, level.hazardRingRadius, hazardSize * 0.5f);

                var hazard = CreateHazard(hazardRoot, position);
                hazard.GetComponent<HazardMotion>()
                    .Launch(level.hazardSpeed, hazardWanderRadius, level.hazardTurnSeconds);

                _hazards.Add(hazard);
            }
        }

        /// <summary>Destroys the current level. Safe to call before anything has been built.</summary>
        public void Clear()
        {
            _coins.Clear();
            _hazards.Clear();

            if (_container != null)
            {
                Destroy(_container.gameObject);
                _container = null;
            }
        }

        static Vector3 PointOnRing(float index, int total, float radius, float height)
        {
            var angle = index / total * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius);
        }

        Coin CreateCoin(Vector3 position)
        {
            // The trigger is on an empty parent with uniform scale, and the flattened cylinder is a
            // child. On the squashed mesh itself, Unity would scale the SphereCollider by the
            // largest axis and the coin would be collected from further away than it looks.
            var root = new GameObject("Coin");
            root.transform.SetParent(_container, worldPositionStays: false);
            root.transform.position = position;

            var trigger = root.AddComponent<SphereCollider>();
            trigger.radius = 0.75f;
            trigger.isTrigger = true;

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "Visual";
            Destroy(visual.GetComponent<Collider>());
            visual.transform.SetParent(root.transform, worldPositionStays: false);
            visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(0.7f, 0.08f, 0.7f);
            Paint(visual, coinMaterial);

            return root.AddComponent<Coin>();
        }

        Hazard CreateHazard(Transform parent, Vector3 position)
        {
            var root = new GameObject("Hazard");
            root.transform.SetParent(parent, worldPositionStays: false);
            root.transform.position = position;

            // A trigger instead of a solid collider, so touching a hazard costs a life without also
            // bouncing the ball away. A sphere, because the monster turns to face the ball and a box
            // would change its reach as it turned.
            var trigger = root.AddComponent<SphereCollider>();
            trigger.radius = hazardSize * 0.5f;
            trigger.isTrigger = true;

            // A kinematic Rigidbody, because the collider moves every frame. Without one, Unity
            // treats it as static and rebuilds its broadphase every physics step, which is slow.
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            root.AddComponent<HazardMotion>();
            root.AddComponent<HazardMonster>().Build(hazardSize, MonsterPalette());

            return root.AddComponent<Hazard>();
        }

        /// <summary>
        /// The monster's materials, made once and shared by every hazard. They are copies of the
        /// hazard material with new colours, which keeps its shader and its emission switched on. A
        /// material built from scratch in code could use a shader variant the build left out.
        /// </summary>
        HazardMonster.Palette MonsterPalette()
        {
            if (_palette != null || hazardMaterial == null)
            {
                return _palette ?? new HazardMonster.Palette();
            }

            _palette = new HazardMonster.Palette
            {
                Spikes = hazardMaterial,
                Body = Tint(hazardMaterial, "Monster Body",
                    new Color(0.18f, 0.02f, 0.06f), new Color(0.25f, 0f, 0.05f)),
                Eye = Tint(hazardMaterial, "Monster Eye",
                    new Color(1f, 0.95f, 0.7f), new Color(2.2f, 1.9f, 0.7f)),
                Pupil = Tint(hazardMaterial, "Monster Pupil",
                    new Color(0.02f, 0.02f, 0.02f), Color.black),
            };

            return _palette;
        }

        static Material Tint(Material source, string name, Color baseColor, Color emission)
        {
            var material = new Material(source) { name = name };
            material.SetColor("_BaseColor", baseColor);
            material.SetColor("_EmissionColor", emission);
            return material;
        }

        void OnDestroy()
        {
            // Materials made with `new` are not cleaned up with the scene.
            if (_palette == null) return;
            Destroy(_palette.Body);
            Destroy(_palette.Eye);
            Destroy(_palette.Pupil);
        }

        static void Paint(GameObject target, Material material)
        {
            if (material == null)
            {
                return;
            }

            target.GetComponent<MeshRenderer>().sharedMaterial = material;
        }
    }
}
