using UnityEngine;
using UnityEngine.Tilemaps;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;

namespace AI_Mod.Runtime
{
    internal readonly struct PlayerSnapshot
    {
        internal static readonly PlayerSnapshot Empty = new PlayerSnapshot(null, Vector2.zero, Vector2.zero, 0f, 0f);

        internal PlayerSnapshot(CharacterController? controller, Vector2 position, Vector2 velocity, float radius, float moveSpeed)
        {
            Controller = controller;
            Position = position;
            Velocity = velocity;
            Radius = radius;
            MoveSpeed = moveSpeed;
        }

        internal CharacterController? Controller { get; }
        internal Vector2 Position { get; }
        internal Vector2 Velocity { get; }
        internal float Radius { get; }
        internal float MoveSpeed { get; }
        internal bool IsValid => Controller != null && !Controller.Equals(null);
    }

    internal readonly struct GemSnapshot
    {
        internal GemSnapshot(Vector2 position, float radius, bool collectible)
        {
            Position = position;
            Radius = radius;
            IsCollectible = collectible;
        }

        internal Vector2 Position { get; }
        internal float Radius { get; }
        internal bool IsCollectible { get; }
    }

    internal readonly struct EncirclementSnapshot
    {
        internal const int BinCount = 12;

        internal static readonly EncirclementSnapshot Empty = new EncirclementSnapshot(
            false,
            0f,
            0f,
            0f,
            0f,
            Vector2.zero,
            1f,
            0f,
            0f,
            0f);

        internal EncirclementSnapshot(
            bool hasRing,
            float coverage,
            float tightness,
            float closeness,
            float intensity,
            Vector2 breakoutDirection,
            float gapOccupancy,
            float ringRadius,
            float ringHalfWidth,
            float exitRadius)
        {
            HasRing = hasRing;
            Coverage = coverage;
            Tightness = tightness;
            Closeness = closeness;
            Intensity = intensity;
            BreakoutDirection = breakoutDirection;
            GapOccupancy = Mathf.Clamp01(gapOccupancy);
            RingRadius = Mathf.Max(ringRadius, 0f);
            RingHalfWidth = Mathf.Max(ringHalfWidth, 0f);
            ExitRadius = Mathf.Max(exitRadius, 0f);
        }

        internal bool HasRing { get; }
        internal float Coverage { get; }
        internal float Tightness { get; }
        internal float Closeness { get; }
        internal float Intensity { get; }
        internal Vector2 BreakoutDirection { get; }
        internal float GapOccupancy { get; }
        internal float RingRadius { get; }
        internal float RingHalfWidth { get; }
        internal float ExitRadius { get; }
        internal bool HasBreakoutDirection => BreakoutDirection.sqrMagnitude > 0.0001f;
    }

    internal readonly struct DynamicObstacle
    {
        internal DynamicObstacle(Vector2 position, Vector2 velocity, float radius, ObstacleKind kind)
        {
            Position = position;
            Velocity = velocity;
            Radius = radius;
            Kind = kind;
        }

        internal Vector2 Position { get; }
        internal Vector2 Velocity { get; }
        internal float Radius { get; }
        internal ObstacleKind Kind { get; }
    }

    internal readonly struct WallTilemap
    {
        internal WallTilemap(Tilemap tilemap, Bounds worldBounds, Rect[] boundingBoxes)
        {
            Tilemap = tilemap;
            WorldBounds = worldBounds;
            BoundingBoxes = boundingBoxes;
        }

        internal Tilemap Tilemap { get; }
        internal Bounds WorldBounds { get; }
        internal Rect[] BoundingBoxes { get; }
    }

    internal enum ObstacleKind
    {
        Enemy,
        Bullet
    }
}
