using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SpriteSheetMaker
{
    internal enum PreviewTargetMode
    {
        Standard,
        Player,
        Effect
    }

    internal enum PlayerAnimationState
    {
        Idle,
        Move,
        MoveRight,
        MoveLeft,
        MoveUp,
        MoveDown,
        Jump,
        JumpStart,
        JumpAir,
        JumpLand,
        JumpRightStart,
        JumpRightAir,
        JumpRightLand,
        JumpLeftStart,
        JumpLeftAir,
        JumpLeftLand,
        AttackRight1,
        AttackRight2,
        AttackLeft1,
        AttackLeft2
    }

    internal enum PreviewAction
    {
        MoveLeft,
        MoveRight,
        MoveUp,
        MoveDown,
        Jump,
        Attack1,
        Attack2
    }

    internal sealed class AnimationClipSettings
    {
        public bool Enabled;
        public int StartCell = 1;
        public int EndCell = 1;
        public int Fps = 12;

        public AnimationClipSettings Clone()
        {
            return new AnimationClipSettings
            {
                Enabled = Enabled,
                StartCell = StartCell,
                EndCell = EndCell,
                Fps = Fps
            };
        }
    }

    internal sealed class PreviewInputController
    {
        private readonly HashSet<Keys> held = new HashSet<Keys>();
        private readonly HashSet<PreviewAction> pressed = new HashSet<PreviewAction>();
        private readonly Dictionary<PreviewAction, Keys> bindings = new Dictionary<PreviewAction, Keys>();

        public PreviewInputController()
        {
            bindings[PreviewAction.MoveLeft] = Keys.A;
            bindings[PreviewAction.MoveRight] = Keys.D;
            bindings[PreviewAction.MoveUp] = Keys.W;
            bindings[PreviewAction.MoveDown] = Keys.S;
            bindings[PreviewAction.Jump] = Keys.Space;
            bindings[PreviewAction.Attack1] = Keys.G;
            bindings[PreviewAction.Attack2] = Keys.R;
        }

        // キー・マウスが新しく押されたときに知らせる（待機中に間引いている再生タイマーを、すぐ通常の間隔へ戻すため）。
        public event Action Activity;

        // 押されているキーや、まだ処理されていない押下があるか。
        public bool AnyActive { get { return held.Count > 0 || pressed.Count > 0; } }

        public bool Left { get { return IsDown(PreviewAction.MoveLeft); } }
        public bool Right { get { return IsDown(PreviewAction.MoveRight); } }
        public bool Up { get { return IsDown(PreviewAction.MoveUp); } }
        public bool Down { get { return IsDown(PreviewAction.MoveDown); } }

        public IDictionary<PreviewAction, Keys> GetBindingsCopy()
        {
            return new Dictionary<PreviewAction, Keys>(bindings);
        }

        public Keys GetBinding(PreviewAction action)
        {
            Keys key;
            return bindings.TryGetValue(action, out key) ? key : Keys.None;
        }

        public void SetBinding(PreviewAction action, Keys key)
        {
            PreviewAction? duplicate = null;
            foreach (KeyValuePair<PreviewAction, Keys> pair in bindings)
                if (pair.Key != action && pair.Value == key) duplicate = pair.Key;
            if (duplicate.HasValue) bindings[duplicate.Value] = Keys.None;
            bindings[action] = key;
            Clear();
        }

        public void RestoreBindings(IDictionary<PreviewAction, Keys> source)
        {
            bindings.Clear();
            foreach (KeyValuePair<PreviewAction, Keys> pair in source) bindings[pair.Key] = pair.Value;
            Clear();
        }

        public bool Handles(Keys key)
        {
            return bindings.ContainsValue(key) && key != Keys.None;
        }

        public void MouseDown(MouseButtons button)
        {
            Keys key = MouseButtonToKey(button);
            if (key != Keys.None) KeyDown(key);
        }

        public void MouseUp(MouseButtons button)
        {
            Keys key = MouseButtonToKey(button);
            if (key != Keys.None) KeyUp(key);
        }

        private static Keys MouseButtonToKey(MouseButtons button)
        {
            switch (button)
            {
                case MouseButtons.Left: return Keys.LButton;
                case MouseButtons.Right: return Keys.RButton;
                case MouseButtons.Middle: return Keys.MButton;
                case MouseButtons.XButton1: return Keys.XButton1;
                case MouseButtons.XButton2: return Keys.XButton2;
                default: return Keys.None;
            }
        }

        public void KeyDown(Keys key)
        {
            if (!held.Contains(key))
            {
                foreach (KeyValuePair<PreviewAction, Keys> pair in bindings)
                    if (pair.Value == key) pressed.Add(pair.Key);
            }
            held.Add(key);
            Action activity = Activity;
            if (activity != null) activity();
        }

        public void KeyUp(Keys key)
        {
            held.Remove(key);
        }

        private bool IsDown(PreviewAction action)
        {
            Keys key = GetBinding(action);
            return key != Keys.None && held.Contains(key);
        }

        public bool Consume(PreviewAction action)
        {
            bool value = pressed.Contains(action);
            pressed.Remove(action);
            return value;
        }

        public PlayerAnimationState? ConsumeAttackState(bool facingRight)
        {
            PreviewAction[] actions = { PreviewAction.Attack1, PreviewAction.Attack2 };
            PlayerAnimationState[] rightStates = { PlayerAnimationState.AttackRight1, PlayerAnimationState.AttackRight2 };
            PlayerAnimationState[] leftStates = { PlayerAnimationState.AttackLeft1, PlayerAnimationState.AttackLeft2 };
            for (int i = 0; i < actions.Length; i++)
                if (Consume(actions[i])) return facingRight ? rightStates[i] : leftStates[i];
            return null;
        }

        public void Clear()
        {
            held.Clear();
            pressed.Clear();
        }
    }

    // 接地オフセット: スプライトがセル中央付近に描かれていて足元に透明余白がある場合に、
    // 描画位置を縦にずらして足を足場へ接地させる。画像ピクセル単位の値を表示サイズへ換算する。
    internal static class GroundContact
    {
        public const int MinOffset = -128;
        public const int MaxOffset = 128;

        public static float ToSceneOffset(int offsetPixels, int imageHeight, Size displaySize)
        {
            if (imageHeight <= 0 || displaySize.Height <= 0) return 0;
            int clamped = Math.Max(MinOffset, Math.Min(MaxOffset, offsetPixels));
            return clamped * (displaySize.Height / (float)imageHeight);
        }
    }

    // キャラクターの当たり判定の箱（コライダー）。大きさは画像ピクセルで指定し、画像の中心に置く。
    // 0 の軸は従来どおりの自動（幅は画像の半分、高さは画像と同じ）。
    internal static class ColliderSize
    {
        public const int MaxPixels = 4096;

        public static SizeF ToScene(int widthPixels, int heightPixels, Size imageSize, Size displaySize)
        {
            float scaleX = imageSize.Width > 0 ? displaySize.Width / (float)imageSize.Width : 1f;
            float scaleY = imageSize.Height > 0 ? displaySize.Height / (float)imageSize.Height : 1f;
            float width = widthPixels > 0 ? Math.Min(MaxPixels, widthPixels) * scaleX : displaySize.Width * 0.5f;
            float height = heightPixels > 0 ? Math.Min(MaxPixels, heightPixels) * scaleY : displaySize.Height;
            return new SizeF(Math.Max(1f, width), Math.Max(1f, height));
        }

        public static SizeF Automatic(Size displaySize)
        {
            return new SizeF(Math.Max(1f, displaySize.Width * 0.5f), Math.Max(1f, displaySize.Height));
        }
    }

    // プレビューの固定の見本地形（床・スロープ・直角の崖・すり抜けられる足場）。
    // 座標はシーン(640x360)の論理ピクセルで、値が小さいほど上。描画と当たり判定で同じものを使う。
    public sealed class Terrain
    {
        public const float GroundThicknessRatio = 0.12f;

        public readonly Size Scene;
        public readonly float GroundTop;      // 平らな地面の上面
        public readonly float SlopeStartX;    // スロープの登り始め
        public readonly float SlopeEndX;      // スロープの頂点＝直角の崖の位置
        public readonly float PeakTop;        // 崖の上端の高さ
        public readonly float PlatformLeft;   // 足場（下からはすり抜けられる）
        public readonly float PlatformRight;
        public readonly float PlatformTop;
        public readonly float PlatformBottom;

        public Terrain(Size scene)
        {
            Scene = scene;
            GroundTop = scene.Height - (float)Math.Round(scene.Height * GroundThicknessRatio);
            SlopeStartX = scene.Width * 0.53f;
            SlopeEndX = scene.Width * 0.74f;
            PeakTop = GroundTop - scene.Height * 0.26f;
            PlatformLeft = scene.Width * 0.16f;
            PlatformRight = scene.Width * 0.44f;
            PlatformTop = GroundTop - scene.Height * 0.30f;
            PlatformBottom = PlatformTop + scene.Height * 0.06f;
        }

        // x の位置の、固い地面（床・スロープ・崖の上）の上面。
        public float SurfaceAt(float x)
        {
            if (x < SlopeStartX || x >= SlopeEndX) return GroundTop;
            float t = (x - SlopeStartX) / (SlopeEndX - SlopeStartX);
            return GroundTop + (PeakTop - GroundTop) * t;
        }

        // 立つ高さは、コライダーの幅（中心±halfWidth）の真下で一番高い地面で決める。
        // 地面は「平ら → 上り坂 → 崖で落ちる」の折れ線なので、両端と崖の上端だけ見れば最も高い所が分かる。
        private float SolidSupport(float centerX, float halfWidth)
        {
            float left = centerX - halfWidth, right = centerX + halfWidth;
            float support = Math.Min(SurfaceAt(left), SurfaceAt(right));
            if (left < SlopeEndX && right >= SlopeEndX) support = Math.Min(support, PeakTop);   // 崖の縁に掛かっている
            return support;
        }

        // 直角の崖の壁: 低い側（右）から左へ入ろうとして、足が崖の上端より下にあるときは通れない。
        public bool BlockedByWall(float oldCenterX, float newCenterX, float halfWidth, float feetY)
        {
            return oldCenterX - halfWidth >= SlopeEndX && newCenterX - halfWidth < SlopeEndX && feetY > PeakTop + 1f;
        }

        private bool OverlapsPlatform(float centerX, float halfWidth)
        {
            return centerX + halfWidth >= PlatformLeft && centerX - halfWidth <= PlatformRight;
        }

        // 足元(feetY)から見た、立てる面の高さ。足場は足が上面より上にあるときだけ足場になる（下から上がれる）。
        public float SupportY(float centerX, float halfWidth, float feetY)
        {
            float support = SolidSupport(centerX, halfWidth);
            if (feetY <= PlatformTop + 1.5f && OverlapsPlatform(centerX, halfWidth)) support = Math.Min(support, PlatformTop);
            return support;
        }

        // 落下中に足元が oldFeet→newFeet へ動くときに、着地する面（無ければ newFeet より下の値）。
        public float LandingY(float centerX, float halfWidth, float oldFeet, float newFeet)
        {
            float landing = SolidSupport(centerX, halfWidth);
            if (oldFeet <= PlatformTop + 1.5f && newFeet >= PlatformTop && OverlapsPlatform(centerX, halfWidth))
                landing = Math.Min(landing, PlatformTop);
            return landing;
        }
    }

    internal sealed class PlayerStateController
    {
        private float verticalVelocity;
        private float attackRemaining;
        private PlayerAnimationState attackState = PlayerAnimationState.AttackRight1;
        private float landingY;
        private float jumpStartRemaining;
        private float jumpLandRemaining;
        private bool airborne;
        private bool facingRight = true;
        private bool jumpFacingRight = true;

        public PointF Position { get; private set; }

        // ジャンプ力の設定値（1.0 が基準）を、跳ぶ初速（画面の高さ/秒）へ換算する。設定値 1.0 で、見本の足場に
        // 乗れる高さまで跳べる（以前は 1.6 が同じ高さで、既定の 1.0 では足場に乗れなかった）。
        public const float JumpForceScale = 1.2f;

        public static float JumpForceFromSetting(float setting, float sceneHeight)
        {
            return setting * sceneHeight * JumpForceScale;
        }

        // 地面に立ったまま、攻撃・ジャンプの途中でもない状態（入力がなければ動かない）。
        public bool IsResting
        {
            get
            {
                return !airborne && verticalVelocity == 0 && attackRemaining <= 0 &&
                    jumpStartRemaining <= 0 && jumpLandRemaining <= 0;
            }
        }

        public PlayerAnimationState State { get; private set; }
        public PlayerAnimationState MovementState { get; private set; }
        public bool FacingRight { get { return facingRight; } }

        // 地形（足場・スロープ・崖）を使うか。null なら従来の平らな床だけ（上下キーで奥行き移動できる）。
        private Terrain terrain;
        private bool terrainPoseReady;
        private float terrainCenterX, terrainFeetY;
        public Terrain Terrain
        {
            get { return terrain; }
            set { terrain = value; terrainPoseReady = false; }
        }

        // テストで、足元の位置を直接指定する。
        internal void SetPoseForTests(float centerX, float feetY, Size spriteSize)
        {
            terrainCenterX = centerX;
            terrainFeetY = feetY;
            terrainPoseReady = true;
            airborne = false;
            verticalVelocity = 0;
            Position = new PointF(centerX - spriteSize.Width * 0.5f, feetY - spriteSize.Height);
        }

        public PlayerStateController()
        {
            Reset(new Size(640, 360), new Size(64, 64));
        }

        public void Reset(Size sceneSize, Size spriteSize)
        {
            Position = new PointF(
                Math.Max(0, (sceneSize.Width - spriteSize.Width) * 0.5f),
                Math.Max(0, sceneSize.Height - spriteSize.Height - (float)Math.Round(sceneSize.Height * Terrain.GroundThicknessRatio)));
            terrainPoseReady = false;
            verticalVelocity = 0;
            attackRemaining = 0;
            airborne = false;
            landingY = Position.Y;
            State = PlayerAnimationState.Idle;
            jumpStartRemaining = 0;
            jumpLandRemaining = 0;
            facingRight = true;
            jumpFacingRight = true;
        }

        public void Update(float seconds, PreviewInputController input, Size sceneSize,
            Size spriteSize, Func<PlayerAnimationState, float> durationProvider,
            float movementSpeed, float jumpForce, float gravity)
        {
            Update(seconds, input, sceneSize, spriteSize, ColliderSize.Automatic(spriteSize), durationProvider, movementSpeed, jumpForce, gravity);
        }

        // collider: 当たり判定の箱（シーン座標）。画像の中心に置き、その下端を足元として扱う。
        public void Update(float seconds, PreviewInputController input, Size sceneSize,
            Size spriteSize, SizeF collider, Func<PlayerAnimationState, float> durationProvider,
            float movementSpeed, float jumpForce, float gravity)
        {
            seconds = Math.Max(0, Math.Min(0.1f, seconds));
            movementSpeed = Math.Max(0, movementSpeed);
            jumpForce = Math.Max(1, jumpForce);
            gravity = Math.Max(1, gravity);
            if (terrain != null)
            {
                UpdateOnTerrain(seconds, input, sceneSize, spriteSize, collider, durationProvider, movementSpeed, jumpForce, gravity);
                return;
            }
            int platformHeight = Math.Max(1, (int)Math.Round(sceneSize.Height * Terrain.GroundThicknessRatio));
            // コライダーの下端が床に着く位置（画像はコライダーの中心に合わせて置く）
            float groundY = Math.Max(0, sceneSize.Height - platformHeight - collider.Height * 0.5f - spriteSize.Height * 0.5f);
            PointF next = Position;

            if (input.Left != input.Right)
            {
                facingRight = input.Right;
                next.X += (input.Right ? movementSpeed : -movementSpeed) * seconds;
            }

            PlayerAnimationState? requestedAttack = input.ConsumeAttackState(facingRight);
            if (requestedAttack.HasValue)
            {
                attackState = requestedAttack.Value;
                attackRemaining = Math.Max(0.08f, durationProvider(attackState));
            }
            if (input.Consume(PreviewAction.Jump) && !airborne)
            {
                airborne = true;
                jumpFacingRight = facingRight;
                landingY = Position.Y;
                verticalVelocity = -jumpForce;
                jumpStartRemaining = Math.Max(0.06f, durationProvider(jumpFacingRight
                    ? PlayerAnimationState.JumpRightStart : PlayerAnimationState.JumpLeftStart));
            }

            if (input.Up != input.Down && !airborne)
                next.Y += (input.Down ? movementSpeed : -movementSpeed) * seconds;

            if (airborne)
            {
                verticalVelocity += gravity * seconds;
                next.Y += verticalVelocity * seconds;
                if (next.Y >= landingY)
                {
                    next.Y = landingY;
                    verticalVelocity = 0;
                    airborne = false;
                    jumpLandRemaining = Math.Max(0.06f, durationProvider(jumpFacingRight
                        ? PlayerAnimationState.JumpRightLand : PlayerAnimationState.JumpLeftLand));
                }
            }

            next.X = Math.Max(0, Math.Min(Math.Max(0, sceneSize.Width - spriteSize.Width), next.X));
            next.Y = Math.Max(0, Math.Min(groundY, next.Y));
            Position = next;

            ResolveState(seconds, input);
        }

        // 攻撃・ジャンプ・移動・待機のうち、今の見た目の状態を決める。
        private void ResolveState(float seconds, PreviewInputController input)
        {
            if (attackRemaining > 0)
            {
                attackRemaining -= seconds;
                State = attackState;
            }
            else if (airborne)
            {
                if (jumpStartRemaining > 0)
                {
                    jumpStartRemaining -= seconds;
                    State = jumpFacingRight ? PlayerAnimationState.JumpRightStart : PlayerAnimationState.JumpLeftStart;
                }
                else State = jumpFacingRight ? PlayerAnimationState.JumpRightAir : PlayerAnimationState.JumpLeftAir;
            }
            else if (jumpLandRemaining > 0)
            {
                jumpLandRemaining -= seconds;
                State = jumpFacingRight ? PlayerAnimationState.JumpRightLand : PlayerAnimationState.JumpLeftLand;
            }
            else if (input.Left != input.Right)
                State = input.Right ? PlayerAnimationState.MoveRight : PlayerAnimationState.MoveLeft;
            else if (input.Up != input.Down)
                State = input.Up ? PlayerAnimationState.MoveUp : PlayerAnimationState.MoveDown;
            else
                State = PlayerAnimationState.Idle;

            MovementState = input.Left != input.Right
                ? (input.Right ? PlayerAnimationState.MoveRight : PlayerAnimationState.MoveLeft)
                : input.Up != input.Down
                    ? (input.Up ? PlayerAnimationState.MoveUp : PlayerAnimationState.MoveDown)
                    : PlayerAnimationState.Idle;
        }

        // 固い地形の上で動く（横から見た物理）。足元(中心x, 足のy)を基準に、スロープは登り、
        // 直角の崖は横から通れず、崖の縁を越えると落ちる。足場は下から上がれて、上に乗れる。
        private void UpdateOnTerrain(float seconds, PreviewInputController input, Size sceneSize, Size spriteSize, SizeF collider,
            Func<PlayerAnimationState, float> durationProvider, float movementSpeed, float jumpForce, float gravity)
        {
            // 足元＝コライダーの下端。画像の中心とコライダーの中心は同じ位置。
            float centerToFeet = collider.Height * 0.5f;
            if (!terrainPoseReady)
            {
                terrainCenterX = Position.X + spriteSize.Width * 0.5f;
                terrainFeetY = Position.Y + spriteSize.Height * 0.5f + centerToFeet;
                terrainPoseReady = true;
            }
            float halfWidth = collider.Width * 0.5f;
            float cx = terrainCenterX, feet = terrainFeetY;

            float dx = 0;
            if (input.Left != input.Right)
            {
                facingRight = input.Right;
                dx = (input.Right ? movementSpeed : -movementSpeed) * seconds;
            }

            PlayerAnimationState? requestedAttack = input.ConsumeAttackState(facingRight);
            if (requestedAttack.HasValue)
            {
                attackState = requestedAttack.Value;
                attackRemaining = Math.Max(0.08f, durationProvider(attackState));
            }
            if (input.Consume(PreviewAction.Jump) && !airborne)
            {
                airborne = true;
                jumpFacingRight = facingRight;
                verticalVelocity = -jumpForce;
                jumpStartRemaining = Math.Max(0.06f, durationProvider(jumpFacingRight
                    ? PlayerAnimationState.JumpRightStart : PlayerAnimationState.JumpLeftStart));
            }

            if (dx != 0)
            {
                float minX = spriteSize.Width * 0.5f;
                float maxX = Math.Max(minX, sceneSize.Width - spriteSize.Width * 0.5f);
                float newCx = Math.Max(minX, Math.Min(maxX, cx + dx));
                float support = terrain.SupportY(newCx, halfWidth, feet);
                float tolerance = Math.Abs(newCx - cx) * 1.2f + 2f;
                if (terrain.BlockedByWall(cx, newCx, halfWidth, feet)) { }      // 壁: 横へは進めない
                else if (airborne)
                {
                    if (support >= feet - 1.5f) cx = newCx;             // 空中: 高い地形（崖の壁）には入れない
                }
                else if (feet - support <= tolerance)                    // 地上: 登れる傾きまで。壁は止まる
                {
                    cx = newCx;
                    if (support - feet > tolerance) { airborne = true; verticalVelocity = 0; }
                    else feet = support;
                }
            }

            if (airborne)
            {
                verticalVelocity = Math.Min(verticalVelocity + gravity * seconds, 2400f);
                float newFeet = feet + verticalVelocity * seconds;
                if (verticalVelocity >= 0)
                {
                    float landing = terrain.LandingY(cx, halfWidth, feet, newFeet);
                    if (newFeet >= landing)
                    {
                        feet = landing;
                        airborne = false;
                        verticalVelocity = 0;
                        jumpLandRemaining = Math.Max(0.06f, durationProvider(jumpFacingRight
                            ? PlayerAnimationState.JumpRightLand : PlayerAnimationState.JumpLeftLand));
                    }
                    else feet = newFeet;
                }
                else feet = newFeet;
                if (feet - collider.Height < 0)                          // 画面の上端で頭を打つ
                {
                    feet = collider.Height;
                    if (verticalVelocity < 0) verticalVelocity = 0;
                }
            }
            else
            {
                float support = terrain.SupportY(cx, halfWidth, feet);
                if (support > feet + 0.5f) { airborne = true; verticalVelocity = 0; }
                else feet = support;
            }

            terrainCenterX = cx;
            terrainFeetY = feet;
            Position = new PointF(cx - spriteSize.Width * 0.5f, feet - centerToFeet - spriteSize.Height * 0.5f);
            ResolveState(seconds, input);
        }
    }

    internal sealed class SpriteAnimationController
    {
        private readonly Dictionary<PlayerAnimationState, AnimationClipSettings> clips;
        private PlayerAnimationState requestedState = PlayerAnimationState.Idle;
        private PlayerAnimationState playingState = PlayerAnimationState.Idle;
        private int currentCell = 1;
        private double accumulatedSeconds;

        public int CurrentCell { get { return currentCell; } }
        public PlayerAnimationState RequestedState { get { return requestedState; } }
        public PlayerAnimationState PlayingState { get { return playingState; } }
        public bool IsOppositeDirectionFallback
        {
            get
            {
                return (IsRightDirectional(requestedState) && IsLeftDirectional(playingState)) ||
                       (IsLeftDirectional(requestedState) && IsRightDirectional(playingState));
            }
        }

        public SpriteAnimationController(Dictionary<PlayerAnimationState, AnimationClipSettings> source)
        {
            clips = source;
        }

        public void Reset(PlayerAnimationState state, int maxCell)
        {
            requestedState = state;
            playingState = ResolveState(state, maxCell);
            AnimationClipSettings clip = GetValidClip(playingState, maxCell);
            currentCell = clip == null ? 1 : Math.Max(1, Math.Min(maxCell, clip.StartCell));
            accumulatedSeconds = 0;
        }

        // fallbackState: ジャンプ/攻撃のクリップが1つも設定されていないときに代わりに再生する状態
        // （移動していれば移動、していなければ待機）。設定が無いのに動きが止まって見えるのを防ぐ。
        public void Update(double seconds, PlayerAnimationState state, int maxCell, PlayerAnimationState? fallbackState = null)
        {
            if (fallbackState.HasValue && IsActionState(state) && NoActionClip(state, maxCell))
                state = fallbackState.Value;
            PlayerAnimationState resolved = ResolveState(state, maxCell);
            if (requestedState != state || playingState != resolved)
            {
                requestedState = state;
                playingState = resolved;
                AnimationClipSettings changed = GetValidClip(playingState, maxCell);
                currentCell = changed == null ? 1 : changed.StartCell;
                accumulatedSeconds = 0;
            }

            AnimationClipSettings clip = GetValidClip(playingState, maxCell);
            if (clip == null) return;
            accumulatedSeconds += Math.Max(0, seconds);
            double frameDuration = 1.0 / Math.Max(1, clip.Fps);
            while (accumulatedSeconds >= frameDuration)
            {
                accumulatedSeconds -= frameDuration;
                currentCell++;
                if (currentCell > clip.EndCell || currentCell > maxCell) currentCell = clip.StartCell;
            }
        }

        // 次にコマが切り替わるまでの秒数。クリップがなければ 0.1 秒。待機中に、コマが変わる瞬間だけ判定するために使う。
        public double GetSecondsToNextFrame(int maxCell)
        {
            AnimationClipSettings clip = GetValidClip(playingState, maxCell);
            if (clip == null) return 0.1;
            double frameDuration = 1.0 / Math.Max(1, clip.Fps);
            return Math.Max(0.001, frameDuration - accumulatedSeconds);
        }

        public float GetDuration(PlayerAnimationState state, int maxCell)
        {
            AnimationClipSettings clip = GetValidClip(ResolveState(state, maxCell), maxCell);
            if (clip == null) return 0.1f;
            return Math.Max(0.08f, (clip.EndCell - clip.StartCell + 1) / (float)Math.Max(1, clip.Fps));
        }

        private PlayerAnimationState ResolveState(PlayerAnimationState requested, int maxCell)
        {
            PlayerAnimationState[] candidates;
            switch (requested)
            {
                case PlayerAnimationState.MoveLeft:
                    candidates = new[] { PlayerAnimationState.MoveLeft, PlayerAnimationState.MoveRight,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.MoveRight:
                    candidates = new[] { PlayerAnimationState.MoveRight, PlayerAnimationState.MoveLeft,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.MoveUp:
                    candidates = new[] { PlayerAnimationState.MoveUp, PlayerAnimationState.MoveRight,
                        PlayerAnimationState.MoveLeft, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.MoveDown:
                    candidates = new[] { PlayerAnimationState.MoveDown, PlayerAnimationState.MoveRight,
                        PlayerAnimationState.MoveLeft, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpRightStart:
                    candidates = new[] { PlayerAnimationState.JumpRightStart, PlayerAnimationState.JumpRightAir,
                        PlayerAnimationState.JumpRightLand, PlayerAnimationState.JumpLeftStart,
                        PlayerAnimationState.JumpLeftAir, PlayerAnimationState.JumpLeftLand,
                        PlayerAnimationState.JumpStart, PlayerAnimationState.JumpAir, PlayerAnimationState.JumpLand,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpRightAir:
                    candidates = new[] { PlayerAnimationState.JumpRightAir, PlayerAnimationState.JumpRightStart,
                        PlayerAnimationState.JumpRightLand, PlayerAnimationState.JumpLeftAir,
                        PlayerAnimationState.JumpLeftStart, PlayerAnimationState.JumpLeftLand,
                        PlayerAnimationState.JumpAir, PlayerAnimationState.JumpStart, PlayerAnimationState.JumpLand,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpRightLand:
                    candidates = new[] { PlayerAnimationState.JumpRightLand, PlayerAnimationState.JumpRightAir,
                        PlayerAnimationState.JumpRightStart, PlayerAnimationState.JumpLeftLand,
                        PlayerAnimationState.JumpLeftAir, PlayerAnimationState.JumpLeftStart,
                        PlayerAnimationState.JumpLand, PlayerAnimationState.JumpAir, PlayerAnimationState.JumpStart,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpLeftStart:
                    candidates = new[] { PlayerAnimationState.JumpLeftStart, PlayerAnimationState.JumpLeftAir,
                        PlayerAnimationState.JumpLeftLand, PlayerAnimationState.JumpRightStart,
                        PlayerAnimationState.JumpRightAir, PlayerAnimationState.JumpRightLand,
                        PlayerAnimationState.JumpStart, PlayerAnimationState.JumpAir, PlayerAnimationState.JumpLand,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpLeftAir:
                    candidates = new[] { PlayerAnimationState.JumpLeftAir, PlayerAnimationState.JumpLeftStart,
                        PlayerAnimationState.JumpLeftLand, PlayerAnimationState.JumpRightAir,
                        PlayerAnimationState.JumpRightStart, PlayerAnimationState.JumpRightLand,
                        PlayerAnimationState.JumpAir, PlayerAnimationState.JumpStart, PlayerAnimationState.JumpLand,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpLeftLand:
                    candidates = new[] { PlayerAnimationState.JumpLeftLand, PlayerAnimationState.JumpLeftAir,
                        PlayerAnimationState.JumpLeftStart, PlayerAnimationState.JumpRightLand,
                        PlayerAnimationState.JumpRightAir, PlayerAnimationState.JumpRightStart,
                        PlayerAnimationState.JumpLand, PlayerAnimationState.JumpAir, PlayerAnimationState.JumpStart,
                        PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.Jump:
                    candidates = new[] { PlayerAnimationState.Jump, PlayerAnimationState.JumpAir,
                        PlayerAnimationState.JumpStart, PlayerAnimationState.JumpLand,
                        PlayerAnimationState.Move, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpStart:
                    candidates = new[] { PlayerAnimationState.JumpStart, PlayerAnimationState.JumpAir,
                        PlayerAnimationState.JumpLand, PlayerAnimationState.Jump, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpAir:
                    candidates = new[] { PlayerAnimationState.JumpAir, PlayerAnimationState.JumpStart,
                        PlayerAnimationState.JumpLand, PlayerAnimationState.Jump, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.JumpLand:
                    candidates = new[] { PlayerAnimationState.JumpLand, PlayerAnimationState.JumpAir,
                        PlayerAnimationState.JumpStart, PlayerAnimationState.Jump, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.AttackRight1:
                    candidates = new[] { PlayerAnimationState.AttackRight1, PlayerAnimationState.AttackLeft1,
                        PlayerAnimationState.AttackRight2, PlayerAnimationState.AttackLeft2, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.AttackRight2:
                    candidates = new[] { PlayerAnimationState.AttackRight2, PlayerAnimationState.AttackLeft2,
                        PlayerAnimationState.AttackRight1, PlayerAnimationState.AttackLeft1, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.AttackLeft1:
                    candidates = new[] { PlayerAnimationState.AttackLeft1, PlayerAnimationState.AttackRight1,
                        PlayerAnimationState.AttackLeft2, PlayerAnimationState.AttackRight2, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.AttackLeft2:
                    candidates = new[] { PlayerAnimationState.AttackLeft2, PlayerAnimationState.AttackRight2,
                        PlayerAnimationState.AttackLeft1, PlayerAnimationState.AttackRight1, PlayerAnimationState.Idle };
                    break;
                case PlayerAnimationState.Move:
                    candidates = new[] { PlayerAnimationState.MoveRight, PlayerAnimationState.MoveLeft,
                        PlayerAnimationState.Idle };
                    break;
                default:
                    candidates = new[] { PlayerAnimationState.Idle, PlayerAnimationState.MoveRight,
                        PlayerAnimationState.MoveLeft };
                    break;
            }

            foreach (PlayerAnimationState candidate in candidates)
                if (GetValidClip(candidate, maxCell) != null) return candidate;
            return requested;
        }

        // そのジャンプ/攻撃に使えるクリップ（代替候補を含む）が1つも無い。
        private bool NoActionClip(PlayerAnimationState state, int maxCell)
        {
            PlayerAnimationState resolved = ResolveState(state, maxCell);
            return !IsActionState(resolved) || GetValidClip(resolved, maxCell) == null;
        }

        private static bool IsActionState(PlayerAnimationState state)
        {
            switch (state)
            {
                case PlayerAnimationState.Jump:
                case PlayerAnimationState.JumpStart:
                case PlayerAnimationState.JumpAir:
                case PlayerAnimationState.JumpLand:
                case PlayerAnimationState.JumpRightStart:
                case PlayerAnimationState.JumpRightAir:
                case PlayerAnimationState.JumpRightLand:
                case PlayerAnimationState.JumpLeftStart:
                case PlayerAnimationState.JumpLeftAir:
                case PlayerAnimationState.JumpLeftLand:
                case PlayerAnimationState.AttackRight1:
                case PlayerAnimationState.AttackRight2:
                case PlayerAnimationState.AttackLeft1:
                case PlayerAnimationState.AttackLeft2:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsRightDirectional(PlayerAnimationState state)
        {
            return state == PlayerAnimationState.MoveRight || state == PlayerAnimationState.JumpRightStart ||
                   state == PlayerAnimationState.JumpRightAir || state == PlayerAnimationState.JumpRightLand ||
                   state == PlayerAnimationState.AttackRight1 || state == PlayerAnimationState.AttackRight2;
        }

        private static bool IsLeftDirectional(PlayerAnimationState state)
        {
            return state == PlayerAnimationState.MoveLeft || state == PlayerAnimationState.JumpLeftStart ||
                   state == PlayerAnimationState.JumpLeftAir || state == PlayerAnimationState.JumpLeftLand ||
                   state == PlayerAnimationState.AttackLeft1 || state == PlayerAnimationState.AttackLeft2;
        }

        public bool ShouldMirrorForFacing(bool facingRight)
        {
            if (IsOppositeDirectionFallback) return true;
            return !facingRight && !IsRightDirectional(playingState) && !IsLeftDirectional(playingState);
        }

        private AnimationClipSettings GetValidClip(PlayerAnimationState state, int maxCell)
        {
            AnimationClipSettings clip;
            if (!clips.TryGetValue(state, out clip) || clip == null || !clip.Enabled || maxCell <= 0) return null;
            int start = Math.Max(1, Math.Min(maxCell, clip.StartCell));
            int end = Math.Max(1, Math.Min(maxCell, clip.EndCell));
            if (start > end) return null;
            return new AnimationClipSettings { Enabled = true, StartCell = start, EndCell = end, Fps = clip.Fps };
        }
    }

    internal sealed class EffectMotionController
    {
        public PointF Position { get; private set; }

        public void Reset(Size sceneSize, Size spriteSize)
        {
            Position = new PointF(
                Math.Max(0, (sceneSize.Width - spriteSize.Width) * 0.5f),
                Math.Max(0, (sceneSize.Height - spriteSize.Height) * 0.5f));
        }

        public void Update(float seconds, PointF direction, float speed, Size sceneSize, Size spriteSize)
        {
            float length = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            if (length > 0.0001f)
            {
                direction.X /= length;
                direction.Y /= length;
            }
            PointF next = new PointF(Position.X + direction.X * speed * seconds,
                Position.Y + direction.Y * speed * seconds);
            if (next.X < -spriteSize.Width || next.X > sceneSize.Width ||
                next.Y < -spriteSize.Height || next.Y > sceneSize.Height)
                Reset(sceneSize, spriteSize);
            else Position = next;
        }
    }
}
