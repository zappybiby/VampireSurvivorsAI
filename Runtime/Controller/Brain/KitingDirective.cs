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
            float radiusSpread,
            Vector2 radialDirection,
            Vector2 orbitDirection,
            Vector2 alternateOrbitDirection,
            float currentRadius,
            float swarmSpeed,
            float swarmOutrunBias,
            float clearanceScore,
            bool fallbackRequested,
            int sampleCount,
            float lanePenalty,
            float laneScore,
            float stragglerScore,
            float gemScore,
            float escapeScore,
            int laneOrientation,
            float radialAdjustment,
            float alternateLanePenalty,
            float alternateLaneScore,
            int alternateLaneOrientation,
            float arcHalfAngleDegrees,
            float arcAngleDegrees,
            Vector2 arcMidlineDirection,
            int arcDirectionSign)
        {
            Anchor = anchor;
            PreferredRadius = preferredRadius;
            RadiusTolerance = radiusTolerance;
            RadiusSpread = radiusSpread;
            RadialDirection = radialDirection;
            OrbitDirection = orbitDirection;
            AlternateOrbitDirection = alternateOrbitDirection;
            CurrentRadius = currentRadius;
            SwarmSpeed = swarmSpeed;
            SwarmOutrunBias = swarmOutrunBias;
            ClearanceScore = clearanceScore;
            FallbackRequested = fallbackRequested;
            SampleCount = sampleCount;
            LanePenalty = lanePenalty;
            LaneScore = laneScore;
            StragglerScore = stragglerScore;
            GemScore = gemScore;
            EscapeScore = escapeScore;
            LaneOrientation = laneOrientation;
            RadialAdjustment = radialAdjustment;
            AlternateLanePenalty = alternateLanePenalty;
            AlternateLaneScore = alternateLaneScore;
            AlternateLaneOrientation = alternateLaneOrientation;
            ArcHalfAngleDegrees = arcHalfAngleDegrees;
            ArcAngleDegrees = arcAngleDegrees;
            ArcMidlineDirection = arcMidlineDirection;
            ArcDirectionSign = arcDirectionSign;
            HasDirective = true;
        }

        internal bool HasDirective { get; }
        internal Vector2 Anchor { get; }
        internal float PreferredRadius { get; }
        internal float RadiusTolerance { get; }
        internal float RadiusSpread { get; }
        internal Vector2 RadialDirection { get; }
        internal Vector2 OrbitDirection { get; }
        internal Vector2 AlternateOrbitDirection { get; }
        internal float CurrentRadius { get; }
        internal float SwarmSpeed { get; }
        internal float SwarmOutrunBias { get; }
        internal float ClearanceScore { get; }
        internal bool FallbackRequested { get; }
        internal int SampleCount { get; }
        internal float LanePenalty { get; }
        internal float LaneScore { get; }
        internal float StragglerScore { get; }
        internal float GemScore { get; }
        internal float EscapeScore { get; }
        internal int LaneOrientation { get; }
        internal float RadialAdjustment { get; }
        internal float AlternateLanePenalty { get; }
        internal float AlternateLaneScore { get; }
        internal int AlternateLaneOrientation { get; }
        internal bool HasAlternateOrbit => AlternateOrbitDirection.sqrMagnitude > 0.5f;
        internal float ArcHalfAngleDegrees { get; }
        internal float ArcAngleDegrees { get; }
        internal Vector2 ArcMidlineDirection { get; }
        internal int ArcDirectionSign { get; }

        internal static KitingDirective Create(
            Vector2 anchor,
            float preferredRadius,
            float radiusTolerance,
            float radiusSpread,
            Vector2 radialDirection,
            Vector2 orbitDirection,
            Vector2 alternateOrbitDirection,
            float currentRadius,
            float swarmSpeed,
            float swarmOutrunBias,
            float clearanceScore,
            bool fallbackRequested,
            int sampleCount,
            float lanePenalty,
            float laneScore,
            float stragglerScore,
            float gemScore,
            float escapeScore,
            int laneOrientation,
            float radialAdjustment,
            float alternateLanePenalty,
            float alternateLaneScore,
            int alternateLaneOrientation,
            float arcHalfAngleDegrees,
            float arcAngleDegrees,
            Vector2 arcMidlineDirection,
            int arcDirectionSign)
        {
            return new KitingDirective(
                anchor,
                preferredRadius,
                radiusTolerance,
                radiusSpread,
                radialDirection,
                orbitDirection,
                alternateOrbitDirection,
                currentRadius,
                swarmSpeed,
                swarmOutrunBias,
                clearanceScore,
                fallbackRequested,
                sampleCount,
                lanePenalty,
                laneScore,
                stragglerScore,
                gemScore,
                escapeScore,
                laneOrientation,
                radialAdjustment,
                alternateLanePenalty,
                alternateLaneScore,
                alternateLaneOrientation,
                arcHalfAngleDegrees,
                arcAngleDegrees,
                arcMidlineDirection,
                arcDirectionSign);
        }
    }
}
