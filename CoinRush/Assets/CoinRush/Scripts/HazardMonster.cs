using UnityEngine;
using UnityEngine.Rendering;

namespace CoinRush
{
    /// <summary>
    /// What a hazard looks like: a spiked, breathing blob with one glowing eye and a row of teeth
    /// that turns to watch the ball.
    ///
    /// Built from primitives in code, like the rest of the arena, so there is no model to import.
    /// Only the look lives here. The trigger, the movement and the damage stay on the hazard's root,
    /// so the bobbing below never moves the collider.
    /// </summary>
    public sealed class HazardMonster : MonoBehaviour
    {
        /// <summary>The materials a monster is built from. Shared by every hazard in the arena.</summary>
        public sealed class Palette
        {
            public Material Body;
            public Material Spikes;
            public Material Eye;
            public Material Pupil;
        }

        const int SpikeCount = 18;
        const float TurnDegreesPerSecond = 220f;
        const float BobHeight = 0.12f;
        const float BobSpeed = 3f;
        const float BreatheAmount = 0.06f;
        const float BreatheSpeed = 5f;

        // One lookup shared by every hazard. Unity's null check also catches a ball that was destroyed.
        static Transform s_ball;

        // Unity has no cone primitive, so one is built on first use and shared by every spike.
        static Mesh s_cone;

        Transform _visual;
        float _phase;

        /// <summary>Creates the body parts under this object. <paramref name="size"/> is the overall width.</summary>
        public void Build(float size, Palette palette)
        {
            // Every hazard starts at a different point in its bob and breath, so a group of them
            // does not move in step.
            _phase = Random.Range(0f, Mathf.PI * 2f);

            _visual = new GameObject("Visual").transform;
            _visual.SetParent(transform, worldPositionStays: false);

            var bodyRadius = size * 0.36f;

            Part(Primitive(PrimitiveType.Sphere), "Body", palette.Body,
                Vector3.zero, Quaternion.identity, Vector3.one * (bodyRadius * 2f));

            BuildSpikes(bodyRadius, size, palette.Spikes);
            BuildFace(bodyRadius, palette);
        }

        void BuildSpikes(float bodyRadius, float size, Material material)
        {
            var length = size * 0.4f;
            var thickness = size * 0.16f;

            // Points spread evenly over a sphere (a Fibonacci spiral), skipping the ones that would
            // poke through the face or into the floor.
            var golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (var i = 0; i < SpikeCount; i++)
            {
                var y = 1f - (i + 0.5f) / SpikeCount * 2f;
                var ring = Mathf.Sqrt(1f - y * y);
                var direction = new Vector3(Mathf.Cos(golden * i) * ring, y, Mathf.Sin(golden * i) * ring);

                if (Vector3.Dot(direction, Vector3.forward) > 0.45f || direction.y < -0.6f)
                {
                    continue;
                }

                // The base sits a little inside the body, so the spike grows out of it instead of
                // floating on its surface.
                Part(Cone(), "Spike", material,
                    direction * (bodyRadius * 0.85f),
                    Quaternion.LookRotation(direction),
                    new Vector3(thickness, thickness, length));
            }
        }

        void BuildFace(float bodyRadius, Palette palette)
        {
            // One big eye bulging out of the front.
            var eyeRadius = bodyRadius * 0.48f;
            var eyeCentre = new Vector3(0f, bodyRadius * 0.2f, bodyRadius * 0.72f);

            Part(Primitive(PrimitiveType.Sphere), "Eye", palette.Eye,
                eyeCentre, Quaternion.identity, Vector3.one * (eyeRadius * 2f));

            var pupilRadius = eyeRadius * 0.45f;
            Part(Primitive(PrimitiveType.Sphere), "Pupil", palette.Pupil,
                eyeCentre + Vector3.forward * (eyeRadius - pupilRadius * 0.35f),
                Quaternion.identity,
                new Vector3(pupilRadius * 1.1f, pupilRadius * 2f, pupilRadius * 1.2f));

            // A curved row of fangs under the eye, pointing down and a little forward.
            const int teeth = 5;
            var fangWidth = bodyRadius * 0.16f;
            var fangLength = bodyRadius * 0.34f;
            for (var i = 0; i < teeth; i++)
            {
                var angle = Mathf.Lerp(-40f, 40f, i / (teeth - 1f)) * Mathf.Deg2Rad;
                var position = new Vector3(
                    Mathf.Sin(angle) * bodyRadius * 0.85f,
                    -bodyRadius * 0.42f,
                    Mathf.Cos(angle) * bodyRadius * 0.85f);

                Part(Cone(), "Fang", palette.Eye,
                    position,
                    Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f) * Quaternion.Euler(75f, 0f, 0f),
                    new Vector3(fangWidth, fangWidth, fangLength));
            }
        }

        static GameObject Primitive(PrimitiveType shape)
        {
            var part = GameObject.CreatePrimitive(shape);

            // The hazard's own trigger decides contact. A collider on every part would make the
            // hit area depend on which way the monster happens to be facing.
            Destroy(part.GetComponent<Collider>());
            return part;
        }

        static GameObject Cone()
        {
            var part = new GameObject();
            part.AddComponent<MeshFilter>().sharedMesh = ConeMesh();
            part.AddComponent<MeshRenderer>();
            return part;
        }

        void Part(GameObject part, string name, Material material,
            Vector3 position, Quaternion rotation, Vector3 scale)
        {
            part.name = name;
            part.transform.SetParent(_visual, worldPositionStays: false);
            part.transform.localPosition = position;
            part.transform.localRotation = rotation;
            part.transform.localScale = scale;

            var renderer = part.GetComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;

            // Dozens of tiny shadows cost more than they add. The body alone casts one.
            renderer.shadowCastingMode = name == "Body" ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }

        /// <summary>
        /// A cone one unit long along +Z with a base one unit across, its base centred on the
        /// origin. Each face has its own vertices, so it shades as flat facets, not a smooth blur.
        /// There is no base cap; it is always buried in the body.
        /// </summary>
        static Mesh ConeMesh()
        {
            if (s_cone != null)
            {
                return s_cone;
            }

            const int sides = 8;
            var tip = new Vector3(0f, 0f, 1f);
            var vertices = new Vector3[sides * 3];
            var triangles = new int[sides * 3];

            for (var i = 0; i < sides; i++)
            {
                var a = (float)i / sides * Mathf.PI * 2f;
                var b = (float)(i + 1) / sides * Mathf.PI * 2f;

                // Wound so the face points outwards, which is the side Unity draws.
                vertices[i * 3] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.5f;
                vertices[i * 3 + 1] = new Vector3(Mathf.Cos(b), Mathf.Sin(b), 0f) * 0.5f;
                vertices[i * 3 + 2] = tip;

                triangles[i * 3] = i * 3;
                triangles[i * 3 + 1] = i * 3 + 1;
                triangles[i * 3 + 2] = i * 3 + 2;
            }

            s_cone = new Mesh { name = "Spike", vertices = vertices, triangles = triangles };
            s_cone.RecalculateNormals();
            s_cone.RecalculateBounds();
            return s_cone;
        }

        void Update()
        {
            if (_visual == null)
            {
                return;
            }

            var time = Time.time + _phase;

            _visual.localPosition = Vector3.up * (Mathf.Sin(time * BobSpeed) * BobHeight);
            _visual.localScale = Vector3.one * (1f + Mathf.Sin(time * BreatheSpeed) * BreatheAmount);

            FaceBall();
        }

        void FaceBall()
        {
            if (s_ball == null)
            {
                var ball = FindAnyObjectByType<BallController>();
                if (ball == null) return;
                s_ball = ball.transform;
            }

            // Turn on the spot only, so the monster never tips over to look down at the ball.
            var toBall = s_ball.position - transform.position;
            toBall.y = 0f;
            if (toBall.sqrMagnitude < 0.0001f)
            {
                return;
            }

            _visual.rotation = Quaternion.RotateTowards(
                _visual.rotation,
                Quaternion.LookRotation(toBall),
                TurnDegreesPerSecond * Time.deltaTime);
        }
    }
}
