//==================================================
// ColliderTests
// キャラクターのコライダー（当たり判定の箱）: 画像ピクセル→シーンの換算、0 の自動、
// 画像の中心に置いたときの接地位置、幅による壁での止まり方、プロジェクトへの保存。
//==================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SpriteSheetMaker;

namespace SpriteSheetMakerTests
{
    internal static class ColliderTests
    {
        private static readonly Size SceneSize = new Size(640, 360);
        private static readonly Size SpriteSize = new Size(64, 64);

        public static IEnumerable<TestCase> All()
        {
            yield return new TestCase { Name = "Collider_ToScene_ConvertsPixelsAndZeroIsAutomatic", Action = ToScene_ConvertsPixelsAndZeroIsAutomatic };
            yield return new TestCase { Name = "Collider_Automatic_MatchesThePreviousBehavior", Action = Automatic_MatchesThePreviousBehavior };
            yield return new TestCase { Name = "Collider_IsCenteredOnTheImageAndItsBottomStandsOnTheGround", Action = IsCenteredOnTheImageAndItsBottomStandsOnTheGround };
            yield return new TestCase { Name = "Collider_WidthDecidesWhereTheWallStops", Action = WidthDecidesWhereTheWallStops };
            yield return new TestCase { Name = "Collider_WideColliderStandsHigherOnTheSlope", Action = WideColliderStandsHigherOnTheSlope };
        }

        private static float ConstantDuration(PlayerAnimationState state) { return 0.2f; }

        private static void ToScene_ConvertsPixelsAndZeroIsAutomatic()
        {
            // 画像 32x32 をシーンで 64x64 に表示 → 2倍
            SizeF box = ColliderSize.ToScene(10, 20, new Size(32, 32), new Size(64, 64));
            Assert.AreEqual(20f, box.Width, "width in scene units");
            Assert.AreEqual(40f, box.Height, "height in scene units");
            SizeF auto = ColliderSize.ToScene(0, 0, new Size(32, 32), new Size(64, 64));
            Assert.AreEqual(32f, auto.Width, "0 width = half the image");
            Assert.AreEqual(64f, auto.Height, "0 height = the image height");
            SizeF mixed = ColliderSize.ToScene(0, 8, new Size(32, 32), new Size(64, 64));
            Assert.AreEqual(32f, mixed.Width, "each axis is automatic on its own");
            Assert.AreEqual(16f, mixed.Height, "height still converted");
        }

        // 自動のコライダーを渡しても、渡さない古い呼び方と同じ動きになる。
        private static void Automatic_MatchesThePreviousBehavior()
        {
            var terrain = new Terrain(SceneSize);
            var oldWay = Player(terrain, 200f);
            var newWay = Player(terrain, 200f);
            var oldInput = new PreviewInputController();   // ジャンプは1回で消費されるので入力は別々に持つ
            var newInput = new PreviewInputController();
            oldInput.KeyDown(Keys.D);
            newInput.KeyDown(Keys.D);
            for (int i = 0; i < 300; i++)
            {
                if (i == 40) { oldInput.KeyDown(Keys.Space); newInput.KeyDown(Keys.Space); }
                oldWay.Update(0.016f, oldInput, SceneSize, SpriteSize, ConstantDuration, 200f, 500f, 800f);
                newWay.Update(0.016f, newInput, SceneSize, SpriteSize, ColliderSize.Automatic(SpriteSize), ConstantDuration, 200f, 500f, 800f);
                Assert.AreEqual(oldWay.Position, newWay.Position, "same position at step " + i);
            }
        }

        // 画像より高いコライダーは、その下端が地面に着くので、画像は (コライダー高 - 画像高)/2 だけ浮く。
        private static void IsCenteredOnTheImageAndItsBottomStandsOnTheGround()
        {
            var terrain = new Terrain(SceneSize);
            var player = Player(terrain, 200f);
            var input = new PreviewInputController();
            var tall = new SizeF(32, 100);
            for (int i = 0; i < 120; i++)
                player.Update(0.016f, input, SceneSize, SpriteSize, tall, ConstantDuration, 200f, 500f, 800f);
            float spriteCenterY = player.Position.Y + SpriteSize.Height * 0.5f;
            Assert.InRange(spriteCenterY + tall.Height * 0.5f, terrain.GroundTop - 0.6, terrain.GroundTop + 0.6, "collider bottom on the ground");
            Assert.InRange(terrain.GroundTop - (player.Position.Y + SpriteSize.Height), 17.4, 18.6, "image floats by (100 - 64) / 2");

            var shortBox = new SizeF(32, 40);
            var low = Player(terrain, 200f);
            for (int i = 0; i < 120; i++)
                low.Update(0.016f, input, SceneSize, SpriteSize, shortBox, ConstantDuration, 200f, 500f, 800f);
            Assert.InRange((low.Position.Y + SpriteSize.Height) - terrain.GroundTop, 11.4, 12.6, "a shorter collider sinks the image by (64 - 40) / 2");
        }

        // 崖の右の低い地面から左へ歩くと、コライダーの左端が壁で止まる（細いほど壁に近づける）。
        private static void WidthDecidesWhereTheWallStops()
        {
            var terrain = new Terrain(SceneSize);
            Func<float, float> stopX = width =>
            {
                var player = Player(terrain, terrain.SlopeEndX + 120f);
                var input = new PreviewInputController();
                input.KeyDown(Keys.A);
                for (int i = 0; i < 300; i++)
                    player.Update(0.016f, input, SceneSize, SpriteSize, new SizeF(width, 64), ConstantDuration, 200f, 500f, 800f);
                return player.Position.X + SpriteSize.Width * 0.5f;
            };
            float narrow = stopX(8f);
            float wide = stopX(60f);
            Assert.InRange(narrow - terrain.SlopeEndX, 2, 6, "narrow collider: center stops half its width from the wall");
            Assert.InRange(wide - terrain.SlopeEndX, 28, 32, "wide collider: center stops further away");
        }

        // 坂の途中では、コライダーの右端（坂の上側）が地面に触れるので、幅が広いほど高く立つ。
        private static void WideColliderStandsHigherOnTheSlope()
        {
            var terrain = new Terrain(SceneSize);
            float middle = (terrain.SlopeStartX + terrain.SlopeEndX) * 0.5f;
            Func<float, float> feetAt = width =>
            {
                var player = Player(terrain, middle);
                var input = new PreviewInputController();
                for (int i = 0; i < 120; i++)
                    player.Update(0.016f, input, SceneSize, SpriteSize, new SizeF(width, 64), ConstantDuration, 200f, 500f, 800f);
                Assert.InRange(player.Position.X + SpriteSize.Width * 0.5f, middle - 0.01, middle + 0.01, "does not slide");
                return player.Position.Y + SpriteSize.Height;   // 高さ64のコライダーなので画像の下端＝足元
            };
            float slope = (terrain.GroundTop - terrain.PeakTop) / (terrain.SlopeEndX - terrain.SlopeStartX);
            float narrow = feetAt(2f);
            float wide = feetAt(60f);
            Assert.InRange(terrain.SurfaceAt(middle) - narrow, 0, 1.5, "a thin collider stands on the slope under its center");
            Assert.InRange(narrow - wide, 29 * slope - 1.5, 29 * slope + 1.5, "a wide collider rests on its uphill corner");
        }

        private static PlayerStateController Player(Terrain terrain, float centerX)
        {
            var player = new PlayerStateController();
            player.Reset(SceneSize, SpriteSize);
            player.Terrain = terrain;
            player.SetPoseForTests(centerX, terrain.GroundTop, SpriteSize);
            return player;
        }
    }
}
