using UnityEngine;

namespace AI_Mod.Runtime.Brain
{
    internal readonly struct KitingDirective
    {
        internal static readonly KitingDirective None = new KitingDirective();

        private KitingDirective(
            Vector2 anchor,
            float preferredRadius,
            float radiusTolerance,
            Vector2 radialDirection,
            Vector2 orbitDirection,
            Vector2 alternateOrbitDirection,
            float currentRadius,
            float swarmSpeed,
            float swarmOutrunBias,
            float clearanceScore,
            bool fallbackRequested,
            int sampleCount)
        {
            Anchor = anchor;
            PreferredRadius = preferredRadius;
            RadiusTolerance = radiusTolerance;
            RadialDirection = radialDirection;
            OrbitDirection = orbitDirection;
            AlternateOrbitDirection = alternateOrbitDirection;
            CurrentRadius = currentRadius;
            SwarmSpeed = swarmSpeed;
            SwarmOutrunBias = swarmOutrunBias;
            ClearanceScore = clearanceScore;
            FallbackRequested = fallbackRequested;
            SampleCount = sampleCount;
            HasDirective = true;
        }

        internal bool HasDirective { get; }
        internal Vector2 Anchor { get; }
        internal float PreferredRadius { get; }
        internal float RadiusTolerance { get; }
        internal Vector2 RadialDirection { get; }
        internal Vector2 OrbitDirection { get; }
        internal Vector2 AlternateOrbitDirection { get; }
        internal float CurrentRadius { get; }
        internal float SwarmSpeed { get; }
        internal float SwarmOutrunBias { get; }
        internal float ClearanceScore { get; }
        internal bool FallbackRequested { get; }
        internal int SampleCount { get; }
        internal bool HasAlternateOrbit => AlternateOrbitDirection.sqrMagnitude > 0.5f;

        internal static KitingDirective Create(
            Vector2 anchor,
            float preferredRadius,
            float radiusTolerance,
            Vector2 radialDirection,
            Vector2 orbitDirection,
            Vector2 alternateOrbitDirection,
            float currentRadius,
            float swarmSpeed,
            float swarmOutrunBias,
            float clearanceScore,
            bool fallbackRequested,
            int sampleCount)
        {
            return new KitingDirective(
                anchor,
                preferredRadius,
                radiusTolerance,
                radialDirection,
                orbitDirection,
                alternateOrbitDirection,
                currentRadius,
                swarmSpeed,
                swarmOutrunBias,
                clearanceScore,
                fallbackRequested,
                sampleCount);
        }
    }
}
