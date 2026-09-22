using System.Collections.Generic;
using UnityEngine;

namespace CoinRush
{
    /// <summary>
    /// Lays out the collectables and hazards at runtime, from a <see cref="LevelDefinition"/>.
    ///
    /// Coins and hazards are generated rather than placed by hand in the scene. A ring layout is a few
    /// lines of trigonometry, it rebuilds instantly when the level restarts, and — as an earlier
    /// ticket demonstrated painfully — hand-placed objects in the Editor are exactly where transform
    /// mistakes come from. The trade is that you cannot art-direct an individual coin, which for a
    /// test arena is not a cost worth paying to avoid.
    /// </summary>
    public sealed class ArenaBuilder : MonoBehaviour
    {
        [Header("Coins")]
        [SerializeField] float coinHeight = 0.7f;
        [SerializeField] Material coinMaterial;

        [Header("Hazards")]
        [SerializeField] float hazardSize = 1.4f;
        [SerializeField] Material hazardMaterial;

        readonly List<Coin> _coins = new List<Coin>();
        readonly List<Hazard> _hazards = new List<Hazard>();

        Transform _container;

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

            // Hazards hang off their own container so the whole ring can be orbited as one rigid
            // body. It also keeps the coins still while the hazards move.
            var hazardRoot = new GameObject("Hazards").transform;
            hazardRoot.SetParent(_container, worldPositionStays: false);
            hazardRoot.gameObject.AddComponent<Rotator>().degreesPerSecond = level.hazardOrbitDegreesPerSecond;

            for (var i = 0; i < level.hazardCount; i++)
            {
                // Offset by half a step so hazards sit between coins rather than under them.
                const float angleOffset = 0.5f;
                var position = PointOnRing(
                    i + angleOffset, level.hazardCount, level.hazardRingRadius, hazardSize * 0.5f);

                _hazards.Add(CreateHazard(hazardRoot, position));
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
            // The trigger lives on an empty parent with uniform scale, and the flattened cylinder is a
            // child. Putting a collider on the squashed mesh itself would inherit that non-uniform
            // scale, and Unity scales a SphereCollider by the largest axis — the coin would grab the
            // ball from further away than it looks.
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
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "Hazard";
            block.transform.SetParent(parent, worldPositionStays: false);
            block.transform.position = position;
            block.transform.localScale = Vector3.one * hazardSize;
            Paint(block, hazardMaterial);

            // A trigger, not a solid body: bouncing off a hazard and losing a life at the same time
            // reads as two punishments for one mistake.
            block.GetComponent<BoxCollider>().isTrigger = true;

            return block.AddComponent<Hazard>();
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
