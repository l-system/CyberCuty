using System.Numerics;

namespace CyberCity.App;

/// <summary>
/// Port of camera.py's Camera class: FPS-style movement with acceleration/friction,
/// a velocity-based "banking" roll effect, and mouse look. Collision detection
/// re-implements the raymarching shader's scene SDF on the CPU (floor + a repeating
/// grid of boxes) so the camera can't walk through the buildings it's flying past.
/// </summary>
public sealed class Camera
{
    // === Orientation basis ===
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public Vector3 Front { get; private set; } = new(0f, 0f, -1f);
    public Vector3 Up { get; private set; } = new(0f, 1f, 0f);
    public Vector3 Right { get; private set; } = new(1f, 0f, 0f);
    public readonly Vector3 WorldUp = new(0f, 1f, 0f);
    public float Fov { get; } = 65f;

    // === Movement ===
    public float MaxSpeed { get; } = 3.0f;
    public float MaxRunSpeed { get; } = 10.0f;
    public float AccelerationRate { get; } = 0.2f;
    public float Friction { get; } = 0.8f;

    public Vector3 Velocity { get; private set; } = Vector3.Zero;
    private Vector3 _movementInput = Vector3.Zero;
    public bool IsRunning { get; set; }

    public float MouseSensitivity { get; } = 0.05f;

    public float MaxPitch { get; } = 85.0f;
    public float MinPitch { get; } = -85.0f;

    // === Roll easing ===
    public float RollSensitivity { get; } = 1.0f;
    public float MaxRoll { get; } = 25.0f;
    public float RollDamping { get; } = 10.0f;
    public float RollSmoothing { get; } = 0.2f;
    public float SpeedRollFactor { get; } = 0.8f;
    public int HistorySize { get; } = 5;

    private float _targetRoll;
    public float CurrentRoll { get; private set; }
    private float _rollVelocity;
    private float _lastYaw;
    private readonly List<float> _yawHistory = new();

    // === Collision ===
    public bool CollisionEnabled { get; } = true;
    public float CollisionRadius { get; } = 0.25f;
    public float CollisionHeight { get; } = 0.25f;
    public float CollisionStepSize { get; } = 0.01f;
    public float MinFloorHeight { get; } = 0.05f;
    public int MaxCollisionIterations { get; } = 50;

    private const float CollisionThreshold = -0.01f;
    private const float BoxRounding = 0.01f;
    private const float BoxCollisionTolerance = 0.02f;

    public Camera(Vector3? position = null, float yaw = -135.0f, float pitch = -20.0f)
    {
        Position = position ?? new Vector3(4.0f, 3.0f, 4.0f);
        Yaw = yaw;
        Pitch = pitch;
        _lastYaw = Yaw;
        UpdateVectors();
    }

    // --- Scene SDF (mirrors the shader's distance functions) ---------------

    public static float GetDistanceToFloor(Vector3 position) => position.Y;

    /// <summary>
    /// Rounded-box SDF matching the shader's building distance function, plus a
    /// small collision tolerance so the camera doesn't catch on rounded fillets.
    /// </summary>
    public static float GetDistanceToBox(Vector3 position, Vector3 boxCenter, Vector3 boxRadius)
    {
        Vector3 delta = Vector3.Abs(position - boxCenter) - boxRadius;

        float maxComponent = MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z));
        float insideDistance = MathF.Min(maxComponent, 0f);
        float outsideDistance = Vector3.Max(delta, Vector3.Zero).Length();

        float distance = insideDistance + outsideDistance - BoxRounding;
        return distance + BoxCollisionTolerance;
    }

    public static float GetDistanceToBuildings(Vector3 position)
    {
        Vector3 singleBoxRadius = new(0.98f, 3.0f, 0.98f);
        const float effectiveBlockSize = 6.0f;
        const float offsetToCenterInBlackSquare = 1.0f;

        // NOTE: the Python source also computes a grid_coord_xz here (floor(pos.xz / blockSize))
        // but never uses it afterward — dead code in the original, intentionally not carried over.

        Vector3 localPosition = new(
            FloorMod(position.X - offsetToCenterInBlackSquare + 0.5f * effectiveBlockSize, effectiveBlockSize) - 0.5f * effectiveBlockSize,
            position.Y - singleBoxRadius.Y,
            FloorMod(position.Z - offsetToCenterInBlackSquare + 0.5f * effectiveBlockSize, effectiveBlockSize) - 0.5f * effectiveBlockSize);

        return GetDistanceToBox(localPosition, Vector3.Zero, singleBoxRadius);
    }

    public static float GetSceneDistance(Vector3 position) =>
        MathF.Min(GetDistanceToFloor(position), GetDistanceToBuildings(position));

    /// <summary>Python's %, which floor-divides (always non-negative for positive b) — unlike C#'s %.</summary>
    private static float FloorMod(float a, float b) => a - b * MathF.Floor(a / b);

    public static Vector3 GetCollisionNormal(Vector3 position, float epsilon = 0.0005f)
    {
        Vector3 dx = new(epsilon, 0f, 0f);
        Vector3 dy = new(0f, epsilon, 0f);
        Vector3 dz = new(0f, 0f, epsilon);

        Vector3 gradient = new(
            GetSceneDistance(position + dx) - GetSceneDistance(position - dx),
            GetSceneDistance(position + dy) - GetSceneDistance(position - dy),
            GetSceneDistance(position + dz) - GetSceneDistance(position - dz));

        return SafeNormalize(gradient, fallback: new Vector3(0f, 1f, 0f));
    }

    // --- Collision resolution -------------------------------------------------

    public (Vector3 Position, Vector3 Velocity) ResolveCollision(Vector3 newPosition, Vector3 velocity)
    {
        if (!CollisionEnabled)
        {
            return (newPosition, velocity);
        }

        Span<Vector3> offsets = stackalloc Vector3[]
        {
            Vector3.Zero,
            new Vector3(CollisionRadius, 0, 0),
            new Vector3(-CollisionRadius, 0, 0),
            new Vector3(0, 0, CollisionRadius),
            new Vector3(0, 0, -CollisionRadius),
            new Vector3(CollisionRadius * 0.707f, 0, CollisionRadius * 0.707f),
            new Vector3(-CollisionRadius * 0.707f, 0, CollisionRadius * 0.707f),
            new Vector3(CollisionRadius * 0.707f, 0, -CollisionRadius * 0.707f),
            new Vector3(-CollisionRadius * 0.707f, 0, -CollisionRadius * 0.707f),
            new Vector3(0, CollisionHeight * 0.5f, 0),
            new Vector3(0, -CollisionHeight * 0.5f, 0),
        };

        float minDistance = float.PositiveInfinity;
        Vector3 collisionPoint = newPosition;

        foreach (var offset in offsets)
        {
            Vector3 point = newPosition + offset;
            float distance = GetSceneDistance(point);
            if (distance < minDistance)
            {
                minDistance = distance;
                collisionPoint = point;
            }
        }

        if (minDistance >= CollisionThreshold)
        {
            return (newPosition, velocity);
        }

        Vector3 resolvedPosition = newPosition;
        Vector3 resolvedVelocity = velocity;

        // Fixed for the duration of the loop (depends only on CollisionRadius/CollisionHeight,
        // neither of which changes below) — hoisted out of the loop so the stackalloc below runs
        // once per call instead of once per iteration. A stackalloc inside a loop re-reserves
        // stack space on every pass without releasing the previous reservation until the method
        // returns, so a loop-bound allocation like this risked a stack overflow at high iteration
        // counts (flagged by CA2014).
        Span<Vector3> testOffsets = stackalloc Vector3[]
        {
            Vector3.Zero,
            new Vector3(CollisionRadius * 0.8f, 0, 0),
            new Vector3(-CollisionRadius * 0.8f, 0, 0),
            new Vector3(0, 0, CollisionRadius * 0.8f),
            new Vector3(0, 0, -CollisionRadius * 0.8f),
            new Vector3(0, CollisionHeight * 0.4f, 0),
            new Vector3(0, -CollisionHeight * 0.4f, 0),
        };

        for (int iteration = 0; iteration < MaxCollisionIterations; iteration++)
        {
            Vector3 normal = GetCollisionNormal(collisionPoint, epsilon: 0.0003f);

            float penetrationDepth = MathF.Abs(minDistance - CollisionThreshold);
            float baseStep = CollisionStepSize * 0.1f;
            float adaptiveStep = MathF.Max(baseStep, penetrationDepth * 0.5f);
            resolvedPosition += normal * adaptiveStep;

            float velocityNormalComponent = Vector3.Dot(resolvedVelocity, normal);
            if (velocityNormalComponent < 0)
            {
                resolvedVelocity -= velocityNormalComponent * normal;
                // Python multiplies by 1.0 here ("less damping than original slide_damping") — a no-op,
                // preserved as a comment rather than a pointless multiply.
            }

            float newMinDistance = float.PositiveInfinity;
            Vector3 newCollisionPoint = resolvedPosition;

            foreach (var offset in testOffsets)
            {
                Vector3 point = resolvedPosition + offset;
                float distance = GetSceneDistance(point);
                if (distance < newMinDistance)
                {
                    newMinDistance = distance;
                    newCollisionPoint = point;
                }
            }

            if (newMinDistance >= CollisionThreshold)
            {
                break;
            }

            minDistance = newMinDistance;
            collisionPoint = newCollisionPoint;
        }

        if (resolvedPosition.Y < MinFloorHeight)
        {
            resolvedPosition = new Vector3(resolvedPosition.X, MinFloorHeight, resolvedPosition.Z);
            if (resolvedVelocity.Y < 0)
            {
                resolvedVelocity = new Vector3(resolvedVelocity.X, 0f, resolvedVelocity.Z);
            }
        }

        return (resolvedPosition, resolvedVelocity);
    }

    // --- Orientation update ---------------------------------------------------

    public void UpdateVectors()
    {
        float yawRad = Yaw * MathF.PI / 180f;
        float pitchRad = Pitch * MathF.PI / 180f;

        Vector3 front = new(
            MathF.Cos(yawRad) * MathF.Cos(pitchRad),
            MathF.Sin(pitchRad),
            MathF.Sin(yawRad) * MathF.Cos(pitchRad));
        Front = SafeNormalize(front, fallback: new Vector3(0f, 0f, -1f));

        Vector3 rightNoRoll = SafeNormalize(Vector3.Cross(Front, WorldUp), fallback: new Vector3(1f, 0f, 0f));
        Vector3 upNoRoll = SafeNormalize(Vector3.Cross(rightNoRoll, Front), fallback: new Vector3(0f, 1f, 0f));

        // Rodrigues' rotation formula: rotate right/up around the front axis by the current roll.
        float rollRad = CurrentRoll * MathF.PI / 180f;
        Vector3 k = Front;
        float cosR = MathF.Cos(rollRad);
        float sinR = MathF.Sin(rollRad);

        Vector3 upRotated = upNoRoll * cosR + Vector3.Cross(k, upNoRoll) * sinR + k * Vector3.Dot(k, upNoRoll) * (1 - cosR);
        Vector3 rightRotated = rightNoRoll * cosR + Vector3.Cross(k, rightNoRoll) * sinR + k * Vector3.Dot(k, rightNoRoll) * (1 - cosR);

        Up = SafeNormalizeOrKeep(upRotated);
        Right = SafeNormalizeOrKeep(rightRotated);
    }

    // --- Movement input ---------------------------------------------------

    public void SetTargetMovementDirection(Vector3 directionVector)
    {
        _movementInput = directionVector.LengthSquared() > 0
            ? Vector3.Normalize(directionVector)
            : Vector3.Zero;
    }

    public void UpdateMovement(float deltaTime)
    {
        if (deltaTime <= 0)
        {
            return;
        }

        float currentMaxSpeed = IsRunning ? MaxRunSpeed : MaxSpeed;
        Vector3 targetVelocity = Vector3.Zero;

        if (_movementInput.LengthSquared() > 0)
        {
            // Movement is not tied to camera orientation — fixed world axes, matching the Python source.
            Vector3 worldForward = new(0f, 0f, 1f);
            Vector3 worldRight = new(1f, 0f, 0f);

            Vector3 worldMovementDirection =
                worldForward * _movementInput.Z +
                worldRight * _movementInput.X +
                WorldUp * _movementInput.Y;

            if (worldMovementDirection.LengthSquared() > 0)
            {
                worldMovementDirection = Vector3.Normalize(worldMovementDirection);
                targetVelocity = worldMovementDirection * currentMaxSpeed;
            }
        }

        if (targetVelocity.LengthSquared() > 0)
        {
            Velocity += (targetVelocity - Velocity) * AccelerationRate * deltaTime;
        }
        else
        {
            Velocity *= MathF.Max(0f, 1 - Friction * deltaTime);
        }

        Vector3 newPosition = Position + Velocity * deltaTime;
        (Position, var resolvedVelocity) = ResolveCollision(newPosition, Velocity);
        Velocity = resolvedVelocity;
    }

    public void UpdateRoll(float deltaTime)
    {
        if (deltaTime <= 0)
        {
            return;
        }

        float yawDelta = Yaw - _lastYaw;
        if (yawDelta > 180) yawDelta -= 360;
        else if (yawDelta < -180) yawDelta += 360;

        float instantaneousYawVelocity = yawDelta / deltaTime;

        _yawHistory.Add(instantaneousYawVelocity);
        if (_yawHistory.Count > HistorySize)
        {
            _yawHistory.RemoveAt(0);
        }

        float smoothedYawVelocity = _yawHistory.Count > 0 ? _yawHistory.Average() : 0f;

        float currentSpeedMagnitude = Velocity.Length();
        float maxPossibleSpeed = MaxRunSpeed;
        float speedFactor = maxPossibleSpeed > 0 ? MathF.Min(currentSpeedMagnitude / maxPossibleSpeed, 1.0f) : 0f;

        float baseRoll = smoothedYawVelocity * RollSensitivity;

        const float minSpeedThreshold = 0.1f;
        float speedRollMultiplier = currentSpeedMagnitude < minSpeedThreshold
            ? 0f
            : speedFactor * (1.0f + SpeedRollFactor);

        float speedInfluencedRoll = baseRoll * speedRollMultiplier;
        _targetRoll = Math.Clamp(speedInfluencedRoll, -MaxRoll, MaxRoll);

        float rollError = _targetRoll - CurrentRoll;
        _rollVelocity += rollError * RollDamping * deltaTime;
        _rollVelocity *= 1.0f - RollSmoothing;
        CurrentRoll += _rollVelocity * deltaTime;

        _lastYaw = Yaw;
    }

    // --- Mouse look ---------------------------------------------------

    public void ProcessMouseMovement(float xOffset, float yOffset, bool constrainPitch = true)
    {
        xOffset *= MouseSensitivity;
        yOffset *= MouseSensitivity;

        Yaw += xOffset;
        Pitch += yOffset;

        Yaw %= 360.0f;
        if (Yaw < 0) Yaw += 360.0f;

        if (constrainPitch)
        {
            Pitch = Math.Clamp(Pitch, MinPitch, MaxPitch);
        }

        UpdateVectors();
    }

    public (Vector3 Position, Vector3 Target, Vector3 Up) GetViewMatrix() => (Position, Position + Front, Up);

    // --- Helpers ---------------------------------------------------

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback) =>
        v.LengthSquared() > 0 ? Vector3.Normalize(v) : fallback;

    /// <summary>
    /// Mirrors the Python source's "if norm != 0: v = v/norm" — i.e. leave the vector
    /// untouched (not swapped for a fallback) when it's already zero-length.
    /// </summary>
    private static Vector3 SafeNormalizeOrKeep(Vector3 v) =>
        v.LengthSquared() > 0 ? Vector3.Normalize(v) : v;
}