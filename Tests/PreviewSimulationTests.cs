using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class PreviewSimulationTests
    {
        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Input_DefaultBinding_TracksHeldKey", Action = Input_DefaultBinding_TracksHeldKey };
            yield return new TestCase { Name = "Input_SetBinding_DedupesExistingKey", Action = Input_SetBinding_DedupesExistingKey };
            yield return new TestCase { Name = "Input_DefaultAttackKeys_AreGAndR_FIsFree", Action = Input_DefaultAttackKeys_AreGAndR_FIsFree };
            yield return new TestCase { Name = "Input_Consume_IsOneShot", Action = Input_Consume_IsOneShot };
            yield return new TestCase { Name = "GroundContact_ConvertsImagePixelsToSceneOffset", Action = GroundContact_ConvertsImagePixelsToSceneOffset };
            yield return new TestCase { Name = "Player_MovesInInputDirection", Action = Player_MovesInInputDirection };
            yield return new TestCase { Name = "Player_JumpReturnsToTakeoffHeight", Action = Player_JumpReturnsToTakeoffHeight };
            yield return new TestCase { Name = "Animator_FallsBackToOppositeDirectionClip", Action = Animator_FallsBackToOppositeDirectionClip };
            yield return new TestCase { Name = "Animator_AdvancesAndLoopsFrames", Action = Animator_AdvancesAndLoopsFrames };
            yield return new TestCase { Name = "Animator_UndefinedJumpKeepsTheMovementAnimation", Action = Animator_UndefinedJumpKeepsTheMovementAnimation };
            yield return new TestCase { Name = "Player_MovementStateIgnoresJumpAndAttack", Action = Player_MovementStateIgnoresJumpAndAttack };
            yield return new TestCase { Name = "Terrain_PlayerClimbsTheSlopeAndIsBlockedByTheCliffWall", Action = Terrain_PlayerClimbsTheSlopeAndIsBlockedByTheCliffWall };
            yield return new TestCase { Name = "Terrain_PlayerFallsOffTheCliffEdgeAndLands", Action = Terrain_PlayerFallsOffTheCliffEdgeAndLands };
            yield return new TestCase { Name = "Terrain_PlatformCanBeJumpedThroughFromBelowAndStoodOn", Action = Terrain_PlatformCanBeJumpedThroughFromBelowAndStoodOn };
            yield return new TestCase { Name = "Jump_DefaultSettingReachesThePlatform", Action = Jump_DefaultSettingReachesThePlatform };
            yield return new TestCase { Name = "Terrain_WithoutTerrainKeepsTheFlatFloorBehavior", Action = Terrain_WithoutTerrainKeepsTheFlatFloorBehavior };
            yield return new TestCase { Name = "Effect_ResetsWhenLeavingSceneBounds", Action = Effect_ResetsWhenLeavingSceneBounds };
            yield return new TestCase { Name = "Effect_FliesInAnyDirectionAtTheGivenSpeed", Action = Effect_FliesInAnyDirectionAtTheGivenSpeed };
        }

        private static readonly Size SceneSize = new Size(640, 360);
        private static readonly Size SpriteSize = new Size(64, 64);

        private static float ConstantDuration(PlayerAnimationState state)
        {
            return 0.05f;
        }

        private static void Input_DefaultBinding_TracksHeldKey()
        {
            var input = new PreviewInputController();
            Assert.IsFalse(input.Right, "should start released");
            input.KeyDown(Keys.D);
            Assert.IsTrue(input.Right, "D is the default MoveRight binding");
            input.KeyUp(Keys.D);
            Assert.IsFalse(input.Right, "should release after KeyUp");
        }

        private static void Input_DefaultAttackKeys_AreGAndR_FIsFree()
        {
            var input = new PreviewInputController();
            Assert.AreEqual(Keys.G, input.GetBinding(PreviewAction.Attack1), "default Attack1");
            Assert.AreEqual(Keys.R, input.GetBinding(PreviewAction.Attack2), "default Attack2");
            foreach (PreviewAction action in System.Enum.GetValues(typeof(PreviewAction)))
                Assert.IsFalse(input.GetBinding(action) == Keys.F, "F is reserved for the fit-view shortcut (" + action + ")");
        }

        private static void GroundContact_ConvertsImagePixelsToSceneOffset()
        {
            // 64pxのセルを高さ100で表示 → 画像1px = 表示1.5625px
            Size display = new Size(100, 100);
            Assert.IsTrue(Math.Abs(GroundContact.ToSceneOffset(0, 64, display)) < 0.0001f, "zero offset stays zero");
            Assert.IsTrue(Math.Abs(GroundContact.ToSceneOffset(16, 64, display) - 25f) < 0.001f, "16px -> 25px (lowers the sprite)");
            Assert.IsTrue(Math.Abs(GroundContact.ToSceneOffset(-16, 64, display) + 25f) < 0.001f, "negative raises the sprite");
            float max = GroundContact.ToSceneOffset(GroundContact.MaxOffset, 64, display);
            Assert.IsTrue(Math.Abs(GroundContact.ToSceneOffset(9999, 64, display) - max) < 0.001f, "clamped to MaxOffset");
            Assert.IsTrue(GroundContact.ToSceneOffset(10, 0, display) == 0f, "invalid image height is ignored");
        }

        private static void Input_SetBinding_DedupesExistingKey()
        {
            var input = new PreviewInputController();
            Assert.AreEqual(Keys.A, input.GetBinding(PreviewAction.MoveLeft), "default MoveLeft binding");
            input.SetBinding(PreviewAction.MoveRight, Keys.A);
            Assert.AreEqual(Keys.A, input.GetBinding(PreviewAction.MoveRight), "MoveRight should now own A");
            Assert.AreEqual(Keys.None, input.GetBinding(PreviewAction.MoveLeft), "MoveLeft should be cleared to avoid a duplicate binding");
        }

        private static void Input_Consume_IsOneShot()
        {
            var input = new PreviewInputController();
            input.KeyDown(Keys.Space);
            Assert.IsTrue(input.Consume(PreviewAction.Jump), "first consume should observe the press");
            Assert.IsFalse(input.Consume(PreviewAction.Jump), "second consume without a new KeyDown should be false");
        }

        private static void Player_MovesInInputDirection()
        {
            var input = new PreviewInputController();
            input.KeyDown(Keys.D);
            var player = new PlayerStateController();
            float startX = player.Position.X;
            player.Update(0.1f, input, SceneSize, SpriteSize, ConstantDuration, 100f, 300f, 600f);
            Assert.IsTrue(player.Position.X > startX, "player should move right while D is held");
            Assert.AreEqual(PlayerAnimationState.MoveRight, player.State, "state should reflect the movement direction");
        }

        private static void Player_JumpReturnsToTakeoffHeight()
        {
            var input = new PreviewInputController();
            input.KeyDown(Keys.Space);
            var player = new PlayerStateController();
            float takeoffY = player.Position.Y;

            player.Update(0.02f, input, SceneSize, SpriteSize, ConstantDuration, 100f, 300f, 600f);
            Assert.IsTrue(player.Position.Y < takeoffY, "player should rise on the tick the jump is triggered");

            float elapsed = 0.02f;
            while (elapsed < 5f)
            {
                player.Update(0.02f, input, SceneSize, SpriteSize, ConstantDuration, 100f, 300f, 600f);
                elapsed += 0.02f;
            }
            Assert.InRange(player.Position.Y, takeoffY - 0.5, takeoffY + 0.5, "player should land back at takeoff height under symmetric gravity");
        }

        private static void Animator_FallsBackToOppositeDirectionClip()
        {
            var clips = new Dictionary<PlayerAnimationState, AnimationClipSettings>();
            clips[PlayerAnimationState.MoveRight] = new AnimationClipSettings { Enabled = true, StartCell = 1, EndCell = 3, Fps = 10 };
            clips[PlayerAnimationState.MoveLeft] = new AnimationClipSettings { Enabled = false };

            var animator = new SpriteAnimationController(clips);
            animator.Reset(PlayerAnimationState.MoveLeft, 3);

            Assert.AreEqual(PlayerAnimationState.MoveRight, animator.PlayingState, "should fall back to the only enabled directional clip");
            Assert.IsTrue(animator.IsOppositeDirectionFallback, "fallback across MoveLeft/MoveRight should be flagged as opposite-direction");
            Assert.IsTrue(animator.ShouldMirrorForFacing(false), "opposite-direction fallback should request a mirrored draw");
        }

        // ジャンプ/攻撃のクリップが1つも無いときは、代わりに移動（無ければ待機）のアニメーションを
        // 続ける。以前は待機に落ちて、移動しながらジャンプしても動きが止まって見えた。
        private static void Animator_UndefinedJumpKeepsTheMovementAnimation()
        {
            var clips = new Dictionary<PlayerAnimationState, AnimationClipSettings>();
            clips[PlayerAnimationState.Idle] = new AnimationClipSettings { Enabled = true, StartCell = 1, EndCell = 2, Fps = 10 };
            clips[PlayerAnimationState.MoveRight] = new AnimationClipSettings { Enabled = true, StartCell = 5, EndCell = 8, Fps = 10 };

            var animator = new SpriteAnimationController(clips);
            animator.Update(0.01, PlayerAnimationState.JumpRightAir, 8, PlayerAnimationState.MoveRight);
            Assert.AreEqual(PlayerAnimationState.MoveRight, animator.PlayingState, "no jump clip: keep the movement animation");
            animator.Update(0.5, PlayerAnimationState.JumpRightAir, 8, PlayerAnimationState.MoveRight);
            Assert.IsTrue(animator.CurrentCell >= 5 && animator.CurrentCell <= 8, "movement frames keep advancing during the jump");

            animator.Update(0.01, PlayerAnimationState.AttackRight1, 8, PlayerAnimationState.Idle);
            Assert.AreEqual(PlayerAnimationState.Idle, animator.PlayingState, "no attack clip: show idle when standing still");

            // ジャンプのクリップがあれば、そちらを使う
            clips[PlayerAnimationState.JumpRightAir] = new AnimationClipSettings { Enabled = true, StartCell = 3, EndCell = 4, Fps = 10 };
            animator.Update(0.01, PlayerAnimationState.JumpRightAir, 8, PlayerAnimationState.MoveRight);
            Assert.AreEqual(PlayerAnimationState.JumpRightAir, animator.PlayingState, "a defined jump clip is used");
        }

        private static void Player_MovementStateIgnoresJumpAndAttack()
        {
            var player = new PlayerStateController();
            var input = new PreviewInputController();
            player.Reset(SceneSize, SpriteSize);
            input.KeyDown(Keys.D);
            input.KeyDown(Keys.Space);
            player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 100, 300, 800);
            Assert.AreEqual(PlayerAnimationState.MoveRight, player.MovementState, "moving right while jumping");
            Assert.IsTrue(player.State != PlayerAnimationState.MoveRight, "the visible state is the jump");
            input.KeyUp(Keys.D);
            player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 100, 300, 800);
            Assert.AreEqual(PlayerAnimationState.Idle, player.MovementState, "standing still in the air");
        }

        private static PlayerStateController TerrainPlayer(Terrain terrain, float centerX, float feetY)
        {
            var player = new PlayerStateController();
            player.Reset(SceneSize, SpriteSize);
            player.Terrain = terrain;
            player.SetPoseForTests(centerX, feetY, SpriteSize);
            return player;
        }

        private static void Run(PlayerStateController player, PreviewInputController input, float seconds)
        {
            for (float t = 0; t < seconds; t += 0.016f)
                player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 200f, 500f, 800f);
        }

        private static float FeetY(PlayerStateController player) { return player.Position.Y + SpriteSize.Height; }
        private static float CenterX(PlayerStateController player) { return player.Position.X + SpriteSize.Width * 0.5f; }

        // 床は厚み12%。立っていれば足元は床の上面にあり、右へ歩くとスロープを登って崖の上（頂点）に立てる。
        // 崖の右側の低い地面から左へ歩くと、直角の壁で止まる。
        private static void Terrain_PlayerClimbsTheSlopeAndIsBlockedByTheCliffWall()
        {
            var terrain = new Terrain(SceneSize);
            Assert.InRange(terrain.GroundTop, 316, 318, "ground is 12% thick (360 - 43)");
            var input = new PreviewInputController();

            var player = TerrainPlayer(terrain, 200f, terrain.GroundTop);
            Run(player, input, 0.2f);
            Assert.InRange(FeetY(player), terrain.GroundTop - 0.6, terrain.GroundTop + 0.6, "stands on the flat ground");

            input.KeyDown(Keys.D);
            float highest = FeetY(player);
            for (float t = 0; t < 3f; t += 0.016f)
            {
                player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 200f, 500f, 800f);
                highest = Math.Min(highest, FeetY(player));
            }
            Assert.InRange(highest, terrain.PeakTop - 3, terrain.PeakTop + 3, "climbs the slope up to the top of the cliff");
            Assert.InRange(FeetY(player), terrain.GroundTop - 0.6, terrain.GroundTop + 0.6, "keeps walking off the cliff and lands on the low ground");
            input.KeyUp(Keys.D);

            // 崖の右側の低い地面から左へ
            var right = TerrainPlayer(terrain, terrain.SlopeEndX + 120f, terrain.GroundTop);
            var left = new PreviewInputController();
            left.KeyDown(Keys.A);
            Run(right, left, 4f);
            Assert.InRange(FeetY(right), terrain.GroundTop - 0.6, terrain.GroundTop + 0.6, "stays on the low ground (does not walk through the wall)");
            Assert.IsTrue(CenterX(right) - SpriteSize.Width * 0.25f >= terrain.SlopeEndX - 3, "blocked by the right-angle wall at x=" + terrain.SlopeEndX + " (center " + CenterX(right) + ")");
        }

        // 崖の上から右へ歩き出すと縁を越えて落ち、下の地面に着地する。
        private static void Terrain_PlayerFallsOffTheCliffEdgeAndLands()
        {
            var terrain = new Terrain(SceneSize);
            var player = TerrainPlayer(terrain, terrain.SlopeEndX - 10f, terrain.PeakTop);
            var input = new PreviewInputController();
            input.KeyDown(Keys.D);
            bool wasAirborne = false;
            for (float t = 0; t < 3f; t += 0.016f)
            {
                player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 200f, 500f, 800f);
                if (FeetY(player) < terrain.GroundTop - 5 && CenterX(player) > terrain.SlopeEndX + 20) wasAirborne = true;
            }
            Assert.IsTrue(wasAirborne, "falls after walking off the cliff");
            Assert.InRange(FeetY(player), terrain.GroundTop - 0.6, terrain.GroundTop + 0.6, "lands on the low ground");
        }

        // 足場は下から跳んですり抜けられ、落ちてくると上に乗れる。
        private static void Terrain_PlatformCanBeJumpedThroughFromBelowAndStoodOn()
        {
            var terrain = new Terrain(SceneSize);
            float centerX = (terrain.PlatformLeft + terrain.PlatformRight) * 0.5f;
            var player = TerrainPlayer(terrain, centerX, terrain.GroundTop);
            var input = new PreviewInputController();
            Run(player, input, 0.1f);
            Assert.InRange(FeetY(player), terrain.GroundTop - 0.6, terrain.GroundTop + 0.6, "starts on the ground under the platform");

            input.KeyDown(Keys.Space);
            float highestFeet = FeetY(player);
            for (float t = 0; t < 3f; t += 0.016f)
            {
                player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 200f, 500f, 800f);
                highestFeet = Math.Min(highestFeet, FeetY(player));
                input.KeyUp(Keys.Space);   // 1回だけ跳ぶ
            }
            Assert.IsTrue(highestFeet < terrain.PlatformTop - 10, "jumped up through the platform (highest feet " + highestFeet + ")");
            Assert.InRange(FeetY(player), terrain.PlatformTop - 0.6, terrain.PlatformTop + 0.6, "lands on top of the platform");
            Run(player, input, 1f);
            Assert.InRange(FeetY(player), terrain.PlatformTop - 0.6, terrain.PlatformTop + 0.6, "keeps standing on the platform");
        }

        // ジャンプ力の設定値 1.0（既定）で、見本の足場に乗れる。以前の 1.6 と同じ高さ。
        private static void Jump_DefaultSettingReachesThePlatform()
        {
            Assert.InRange(PlayerStateController.JumpForceFromSetting(1f, 360f), 431.9, 432.1, "setting 1.0 = the old 1.6 (1.6 x 360 x 0.75)");
            var terrain = new Terrain(SceneSize);
            float centerX = (terrain.PlatformLeft + terrain.PlatformRight) * 0.5f;
            var player = TerrainPlayer(terrain, centerX, terrain.GroundTop);
            var input = new PreviewInputController();
            float jumpForce = PlayerStateController.JumpForceFromSetting(1f, SceneSize.Height);
            float gravity = 1f * SceneSize.Height * 2.2f;      // 設定値 1.0 の重力（MainForm と同じ換算）
            input.KeyDown(Keys.Space);
            for (float t = 0; t < 3f; t += 0.016f)
            {
                player.Update(0.016f, input, SceneSize, SpriteSize, ConstantDuration, 200f, jumpForce, gravity);
                input.KeyUp(Keys.Space);
            }
            Assert.InRange(FeetY(player), terrain.PlatformTop - 0.6, terrain.PlatformTop + 0.6, "with the default jump power the character lands on the platform");
        }

        // 地形を使わない（従来）ときは、平らな床の上で上下キーによる奥行き移動もできる。
        private static void Terrain_WithoutTerrainKeepsTheFlatFloorBehavior()
        {
            var player = new PlayerStateController();
            player.Reset(SceneSize, SpriteSize);
            var input = new PreviewInputController();
            input.KeyDown(Keys.W);
            float startY = player.Position.Y;
            Run(player, input, 0.3f);
            Assert.IsTrue(player.Position.Y < startY - 5, "W moves the player up when no terrain is used");
            float expectedGround = SceneSize.Height - SpriteSize.Height - (float)Math.Round(SceneSize.Height * Terrain.GroundThicknessRatio);
            var fresh = new PlayerStateController();
            fresh.Reset(SceneSize, SpriteSize);
            Assert.InRange(fresh.Position.Y, expectedGround - 0.6, expectedGround + 0.6, "the start position stands on the 12% thick floor");
        }

        private static void Animator_AdvancesAndLoopsFrames()
        {
            var clips = new Dictionary<PlayerAnimationState, AnimationClipSettings>();
            clips[PlayerAnimationState.Idle] = new AnimationClipSettings { Enabled = true, StartCell = 5, EndCell = 7, Fps = 10 };

            var animator = new SpriteAnimationController(clips);
            animator.Reset(PlayerAnimationState.Idle, 7);
            Assert.AreEqual(5, animator.CurrentCell, "should start at the clip's StartCell");

            animator.Update(0.1, PlayerAnimationState.Idle, 7);
            Assert.AreEqual(6, animator.CurrentCell, "should advance one frame per 1/Fps seconds");

            animator.Update(0.1, PlayerAnimationState.Idle, 7);
            Assert.AreEqual(7, animator.CurrentCell, "should reach EndCell");

            animator.Update(0.1, PlayerAnimationState.Idle, 7);
            Assert.AreEqual(5, animator.CurrentCell, "should loop back to StartCell after EndCell");
        }

        // 方向は任意のベクトル（斜め・ゆるい角度も可）。長さに関わらず進む速さは speed で決まる。
        private static void Effect_FliesInAnyDirectionAtTheGivenSpeed()
        {
            foreach (var direction in new[] { new PointF(0.3f, -0.9f), new PointF(0.05f, 0.02f), new PointF(-0.7f, 0.7f), new PointF(1f, 0f) })
            {
                var effect = new EffectMotionController();
                var scene = new Size(640, 360);
                var sprite = new Size(32, 32);
                effect.Reset(scene, sprite);
                PointF start = effect.Position;
                effect.Update(0.1f, direction, 100f, scene, sprite);
                float dx = effect.Position.X - start.X, dy = effect.Position.Y - start.Y;
                Assert.InRange(Math.Sqrt(dx * dx + dy * dy), 9.9, 10.1, "distance = speed x time for " + direction);
                float length = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
                Assert.InRange(dx / 10f, direction.X / length - 0.01, direction.X / length + 0.01, "x component follows the vector " + direction);
                Assert.InRange(dy / 10f, direction.Y / length - 0.01, direction.Y / length + 0.01, "y component follows the vector " + direction);
            }
            var still = new EffectMotionController();
            still.Reset(new Size(640, 360), new Size(32, 32));
            PointF before = still.Position;
            still.Update(0.1f, new PointF(0, 0), 100f, new Size(640, 360), new Size(32, 32));
            Assert.AreEqual(before, still.Position, "a zero vector does not move");
        }

        private static void Effect_ResetsWhenLeavingSceneBounds()
        {
            var effect = new EffectMotionController();
            effect.Reset(SceneSize, SpriteSize);
            PointF center = effect.Position;

            effect.Update(0.1f, new PointF(1, 0), 50f, SceneSize, SpriteSize);
            Assert.IsTrue(effect.Position.X > center.X, "should move toward the given direction");

            effect.Update(1f, new PointF(1, 0), 10000f, SceneSize, SpriteSize);
            Assert.InRange(effect.Position.X, center.X - 0.01, center.X + 0.01, "leaving the scene bounds should reset back to the centered start position");
        }
    }
}
