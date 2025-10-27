using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using CharacterController = Il2CppVampireSurvivors.Objects.Characters.CharacterController;
using Stage = Il2CppVampireSurvivors.Objects.Stage;

namespace AI_Mod.Runtime
{
    internal sealed partial class AiWorldState
    {
        private readonly List<DynamicObstacle> _enemies = new List<DynamicObstacle>();
        private readonly List<DynamicObstacle> _bullets = new List<DynamicObstacle>();
        private readonly List<GemSnapshot> _gems = new List<GemSnapshot>();
        private readonly List<WallTilemap> _wallTilemaps = new List<WallTilemap>();
        private readonly FallbackLogger _fallbacks = new FallbackLogger();

        private Stage? _stage;
        private bool _wallsCached;
        private int _version = -1;
        private EncirclementSnapshot _encirclement = EncirclementSnapshot.Empty;

        internal PlayerSnapshot Player { get; private set; } = PlayerSnapshot.Empty;
        internal IReadOnlyList<DynamicObstacle> EnemyObstacles => _enemies;
        internal IReadOnlyList<DynamicObstacle> BulletObstacles => _bullets;
        internal IReadOnlyList<GemSnapshot> Gems => _gems;
        internal IReadOnlyList<WallTilemap> WallTilemaps => _wallTilemaps;

        [HideFromIl2Cpp]
        internal int Version => _version;

        internal EncirclementSnapshot Encirclement => _encirclement;

        internal void ClearTransient()
        {
            _enemies.Clear();
            _bullets.Clear();
            _gems.Clear();
            _wallTilemaps.Clear();
            _wallsCached = false;
            _stage = null;
            _fallbacks.ResetTransient();
            Player = PlayerSnapshot.Empty;
            _version = -1;
            _encirclement = EncirclementSnapshot.Empty;
        }

        internal void Refresh(CharacterController controller)
        {
            Player = BuildPlayerSnapshot(controller);
            RefreshEnemies();
            RefreshBullets();
            RefreshGems();
            RefreshEncirclement();

            if (!_wallsCached)
            {
                RefreshWalls();
                _wallsCached = true;
            }

            if (_version == int.MaxValue)
            {
                _fallbacks.WarnOnce("WorldVersionWrap", "World snapshot version wrapped to zero; TODO: monitor long sessions.");
                _version = 0;
                return;
            }

            _version = _version < 0 ? 0 : _version + 1;
        }

        private Stage? EnsureStageReference()
        {
            if (_stage != null && !_stage.Equals(null))
            {
                var go = _stage.gameObject;
                if (go != null && go.activeInHierarchy)
                {
                    return _stage;
                }
            }

            _stage = null;

            var stages = UnityEngine.Object.FindObjectsOfType<Stage>(true);
            if (stages == null)
            {
                return null;
            }

            for (var i = 0; i < stages.Length; i++)
            {
                var candidate = stages[i];
                if (candidate == null || candidate.Equals(null))
                {
                    continue;
                }

                var go = candidate.gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    continue;
                }

                _stage = candidate;
                break;
            }

            return _stage;
        }
    }
}
