using System.Collections.Generic;
using UnityEngine;

namespace AI_Mod.Runtime.Brain
{
    internal readonly struct PlannerResult
    {
        internal static readonly PlannerResult Zero = new PlannerResult(Vector2.zero, SteeringMode.Idle);

        internal PlannerResult(Vector2 direction, SteeringMode mode)
        {
            Direction = direction;
            Mode = mode;
        }

        internal Vector2 Direction { get; }
        internal SteeringMode Mode { get; }
    }

    internal enum SteeringMode
    {
        Idle,
        VelocityObstacle,
        Kiting,
        Fallback,
        Breakout
    }

    internal sealed class PlannerDebugInfo
    {
        private readonly List<PlannerCandidate> _candidates = new List<PlannerCandidate>(32);
        private readonly List<Vector2> _bestTrajectory = new List<Vector2>();
        private float _bestScore = float.MinValue;
        private Vector2 _bestDirection = Vector2.zero;
        private bool _hasBest;
        private float _bestEnemyOverlapSeconds;
        private float _bestBulletOverlapSeconds;
        private float _bestBreakoutExitTime;
        private bool _bestBreakoutActive;
        private float _encirclementIntensity;

        internal IReadOnlyList<PlannerCandidate> Candidates => _candidates;
        internal IReadOnlyList<Vector2> BestTrajectory => _bestTrajectory;
        internal float BestScore => _hasBest ? _bestScore : float.MinValue;
        internal Vector2 BestDirection => _hasBest ? _bestDirection : Vector2.zero;
        internal bool HasBest => _hasBest;
        internal float BestEnemyOverlapSeconds => _hasBest ? _bestEnemyOverlapSeconds : 0f;
        internal float BestBulletOverlapSeconds => _hasBest ? _bestBulletOverlapSeconds : 0f;
        internal float BestTotalOverlapSeconds => _hasBest ? _bestEnemyOverlapSeconds + _bestBulletOverlapSeconds : 0f;
        internal float BestBreakoutExitTime => _hasBest ? _bestBreakoutExitTime : float.PositiveInfinity;
        internal bool BreakoutActive => _hasBest && _bestBreakoutActive;
        internal float EncirclementIntensity => _hasBest ? _encirclementIntensity : 0f;

        internal void Begin()
        {
            _candidates.Clear();
            _bestTrajectory.Clear();
            _bestDirection = Vector2.zero;
            _bestScore = float.MinValue;
            _hasBest = false;
            _bestEnemyOverlapSeconds = 0f;
            _bestBulletOverlapSeconds = 0f;
            _bestBreakoutExitTime = float.PositiveInfinity;
            _bestBreakoutActive = false;
            _encirclementIntensity = 0f;
        }

        internal void RecordCandidate(Vector2 direction, float score, float enemyOverlapSeconds, float bulletOverlapSeconds)
        {
            _candidates.Add(new PlannerCandidate(direction, score, enemyOverlapSeconds, bulletOverlapSeconds));
        }

        internal void RecordBest(
            Vector2 direction,
            float score,
            IReadOnlyList<Vector2> trajectory,
            float enemyOverlapSeconds,
            float bulletOverlapSeconds,
            float breakoutExitTime,
            bool breakoutActive,
            float encirclementIntensity)
        {
            _bestDirection = direction;
            _bestScore = score;
            _hasBest = true;
            _bestEnemyOverlapSeconds = enemyOverlapSeconds;
            _bestBulletOverlapSeconds = bulletOverlapSeconds;
            _bestBreakoutExitTime = breakoutExitTime;
            _bestBreakoutActive = breakoutActive;
            _encirclementIntensity = encirclementIntensity;

            _bestTrajectory.Clear();
            if (trajectory != null)
            {
                for (var i = 0; i < trajectory.Count; i++)
                {
                    _bestTrajectory.Add(trajectory[i]);
                }
            }
        }
    }

    internal readonly struct PlannerCandidate
    {
        internal PlannerCandidate(Vector2 direction, float score, float enemyOverlapSeconds, float bulletOverlapSeconds)
        {
            Direction = direction;
            Score = score;
            EnemyOverlapSeconds = enemyOverlapSeconds;
            BulletOverlapSeconds = bulletOverlapSeconds;
        }

        internal Vector2 Direction { get; }
        internal float Score { get; }
        internal float EnemyOverlapSeconds { get; }
        internal float BulletOverlapSeconds { get; }
    }
}
