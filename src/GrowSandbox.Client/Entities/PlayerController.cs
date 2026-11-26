using GrowSandbox.Client.Network;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace GrowSandbox.Client.Entities;

/// <summary>
/// Pengendali karakter pemain lokal dengan Client-Side Prediction berbasis <see cref="TileCollision"/>
/// (gerak A/D, lompat Space), state animasi jalan/lompat/punch, serta pengiriman
/// <see cref="PlayerStatePacket"/> ke server 20 kali/detik melalui <c>DeliveryMethod.Sequenced</c>.
/// </summary>
public sealed class PlayerController
{
    private const float ReconciliationSnapThresholdSq = 4f * 4f;
    public const float PunchAnimationDuration = 0.14f;

    private float _stateSendAccumulator;
    private bool _jumpWasPressed;

    public float PositionX { get; private set; }
    public float PositionY { get; private set; }
    public float VelocityX { get; private set; }
    public float VelocityY { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool FacingLeft { get; private set; }
    public uint TickSequence { get; private set; }
    public bool IsSpawned { get; private set; }

    public float WalkAnimTime { get; private set; }
    public float PunchAnimTimer { get; private set; }
    public bool IsPunching => PunchAnimTimer > 0f;
    public float PunchProgress => IsPunching ? 1f - (PunchAnimTimer / PunchAnimationDuration) : 0f;
    public bool JustJumped { get; private set; }
    public bool JustLanded { get; private set; }

    public Vector2 Position => new(PositionX, PositionY);

    public Vector2 CenterPosition => new(
        PositionX + PhysicsConstants.PlayerWidth * 0.5f,
        PositionY + PhysicsConstants.PlayerHeight * 0.5f);

    public Vector2 FeetPosition => new(
        PositionX + PhysicsConstants.PlayerWidth * 0.5f,
        PositionY + PhysicsConstants.PlayerHeight);

    public void Reset()
    {
        PositionX = 0f;
        PositionY = 0f;
        VelocityX = 0f;
        VelocityY = 0f;
        IsGrounded = false;
        FacingLeft = false;
        _stateSendAccumulator = 0f;
        _jumpWasPressed = false;
        WalkAnimTime = 0f;
        PunchAnimTimer = 0f;
        JustJumped = false;
        JustLanded = false;
        IsSpawned = false;
    }

    public void SpawnAtTile(int spawnTileX, int spawnTileY)
    {
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(spawnTileX, spawnTileY);
        PositionX = spawnX;
        PositionY = spawnY;
        VelocityX = 0f;
        VelocityY = 0f;
        IsGrounded = true;
        FacingLeft = false;
        _stateSendAccumulator = 0f;
        _jumpWasPressed = false;
        WalkAnimTime = 0f;
        PunchAnimTimer = 0f;
        IsSpawned = true;
    }

    public void TriggerPunch(int targetTileX)
    {
        PunchAnimTimer = PunchAnimationDuration;
        float targetCenterX = (targetTileX + 0.5f) * WorldConstants.TileSize;
        FacingLeft = targetCenterX < CenterPosition.X;
    }

    public void ApplyServerCorrection(PlayerStatePacket authoritativeState)
    {
        float dx = authoritativeState.PositionX - PositionX;
        float dy = authoritativeState.PositionY - PositionY;
        if ((dx * dx) + (dy * dy) >= ReconciliationSnapThresholdSq)
        {
            PositionX = authoritativeState.PositionX;
            PositionY = authoritativeState.PositionY;
            VelocityX = authoritativeState.VelocityX;
            VelocityY = authoritativeState.VelocityY;
            IsGrounded = authoritativeState.IsGrounded;
            FacingLeft = authoritativeState.FacingLeft;
        }
    }

    public void Update(float dt, KeyboardState keyboard, Tile[] tiles, GameClient gameClient, bool inputBlocked = false)
    {
        JustJumped = false;
        JustLanded = false;

        if (!IsSpawned || !gameClient.HasWorldSnapshot)
        {
            return;
        }

        bool wasGrounded = IsGrounded;
        float preStepVelY = VelocityY;

        if (PunchAnimTimer > 0f)
        {
            PunchAnimTimer = MathF.Max(0f, PunchAnimTimer - dt);
        }

        // 1. Baca input horizontal (A / D) dan lompat (Space) bila tidak sedang mengetik chat.
        float moveAxis = 0f;
        if (!inputBlocked)
        {
            if (keyboard.IsKeyDown(Keys.A) || keyboard.IsKeyDown(Keys.Left))
            {
                moveAxis -= 1f;
            }

            if (keyboard.IsKeyDown(Keys.D) || keyboard.IsKeyDown(Keys.Right))
            {
                moveAxis += 1f;
            }
        }

        VelocityX = moveAxis * PhysicsConstants.MoveSpeed;
        if (moveAxis < 0f)
        {
            FacingLeft = true;
        }
        else if (moveAxis > 0f)
        {
            FacingLeft = false;
        }

        bool jumpPressed = !inputBlocked && keyboard.IsKeyDown(Keys.Space);
        if (jumpPressed && !_jumpWasPressed && IsGrounded)
        {
            VelocityY = -PhysicsConstants.JumpForce;
            IsGrounded = false;
            JustJumped = true;
        }

        _jumpWasPressed = jumpPressed;

        // 2. Client-Side Prediction: gravitasi + resolusi tabrakan AABB 2D
        VelocityY = MathF.Min(VelocityY + PhysicsConstants.Gravity * dt, PhysicsConstants.MaxFallSpeed);

        CollisionResolutionResult result = TileCollision.MoveAndSlide(
            tiles,
            PositionX,
            PositionY,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            VelocityX,
            VelocityY,
            dt);

        PositionX = result.X;
        PositionY = result.Y;
        VelocityX = result.VelocityX;
        VelocityY = result.VelocityY;
        IsGrounded = result.IsGrounded;
        TickSequence++;

        if (!wasGrounded && IsGrounded && preStepVelY > 90f)
        {
            JustLanded = true;
        }

        if (IsGrounded && MathF.Abs(VelocityX) > 10f)
        {
            WalkAnimTime += dt * 10f;
        }
        else
        {
            WalkAnimTime = 0f;
        }

        // 3. Kirim PlayerState ke server 20 kali per detik menggunakan DeliveryMethod.Sequenced
        _stateSendAccumulator += dt;
        if (_stateSendAccumulator >= PhysicsConstants.ClientStateSendInterval)
        {
            _stateSendAccumulator %= PhysicsConstants.ClientStateSendInterval;

            var statePacket = new PlayerStatePacket(
                PlayerId: gameClient.LocalPlayerId,
                Tick: TickSequence,
                PositionX: PositionX,
                PositionY: PositionY,
                VelocityX: VelocityX,
                VelocityY: VelocityY,
                IsGrounded: IsGrounded,
                FacingLeft: FacingLeft);

            gameClient.SendPlayerState(in statePacket);
        }
    }
}
